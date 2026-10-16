using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Recording.Spike;

// usage: Brilliant.Recording.Spike trace|lower|bench [outDir]
var mode = args.Length > 0 ? args[0] : "trace";
var outDir = args.Length > 1 ? args[1] : "out";
Directory.CreateDirectory(outDir);

const string SortExample = """
    public static class Demo
    {
        public static void Run()
        {
            int[] a = { 5, 2, 4, 1 };
            for (int i = 1; i < a.Length; i++)
            {
                int key = a[i];
                int j = i - 1;
                while (j >= 0 && a[j] > key)
                {
                    a[j + 1] = a[j];
                    j--;
                }
                a[j + 1] = key;
            }
        }
    }
    """;

const string LoweringExample = """
    using System.Collections.Generic;
    using System.Threading.Tasks;

    public static class Demo
    {
        public static async Task<int> FetchAsync(int n)
        {
            await Task.Delay(1);
            return n * 2;
        }

        public static int Total(List<int> items)
        {
            var sum = 0;
            foreach (var item in items) sum += item;
            return sum;
        }
    }
    """;

var json = new JsonSerializerOptions(ContentPackFormat.JsonOptions) { WriteIndented = true };
switch (mode)
{
    case "trace":
    {
        var (result, t, rewritten) = TraceRecorder.Record(SortExample, "Demo", "Run");
        File.WriteAllText(Path.Combine(outDir, "trace.json"), JsonSerializer.Serialize(result, json) + "\n");
        File.WriteAllText(Path.Combine(outDir, "rewritten.cs"), rewritten + "\n");
        File.WriteAllText(Path.Combine(outDir, "example.cs"), SortExample + "\n");
        Console.WriteLine($"trace: {result.Status}, {result.Frames.Count} frames; parse+rewrite {t.ParseAndRewriteMs:F0} ms, emit {t.EmitMs:F0} ms, run {t.RunMs:F0} ms, convert {t.ConvertMs:F1} ms");
        break;
    }
    case "lower":
    {
        var o = Lowering.Lower(LoweringExample);
        File.WriteAllText(Path.Combine(outDir, "lowered.cs"), o.LoweredCSharp);
        File.WriteAllText(Path.Combine(outDir, "lowered.il"), o.Il);
        Console.WriteLine($"lower: emit {o.EmitMs:F0} ms, decompile {o.DecompileMs:F0} ms, disassemble {o.DisassembleMs:F0} ms; image sha256 {o.ImageSha256[..16]}");
        break;
    }
    case "bench":
    {
        var o = Bench.Run();
        File.WriteAllText(Path.Combine(outDir, "bench.json"), JsonSerializer.Serialize(o, json) + "\n");
        foreach (var r in o.Rows) Console.WriteLine($"bench: {r.Method} mean {r.MeanNs:F1} ns, ±{r.StdDevNs:F1}, {r.AllocatedBytes} B/op, n={r.Iterations}");
        Console.WriteLine($"bench: total {o.TotalMs:F0} ms");
        break;
    }
    default: Console.Error.WriteLine("unknown mode"); return 2;
}
return 0;
