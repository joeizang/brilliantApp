using System.Diagnostics;
using Brilliant.Code.CSharp;
using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Brilliant.Recording.Spike;

/// <summary>
/// Experiment 1. Records one run of a C# example as a <see cref="TraceResult"/>, the format the trace player already reads:
/// the production <see cref="ProbeRewriter"/> inserts a snapshot probe before every statement; the example runs once on a
/// dedicated thread under a <see cref="ProbeBudget"/>; the frozen snapshots become frames.
/// </summary>
static class TraceRecorder
{
    public sealed record Timings(double ParseAndRewriteMs, double EmitMs, double RunMs, double ConvertMs);

    /// <param name="entry">A public static parameterless method of the first type; it must be void.</param>
    public static (TraceResult Result, Timings Timings, string RewrittenSource) Record(string source, string type, string entry)
    {
        var sw = Stopwatch.StartNew();
        var original = Compile.Tree(source);
        var withReturn = AppendReturn(original, entry);
        // The semantic model must belong to the tree that is rewritten (the rewriter reads data flow from it).
        var model = Compile.Compilation([withReturn]).GetSemanticModel(withReturn);
        var rewritten = ProbeRewriter.Rewrite(withReturn, captureLocals: true, model);
        var parseMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var image = Compile.Emit(Compile.Compilation([rewritten]));
        var emitMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var (context, assembly) = Compile.Load(image);
        var budget = new ProbeBudget(maxSteps: 100_000, timeout: TimeSpan.FromSeconds(2), maxSnapshots: 500);
        Exception? failure = null;
        // A dedicated thread with the same explicit stack as the Mac runtime; ProbeBudget.Current is thread-static.
        var thread = new Thread(() =>
        {
            ProbeBudget.Current = budget;
            try { assembly.GetType(type)!.GetMethod(entry)!.Invoke(null, null); }
            catch (Exception ex) { failure = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex; }
        }, ProbeBudget.DefaultStackSize);
        thread.Start();
        thread.Join();
        var runMs = sw.Elapsed.TotalMilliseconds;
        context.Unload();

        sw.Restart();
        var result = Convert(budget.Snapshots, entry, failure);
        return (result, new Timings(parseMs, emitMs, runMs, sw.Elapsed.TotalMilliseconds), rewritten.ToString());
    }

    // Probes run *before* a statement, so the state after the last one is only seen if the entry method ends in a statement.
    // The recorder adds `return;` for it; its snapshot becomes the closing "return" frame.
    static SyntaxTree AppendReturn(SyntaxTree tree, string entry)
    {
        var root = tree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == entry);
        var body = method.Body!.AddStatements(SyntaxFactory.ReturnStatement());
        return CSharpSyntaxTree.Create((CSharpSyntaxNode)root.ReplaceNode(method.Body!, body), (CSharpParseOptions)tree.Options, tree.FilePath);
    }

    static TraceResult Convert(IReadOnlyList<ProbeSnapshot> snapshots, string function, Exception? failure)
    {
        var frames = new List<TraceFrame>();
        for (var i = 0; i < snapshots.Count; i++)
        {
            var last = i == snapshots.Count - 1 && failure is null;
            var locals = new List<TraceLocal>();
            var tracked = new Dictionary<string, TrackedArray>();
            foreach (var (name, value) in snapshots[i].Locals.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (value is CapturedSequence sequence)
                {
                    var cells = sequence.Values.Take(32).Select(Repr).ToList();
                    var label = ShortType(sequence.Type);
                    tracked[name] = new TrackedArray(label, cells, Math.Max(0, sequence.Values.Count - cells.Count), [Visual.Array]);
                    locals.Add(new TraceLocal(name, label, "[" + string.Join(", ", cells) + "]"));
                }
                else locals.Add(new TraceLocal(name, TypeOf(value), Repr(value)));
            }
            frames.Add(new TraceFrame(last ? "return" : "line", last ? null : snapshots[i].Line, function, locals, tracked));
        }
        return failure is null
            ? new TraceResult(TraceStatus.Ok, frames, "", null, null, null)
            : new TraceResult(TraceStatus.Error, frames, "", failure.GetType().Name + ": " + failure.Message, null, snapshots.LastOrDefault()?.Line);
    }

    static string TypeOf(object? value) => value switch
    {
        null => "null", int => "int", long => "long", bool => "bool", char => "char", string => "string", double => "double",
        CapturedObject o => ShortType(o.Type),
        _ => value.GetType().Name,
    };

    static string Repr(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        char c => "'" + c + "'",
        bool b => b ? "true" : "false",
        CapturedSequence s => "[" + string.Join(", ", s.Values.Take(8).Select(Repr)) + "]",
        CapturedObject o => ShortType(o.Type),
        UnavailableCapture u => "…",
        _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    static string ShortType(string full) => full switch
    {
        "System.Int32[]" => "int[]",
        _ when full.StartsWith("System.Collections.Generic.List`1[[System.Int32") => "List<int>",
        _ => full.Split('.').Last(),
    };
}
