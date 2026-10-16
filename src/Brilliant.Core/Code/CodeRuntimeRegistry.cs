namespace Brilliant.Core.Code;

/// <summary>Only the runtimes registered by the selected app are available to its views.</summary>
public sealed class CodeRuntimeRegistry
{
    private readonly IReadOnlyDictionary<string, ICodeRuntime> _runtimes;

    public CodeRuntimeRegistry(IEnumerable<ICodeRuntime> runtimes)
    {
        var byLanguage = new Dictionary<string, ICodeRuntime>(StringComparer.Ordinal);
        foreach (var runtime in runtimes)
        {
            if (string.IsNullOrWhiteSpace(runtime.Language))
                throw new ArgumentException("A code runtime must declare its language.", nameof(runtimes));
            if (!byLanguage.TryAdd(runtime.Language, runtime))
                throw new ArgumentException($"More than one runtime is registered for '{runtime.Language}'.", nameof(runtimes));
        }
        _runtimes = byLanguage;
    }

    public ICodeRuntime Resolve(string language) => _runtimes.TryGetValue(language, out var runtime)
        ? runtime : throw new InvalidOperationException($"No runtime is enabled for {CodeLanguages.DisplayName(language)}.");
}

public static class CodeLanguages
{
    public static string DisplayName(string language) => language switch
    {
        "python" => "Python",
        "csharp" => "C#",
        _ => language,
    };
}
