using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Brilliant.Code.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Brilliant.Code.CSharp.Tests;

public class ProbeTests
{
    [Fact]
    public void A_finite_loop_exhausts_the_step_budget_before_completing()
    {
        Assert.Throws<BudgetExceededException>(() => Run("class Lesson { public static object Run() { int i=0; while(i<500) i++; return i; } }", new(maxSteps: 10)));
    }

    [Fact]
    public void Recursive_expression_methods_stop_at_the_depth_limit_and_unwind_the_counter()
    {
        var budget = new ProbeBudget(maxSteps: 10_000, maxDepth: 16);
        Assert.Throws<BudgetExceededException>(() => Run("class Lesson { static int F(int n) => n == 0 ? 0 : F(n-1); public static object Run() => F(500); }", budget));
        Assert.Equal(0, budget.Depth);
    }

    [Theory]
    [InlineData("catch(System.Exception) { return 17; }")]
    [InlineData("catch { return 17; }")]
    [InlineData("catch(System.Exception ex) when(ex.Message.Length > 0) { return 17; }")]
    public void Learner_catches_cannot_swallow_a_budget_failure(string handler)
    {
        Assert.Throws<BudgetExceededException>(() => Run("class Lesson { public static object Run() { try { int i=0; while(i<500) i++; return i; } " + handler + " } }", new(maxSteps: 10)));
    }

    [Fact]
    public void Line_probes_capture_only_assigned_locals_on_original_source_lines()
    {
        var budget = new ProbeBudget();
        var result = Run("""
            class Lesson
            {
                public static object Run()
                {
                    int value;
                    value = 3;
                    value += 2;
                    return value;
                }
            }
            """, budget, trace: true);
        Assert.Equal(5, result);
        Assert.Contains(budget.Snapshots, frame => frame.Line == 7 && Equals(frame.Locals.GetValueOrDefault("value"), 3));
        Assert.Contains(budget.Snapshots, frame => frame.Line == 8 && Equals(frame.Locals.GetValueOrDefault("value"), 5));
        Assert.DoesNotContain(budget.Snapshots.Where(f => f.Line <= 6), f => f.Locals.ContainsKey("value"));
    }

    [Theory]
    [InlineData("for(int i=0;i<500;i++) { if(i%2==0) continue; }")]
    [InlineData("int i=0; do { i++; continue; } while(i<500);")]
    [InlineData("foreach(var value in new int[500]) { continue; }")]
    [InlineData("foreach(var (a,b) in new (int,int)[500]) { continue; }")]
    public void Every_loop_form_including_continue_is_bounded(string loop)
    {
        Assert.Throws<BudgetExceededException>(() => Run("class Lesson { public static object Run() { " + loop + " return 1; } }", new(maxSteps: 10)));
    }

    [Fact]
    public void Finally_loops_cannot_replace_an_exhausted_budget_with_more_work()
    {
        Assert.Throws<BudgetExceededException>(() => Run("class Lesson { public static object Run() { try { int i=0; while(i<500)i++; } finally { int j=0; while(j<500)j++; } return 1; } }", new(maxSteps: 10)));
    }

    [Fact]
    public void Ordinary_exceptions_preserve_filters_and_release_depth_for_later_calls()
    {
        var budget = new ProbeBudget(maxDepth: 8);
        Assert.Equal(20, Run("class Lesson { static int Fail() => throw new System.Exception(); public static object Run() { int count=0; for(int i=0;i<20;i++) { try { Fail(); } catch(System.Exception ex) when(ex.Message.Length>0) { count++; } } return count; } }", budget));
        Assert.Equal(0, budget.Depth);
    }

    [Fact]
    public void Default_depth_limit_stops_runaway_recursion_on_the_explicit_stack()
    {
        var budget = new ProbeBudget();
        Assert.Throws<BudgetExceededException>(() => Run("class Lesson { static int F() => F(); public static object Run() => F(); }", budget));
        Assert.Equal(0, budget.Depth);
    }

    [Fact]
    public void Recursive_lambdas_are_instrumented_using_their_delegate_return_type()
    {
        Assert.Throws<BudgetExceededException>(() => Run("class Lesson { public static object Run() { System.Func<int,int> f=null; f=n=>n==0?0:f(n-1); return f(500); } }", new(maxDepth: 8)));
        Assert.Equal(2, Run("class Lesson { public static object Run() { int x=0; System.Action a=()=>x++; a(); a(); return x; } }", new()));
    }

    [Fact]
    public void A_nonmatching_original_filter_remains_nonmatching()
    {
        Assert.Throws<InvalidOperationException>(() => Run("class Lesson { public static object Run() { try { throw new System.InvalidOperationException(); } catch(System.Exception ex) when(ex.Message.Length < 0) { return 99; } } }", new()));
    }

