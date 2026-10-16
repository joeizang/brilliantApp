using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Brilliant.Code.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Brilliant.App;

/// <summary>Throwaway #138 experiment. No part of this harness is intended for the production branch.</summary>
public static class RoslynSpike
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
    {
        using var stream = typeof(RoslynSpike).Assembly.GetManifestResourceStream("probe-runtime.dll")
            ?? throw new InvalidOperationException("probe-runtime.dll metadata resource missing");
        return Basic.Reference.Assemblies.Net100.References.All.Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromStream(stream)).ToArray();
    });

    public static string Run()
    {
        var report = new Dictionary<string, object?>
        {
            ["utc"] = DateTimeOffset.UtcNow,
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["mono"] = Type.GetType("Mono.Runtime") is not null,
            ["dynamicCodeSupported"] = RuntimeFeature.IsDynamicCodeSupported,
            ["dynamicCodeCompiled"] = RuntimeFeature.IsDynamicCodeCompiled,
            ["configuration"] = typeof(RoslynSpike).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            ["stackBytes"] = ProbeBudget.DefaultStackSize,
            ["maxDepth"] = 128,
            ["referenceFramework"] = "net10.0", ["languageVersion"] = "14", ["roslyn"] = "5.0.0",
        };
        try
        {
            Progress("cold compile");
            const string loop = "public static class Snippet { public static long Run() { long sum=0; for(int i=0;i<10000;i++) sum+=i; return sum; } }";
            var cold = Compile(loop);
            report["coldCompileMs"] = cold.Milliseconds;
            report["firstLoadAndRun"] = MeasureExecution(cold.Bytes);
            var warm = new List<double>();
            for (var i = 0; i < 5; i++) warm.Add(Compile(loop).Milliseconds);
            report["warmCompileMs"] = warm;
            report["warmLoopRuns"] = MeasureRepeatedExecution(cold.Bytes);

            Progress("runaway loop");
            report["runaway"] = MeasureExecution(Compile("public static class Snippet { public static long Run() { while(true) {} } }").Bytes);
            Progress("recursion");
            report["recursion"] = MeasureExecution(Compile("public static class Snippet { static long Recurse() => Recurse(); public static long Run() => Recurse(); }").Bytes);

            Progress("allocations");
            var allocations = new Dictionary<string, object?>();
            foreach (var (name, body) in new[]
            {
                ("zero", "return 42;"),
                ("boxing", "Sink=42; return 42;"),
                ("array16", "var data=new int[16]; Sink=data; return data.Length;"),
                ("closure", "int value=42; System.Func<int> f=()=>value; Sink=f; return f();"),
            })
            {
                var code = "public static class Snippet { public static object Sink; public static long Run() { " + body + " } }";
                allocations[name] = MeasureAllocations(Compile(code).Bytes);
            }
            report["allocations"] = allocations;
            report["allocationBoundary"] = "dedicated thread; 1000 warmup calls; full GC; five samples of 1000 direct Func<long> calls; delta GC.GetAllocatedBytesForCurrentThread; compile/load/delegate/thread creation excluded; probes included; static Sink keeps allocations observable";

            Progress("100 compile/run/unload cycles");
            var weak = new List<WeakReference>();
            var memory = new List<object>();
            FullGc();
            var baseline = GC.GetTotalMemory(true);
            var watch = Stopwatch.StartNew();
            for (var i = 1; i <= 100; i++)
            {
                weak.Add(Cycle(Compile(loop).Bytes));
                if (i is 1 or 10 or 25 or 50 or 75 or 100)
                {
                    FullGc();
                    var retained = GC.GetTotalMemory(true);
                    memory.Add(new { cycles = i, aliveContexts = weak.Count(w => w.IsAlive), managedBytes = retained, retainedBytes = retained - baseline, workingSetBytes = WorkingSet() });
                    Progress($"cycles {i}/100, alive {weak.Count(w => w.IsAlive)}, retained {retained - baseline}");
                }
            }
            FullGc();
            report["cycles"] = new { count = 100, elapsedMs = watch.Elapsed.TotalMilliseconds, baselineManagedBytes = baseline, aliveContexts = weak.Count(w => w.IsAlive), checkpoints = memory };
            report["status"] = "complete";
        }
        catch (Exception ex) { report["status"] = "error"; report["error"] = ex.ToString(); }
        Progress("finished");
        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }

    private static (byte[] Bytes, double Milliseconds) Compile(string source)
    {
        var watch = Stopwatch.StartNew();
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14), "snippet.cs");
        var compilation = CSharpCompilation.Create("Spike_" + Guid.NewGuid().ToString("N"), [tree], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) throw new InvalidOperationException(string.Join("\n", errors.Select(d => d.ToString())));
        compilation = compilation.ReplaceSyntaxTree(tree, ProbeRewriter.Rewrite(tree, semanticModel: compilation.GetSemanticModel(tree)));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return (output.ToArray(), watch.Elapsed.TotalMilliseconds);
    }

    private static Func<long> Load(byte[] bytes, AssemblyLoadContext context)
    {
        using var stream = new MemoryStream(bytes);
        return context.LoadFromStream(stream).GetType("Snippet")!.GetMethod("Run")!.CreateDelegate<Func<long>>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object MeasureExecution(byte[] bytes)
    {
        var watch = Stopwatch.StartNew();
        var context = new AssemblyLoadContext("spike", isCollectible: true);
        var run = Load(bytes, context);
        var loadMs = watch.Elapsed.TotalMilliseconds;
        var result = Dedicated(() =>
        {
            var budget = new ProbeBudget();
            ProbeBudget.Current = budget;
            var timer = Stopwatch.StartNew();
            long? value = null;
            string? error = null;
            try { value = run(); }
            catch (Exception ex) { error = ex.GetType().FullName + ": " + ex.Message; }
            finally { ProbeBudget.Current = null; }
            return new { runMs = timer.Elapsed.TotalMilliseconds, value, error, steps = budget.Steps, finalDepth = budget.Depth };
        });
        context.Unload();
        return new { loadMs, execution = result };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object MeasureRepeatedExecution(byte[] bytes)
    {
        var context = new AssemblyLoadContext("warm-loop", isCollectible: true);
        var run = Load(bytes, context);
        var result = Dedicated(() =>
        {
            var times = new List<double>();
            var values = new List<long>();
            for (var i = 0; i < 5; i++)
            {
                ProbeBudget.Current = new ProbeBudget();
                var watch = Stopwatch.StartNew();
                values.Add(run());
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            ProbeBudget.Current = null;
            return new { milliseconds = times, values };
        });
        context.Unload();
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object MeasureAllocations(byte[] bytes)
    {
        var context = new AssemblyLoadContext("allocations", isCollectible: true);
        var run = Load(bytes, context);
        var result = Dedicated(() =>
        {
            ProbeBudget.Current = new ProbeBudget(maxSteps: 1_000_000, timeout: TimeSpan.FromMinutes(2));
            long checksum = 0;
            for (var i = 0; i < 1000; i++) checksum += run();
            FullGc();
            var samples = new long[5];
            for (var sample = 0; sample < samples.Length; sample++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 1000; i++) checksum += run();
                samples[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            ProbeBudget.Current = null;
            return new { callsPerSample = 1000, bytesPerSample = samples, bytesPerCall = samples.Select(n => n / 1000d).ToArray(), checksum };
        });
        context.Unload();
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Cycle(byte[] bytes)
    {
        var context = new AssemblyLoadContext("cycle", isCollectible: true);
        var weak = new WeakReference(context);
        var run = Load(bytes, context);
        var value = Dedicated(() =>
        {
            ProbeBudget.Current = new ProbeBudget();
            try { return run(); }
            finally { ProbeBudget.Current = null; }
        });
        if (value != 49_995_000) throw new InvalidOperationException("Incorrect cycle loop result");
        context.Unload();
        return weak;
    }

    private static T Dedicated<T>(Func<T> action)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception ex) { error = ex; } }, ProbeBudget.DefaultStackSize)
            { IsBackground = true, Name = "Roslyn spike dedicated stack" };
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(3))) throw new TimeoutException("Native spike dedicated thread exceeded three minutes.");
        if (error is not null) throw new InvalidOperationException("Native spike execution failed", error);
        return result!;
    }

    private static void FullGc() { for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } }
    private static long? WorkingSet() { try { return Environment.WorkingSet; } catch { return null; } }
    private static void Progress(string value)
    {
#if MACCATALYST
        try { File.WriteAllText("/tmp/brilliant-138-progress.txt", value); } catch { }
#elif ANDROID
        Android.Util.Log.Info("BRILLIANT_SPIKE_PROGRESS", value);
#else
        Console.Error.WriteLine(value);
#endif
    }
}
