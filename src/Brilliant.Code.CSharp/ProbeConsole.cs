namespace Brilliant.Code.CSharp;

/// <summary>Per-execution output, without replacing the native process's Console writers.</summary>
public static class ProbeConsole
{
    [ThreadStatic] private static TextWriter? _writer;
    public static TextWriter Out => _writer ?? TextWriter.Null;
    public static TextWriter Error => Out;
    public static TextWriter? Writer { set => _writer = value; }
    public static void Write(object? value) => Out.Write(value);
    public static void Write(string? value) => Out.Write(value);
    public static void Write(char[]? value) => Out.Write(value);
    public static void Write(char[] value, int index, int count) => Out.Write(value, index, count);
    public static void Write(string format, params object?[] args) => Out.Write(format, args);
    public static void WriteLine() => Out.WriteLine();
    public static void WriteLine(object? value) => Out.WriteLine(value);
    public static void WriteLine(string? value) => Out.WriteLine(value);
    public static void WriteLine(char[]? value) => Out.WriteLine(value);
    public static void WriteLine(char[] value, int index, int count) => Out.WriteLine(value, index, count);
    public static void WriteLine(string format, params object?[] args) => Out.WriteLine(format, args);
}