    [Theory]
    [InlineData("class Lesson { public static object Run() { again: goto again; } }")]
    [InlineData("class Lesson { public Lesson(int x) : this(x+1) {} public static object Run() => 1; }")]
    public void Unsupported_unbounded_transfers_fail_closed(string source) =>
        Assert.Throws<NotSupportedException>(() => Run(source, new()));

    [Fact]
    public void A_budget_exception_discarded_by_a_clr_filter_still_stops_the_run()
    {
        var budget = new ProbeBudget(maxSteps: 10);
        Assert.Throws<BudgetExceededException>(() => Run("class Lesson { static bool Filter() { for(int i=0;i<500;i++){} return true; } public static object Run() { try { throw new System.Exception(); } catch(System.Exception) when(Filter()) { return 1; } catch { return 42; } } }", budget));
        Assert.Equal(0, budget.Depth);
    }

    [Fact]
    public void Escaped_catch_identifiers_remain_valid()
    {
        Assert.Equal(1, Run("class Lesson { public static object Run() { try { throw new System.Exception(); } catch(System.Exception @event) when(@event.Message.Length>0) { return 1; } } }", new()));
    }

    [Theory]
    [InlineData("catch(System.InvalidOperationException) { return 1; }")]
    [InlineData("catch(System.InvalidOperationException ex) when(ex.Message.Length >= 0) { return 1; }")]
    public void A_catch_of_a_specific_exception_type_still_compiles_and_catches(string handler)
    {
        Assert.Equal(1, Run("class Lesson { public static object Run() { try { throw new System.InvalidOperationException(); } " + handler + " } }", new()));
    }

    [Fact]
    public void Unbraced_loop_bodies_capture_the_executed_line_and_loop_local()
    {
        var budget = new ProbeBudget();
        Run("""
            class Lesson {
            public static object Run() {
                int sum = 0;
                for(int i=0;i<3;i++)
                    sum += i;
                if (sum == 3)
                    return sum;
                return -1;
            }}
            """, budget, trace: true);
        var iterations = budget.Snapshots.Where(f => f.Line == 5).ToArray();
        Assert.Equal(new object?[] { 0, 1, 2 }, iterations.Select(f => f.Locals["i"]));
        Assert.Contains(budget.Snapshots, f => f.Line == 7 && Equals(f.Locals["sum"], 3));
    }

    [Fact]
    public void Snapshots_freeze_nested_arrays_and_object_fields_without_invoking_getters()
    {
        var budget = new ProbeBudget();
        Run("""
            class Box { public int Value=1; public int Bad => throw new System.Exception(); }
            class Lesson {
            public static object Run() {
                int[][] xs = new int[][] { new int[] { 1 } };
                Box box = new();
                xs[0][0] = 2;
                box.Value = 2;
                return 1;
            }}
            """, budget, trace: true);
        var frame = Assert.Single(budget.Snapshots, f => f.Line == 6);
        var outer = Assert.IsType<CapturedSequence>(frame.Locals["xs"]);
        var inner = Assert.IsType<CapturedSequence>(outer.Values[0]);
        Assert.Equal(1, inner.Values[0]);
        var box = Assert.IsType<CapturedObject>(frame.Locals["box"]);
        Assert.Equal(1, box.Fields["Box.Value"]);
    }

    [Fact]
    public void Expression_bodies_report_parameters_on_the_expression_line()
    {
        var budget = new ProbeBudget();
        Assert.Equal(6, Run("class Lesson {\nstatic int Double(int n) => n * 2;\npublic static object Run() => Double(3);\n}", budget, trace: true));
        Assert.Contains(budget.Snapshots, frame => frame.Line == 2 && Equals(frame.Locals.GetValueOrDefault("n"), 3));
    }

    [Fact]
    public void The_time_budget_is_checked_again_when_a_method_returns_from_a_library_call()
    {
        Assert.Throws<BudgetExceededException>(() => Run("class Lesson { public static object Run() { System.Threading.Thread.Sleep(30); return 1; } }", new(timeout: TimeSpan.FromMilliseconds(1))));
    }

    private static object? Run(string source, ProbeBudget budget, bool trace = false)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14), "lesson.cs");
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(ProbeBudget).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Probes_" + Guid.NewGuid().ToString("N"), [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var rewritten = ProbeRewriter.Rewrite(tree, trace, compilation.GetSemanticModel(tree));
        compilation = compilation.ReplaceSyntaxTree(tree, rewritten);
        using var output = new MemoryStream();
        var emit = compilation.Emit(output);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + rewritten.GetRoot().ToFullString());
        output.Position = 0;
        var context = new AssemblyLoadContext("probes-test", isCollectible: true);
        object? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ProbeBudget.Current = budget;
            try { result = context.LoadFromStream(output).GetType("Lesson")!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException ex) { failure = ex.InnerException; }
            catch (Exception ex) { failure = ex; }
            finally { ProbeBudget.Current = null; }
        }, ProbeBudget.DefaultStackSize);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Instrumented learner execution did not stop.");
        context.Unload();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}
