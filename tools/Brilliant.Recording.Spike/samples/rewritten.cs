public static class Demo
{
    public static void Run()
{global::Brilliant.Code.CSharp.ProbeBudget.Current!.Enter();try    {
global::Brilliant.Code.CSharp.ProbeBudget.Current!.Snapshot(5, new string[] {}, new object?[] {});        int[] a = { 5, 2, 4, 1 };
global::Brilliant.Code.CSharp.ProbeBudget.Current!.Snapshot(6, new string[] {"a"}, new object?[] {@a});        for (int i = 1; i < a.Length; i++)
        {
global::Brilliant.Code.CSharp.ProbeBudget.Current!.Tick();global::Brilliant.Code.CSharp.ProbeBudget.Current!.Snapshot(8, new string[] {"i","a"}, new object?[] {@i,@a});            int key = a[i];
global::Brilliant.Code.CSharp.ProbeBudget.Current!.Snapshot(9, new string[] {"key","i","a"}, new object?[] {@key,@i,@a});            int j = i - 1;
global::Brilliant.Code.CSharp.ProbeBudget.Current!.Snapshot(10, new string[] {"j","key","i","a"}, new object?[] {@j,@key,@i,@a});            while (j >= 0 && a[j] > key)
            {
global::Brilliant.Code.CSharp.ProbeBudget.Current!.Tick();global::Brilliant.Code.CSharp.ProbeBudget.Current!.Snapshot(12, new string[] {"j","key","i","a"}, new object?[] {@j,@key,@i,@a});                a[j + 1] = a[j];
global::Brilliant.Code.CSharp.ProbeBudget.Current!.Snapshot(13, new string[] {"j","key","i","a"}, new object?[] {@j,@key,@i,@a});                j--;
            }
global::Brilliant.Code.CSharp.ProbeBudget.Current!.Snapshot(15, new string[] {"j","key","i","a"}, new object?[] {@j,@key,@i,@a});            a[j + 1] = key;
        }
global::Brilliant.Code.CSharp.ProbeBudget.Current!.Snapshot(17, new string[] {"a"}, new object?[] {@a});return;    }
finally{global::Brilliant.Code.CSharp.ProbeBudget.Current!.Exit();}}}
