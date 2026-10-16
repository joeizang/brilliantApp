using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Brilliant.Code.CSharp;
using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Brilliant.Runtime.CSharp;

/// <summary>
/// Records a C# example the way the Python tracer records a Python one, for the CLI to carry in the pack: the code is compiled with a probe
/// before each statement, run under the execution budget, and each probe's variables become one <see cref="TraceFrame"/>. The recording is
/// the same for the same source: locals are sorted by name, the assembly is built deterministically and nothing time- or address-dependent
/// is recorded. Devices play the frames and never run the code, so Android needs no C# runtime.
/// </summary>
public static class CSharpTraceRecorder
{
    public const int MaxFrames = 300;      // steps recorded; more than a learner can usefully click through
    public const int MaxLocals = 24;       // variables shown per frame
    public const int MaxCells = 40;        // array cells recorded per frame (the rest are counted in More)
    public const int MaxRepr = 60;         // characters of a variable's repr
    public const int MaxCellRepr = 24;     // characters of an array cell's repr
    public const int MaxOutput = 4000;     // characters of stdout kept

    private const string FileName = "code.cs";
    private const string Wrapper = "__Main";
    private const string Module = "Main";
    private const int EndLine = 16_707_565;   // the largest #line number: the probe at the end of the top-level code, whose frame is the closing "return"
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);

    public static TraceResult Record(string code, IReadOnlyList<Visual> visuals)
    {
        if (code.Length > 65_536) return Failure("C# examples support up to 64 KiB of source.");
        if (!LanguageVersionFacts.TryParse(CSharpPins.LanguageVersion, out var languageVersion)) return Failure("Unsupported pinned C# language version.");
        var options = new CSharpParseOptions(languageVersion);
        var root = CSharpSyntaxTree.ParseText(code, options, FileName, Encoding.UTF8).GetCompilationUnitRoot();
        if (root.DescendantTrivia(descendIntoTrivia: true).Any(t => t.IsKind(SyntaxKind.DefineDirectiveTrivia) || t.IsKind(SyntaxKind.UndefDirectiveTrivia)))
            return Failure("Source-local #define/#undef directives are not supported when recording C#.");
        if (root.DescendantNodes().Any(n => n is AwaitExpressionSyntax or YieldStatementSyntax or UnsafeStatementSyntax)
            || root.DescendantTokens().Any(t => t.IsKind(SyntaxKind.AsyncKeyword) || t.IsKind(SyntaxKind.UnsafeKeyword)))
            return Failure("Recording C# supports synchronous, safe code; async/iterator/unsafe execution is not supported.");

        var text = new StringBuilder("using System;\nusing System.Collections.Generic;\nusing System.Linq;\n");
        foreach (var directive in root.Usings) text.AppendLine(directive.ToFullString());
        text.AppendLine($"public static class __BrilliantTrace {{ public static void {Wrapper}() {{");
        foreach (var statement in root.Members.OfType<GlobalStatementSyntax>()) AppendSource(text, statement);
        text.AppendLine($"#line {EndLine} \"{FileName}\"\n;\n#line default\n}} }}");
        foreach (var member in root.Members.Where(m => m is not GlobalStatementSyntax)) AppendSource(text, member);
        var tree = CSharpSyntaxTree.ParseText(text.ToString(), options, "trace.cs", Encoding.UTF8);

        var compilation = CSharpCompilation.Create("BrilliantTrace", [tree], CSharpSubmissionCompiler.MetadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: false, deterministic: true));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) return CompileFailure(errors);
        SyntaxTree rewritten;
        try
        {
            // Console output goes to the recording, not the process console (which other code may be using at the same time).
            var captured = tree.WithRootAndOptions(new ConsoleCaptureRewriter(compilation.GetSemanticModel(tree)).Visit(tree.GetRoot())!, tree.Options);
            compilation = compilation.ReplaceSyntaxTree(tree, captured);
            rewritten = ProbeRewriter.Rewrite(captured, captureLocals: true, compilation.GetSemanticModel(captured));
            compilation = compilation.ReplaceSyntaxTree(captured, rewritten);
        }
        catch (NotSupportedException ex) { return Failure(ex.Message); }
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        if (!emit.Success) return CompileFailure(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
        if (pe.Length > 1_048_576) return Failure("The compiled C# example exceeds 1 MiB.");
        pe.Position = 0;

        return Run(pe, visuals);
    }

    private static TraceResult Run(MemoryStream pe, IReadOnlyList<Visual> visuals)
    {
        var context = new AssemblyLoadContext("C# trace", isCollectible: true);
        var budget = new ProbeBudget(maxSteps: 100_000, timeout: TimeSpan.FromSeconds(2), maxSnapshots: MaxFrames);
        Exception? failure = null;
        using var printed = new StringWriter(CultureInfo.InvariantCulture);
        var finished = false;
        var assembly = context.LoadFromStream(pe);
        var main = assembly.GetType("__BrilliantTrace")!.GetMethod(Wrapper)!.CreateDelegate<Action>();
        var thread = new Thread(() =>
        {
            ProbeBudget.Current = budget;
            ProbeConsole.Writer = printed;
            try { main(); }
            catch (Exception ex) { failure = ex; }
            finally { ProbeConsole.Writer = null; ProbeBudget.Current = null; }
        }, ProbeBudget.DefaultStackSize) { IsBackground = true, Name = "C# trace" };
        thread.Start();
        finished = thread.Join(RunTimeout);
        if (!finished) budget.Cancel();

        var snapshots = budget.Snapshots;
        var completed = failure is null && finished;
        var frames = Frames(snapshots, completed, visuals);
        var stdout = Clip(printed.ToString().ReplaceLineEndings("\n"), MaxOutput, ellipsis: false);
        if (finished) context.Unload();
        if (!finished) return new(TraceStatus.Truncated, frames, stdout, "The example didn't finish in time and was stopped.", null, null);
        if (failure is BudgetExceededException exceeded) return new(TraceStatus.Truncated, frames, stdout, exceeded.Message, null, LastLine(snapshots));
        if (failure is not null)
            return new(TraceStatus.Error, frames, stdout, $"{failure.GetType().Name}: {failure.Message}", null, LastLine(snapshots));
        if (budget.SnapshotsDropped)
            return new(TraceStatus.Truncated, frames, stdout, $"The example ran past {MaxFrames} steps and was stopped.", null, null);
        return new(TraceStatus.Ok, frames, stdout, null, null, null);
    }

    private static int? LastLine(IReadOnlyList<ProbeSnapshot> snapshots) =>
        snapshots.Count > 0 && snapshots[^1].Line != EndLine ? snapshots[^1].Line : null;

    private static IReadOnlyList<TraceFrame> Frames(IReadOnlyList<ProbeSnapshot> snapshots, bool completed, IReadOnlyList<Visual> visuals)
    {
        var watched = visuals.Where(v => v.As is Visual.Array or Visual.Stack or Visual.Queue).Select(v => v.Variable).ToHashSet(StringComparer.Ordinal);
        var frames = new List<TraceFrame>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var closing = snapshot.Line == EndLine;
            // Only the end of the top-level code is a "return" with no line; it is only reached when the code ran through.
            if (closing && !completed) continue;
            var ordered = snapshot.Locals.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            var locals = ordered.Take(MaxLocals).Select(pair => Local(pair.Key, pair.Value)).ToArray();
            var tracked = ordered.Where(pair => watched.Contains(pair.Key) && pair.Value is CapturedSequence)
                .ToDictionary(pair => pair.Key, pair => Tracked((CapturedSequence)pair.Value!), StringComparer.Ordinal);
            var function = snapshot.Function is null or Wrapper ? Module : snapshot.Function;
            frames.Add(new(closing ? "return" : "line", closing ? null : snapshot.Line, function, locals, tracked));
        }
        return frames;
    }

    private static TraceLocal Local(string name, object? value) => value switch
    {
        null => new(name, "null", "null"),
        CapturedSequence sequence => new(name, sequence.Display, Clip(Repr(sequence), MaxRepr)),
        CapturedObject captured => new(name, captured.Display, Clip(Repr(captured), MaxRepr)),
        UnavailableCapture unavailable => new(name, "object", $"<{unavailable.Reason}>"),
        _ => new(name, TypeNames.Of(value.GetType()), Clip(Repr(value), MaxRepr), Integer(value)),
    };

    private static int? Integer(object value) => value switch
    {
        int i => i,
        short or sbyte or byte or ushort => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        uint u when u <= int.MaxValue => (int)u,
        ulong u when u <= int.MaxValue => (int)u,
        _ => null,
    };

    private static TrackedArray Tracked(CapturedSequence sequence)
    {
        var cells = sequence.Values.Take(MaxCells).Select(item => Clip(Repr(item), MaxCellRepr)).ToArray();
        var total = sequence.Total >= 0 ? sequence.Total : sequence.Values.Count;
        string[] drawings = sequence.Shape switch
        {
            CapturedShape.Stack => [Visual.Stack, Visual.Array],
            CapturedShape.Queue => [Visual.Queue, Visual.Array],
            CapturedShape.List => [Visual.Array, Visual.Stack, Visual.Queue],
            _ => [Visual.Array],
        };
        return new(sequence.Display, cells, Math.Max(0, total - cells.Length), drawings);
    }

    /// <summary>A value as C# writes it: a string and a char quoted, a bool and a float as literals, a collection in brackets.</summary>
    private static string Repr(object? value) => value switch
    {
        null => "null",
        string text => System.Text.Json.JsonSerializer.Serialize(text),
        char c => "'" + System.Text.Json.JsonSerializer.Serialize(c.ToString())[1..^1] + "'",
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        UnavailableCapture unavailable => $"<{unavailable.Reason}>",
        CapturedSequence sequence => "[" + string.Join(", ", sequence.Values.Take(MaxCells).Select(Repr))
            + ((sequence.Total >= 0 ? sequence.Total : sequence.Values.Count) > MaxCells ? ", …" : "") + "]",
        CapturedObject captured => Repr(captured),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
    };

    // A user type shows its fields (backing fields by their property's name); a framework type shows only its name, not its internals.
    private static string Repr(CapturedObject captured)
    {
        if (captured.Type.StartsWith("System.", StringComparison.Ordinal)) return captured.Display;
        var fields = captured.Fields.Where(pair => pair.Key != "…")
            .Select(pair => (Name: FieldName(pair.Key), pair.Value));
        return captured.Display + " { " + string.Join(", ", fields.Select(f => $"{f.Name} = {Repr(f.Value)}")) + " }";
    }

    private static string FieldName(string key)
    {
        var name = key[(key.LastIndexOf('.') + 1)..];
        return name.StartsWith('<') && name.IndexOf('>') is var end and > 0 ? name[1..end] : name;
    }

    private static string Clip(string text, int limit, bool ellipsis = true) =>
        text.Length <= limit ? text : text[..(ellipsis ? limit - 1 : limit)] + (ellipsis ? "…" : "");

    private static void AppendSource(StringBuilder text, MemberDeclarationSyntax member)
    {
        text.AppendLine($"#line {member.GetLocation().GetLineSpan().StartLinePosition.Line + 1} \"{FileName}\"");
        text.AppendLine(member.ToString());
        text.AppendLine("#line default");
    }

    private static TraceResult CompileFailure(IReadOnlyList<Diagnostic> errors)
    {
        var learner = errors.FirstOrDefault(d => d.Location.GetMappedLineSpan().Path == FileName) ?? errors[0];
        var line = learner.Location.GetMappedLineSpan() is { Path: FileName } span ? span.StartLinePosition.Line + 1 : (int?)null;
        return new(TraceStatus.Error, [], "", $"C# compilation failed: error {learner.Id}: {learner.GetMessage(CultureInfo.InvariantCulture)}", null, line);
    }

    private static TraceResult Failure(string message) => new(TraceStatus.Error, [], "", message, null, null);
}
