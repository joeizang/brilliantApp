using System.Collections.Frozen;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Brilliant.Core.Apps;

/// <summary>An app's identity and enabled capabilities. Parse once and share across the host, CLI and server.</summary>
public sealed class AppManifest
{
    public string AppId { get; }
    public string DisplayName { get; }
    public string? Icon { get; }
    public AppAccent Accent { get; }
    public string ContentPack { get; }
    public IReadOnlySet<string> Runtimes { get; }
    public IReadOnlySet<string> StepTypes { get; }

    private AppManifest(Document doc)
    {
        AppId = doc.AppId!;
        DisplayName = doc.DisplayName!;
        Icon = doc.Icon;
        Accent = new(doc.Accent!.Dark!, doc.Accent.Light!, doc.Accent.OnDark!, doc.Accent.OnLight!);
        ContentPack = doc.ContentPack!;
        Runtimes = doc.Runtimes!.ToFrozenSet(StringComparer.Ordinal);
        StepTypes = doc.StepTypes!.ToFrozenSet(StringComparer.Ordinal);
    }

    public bool SupportsRuntime(string language) => Runtimes.Contains(language);
    public bool SupportsStepType(string stepType) => StepTypes.Contains(stepType);

    public static AppManifest Parse(string yaml)
    {
        try
        {
            var doc = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithDuplicateKeyChecking()
                .Build()
                .Deserialize<Document>(yaml);
            if (doc is null) throw new AppManifestException("App manifest is empty.");
            Require(doc.AppId, "appId", @"^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$");
            if (string.IsNullOrWhiteSpace(doc.DisplayName)) throw new AppManifestException("App manifest requires 'displayName'.");
            RequirePath(doc.ContentPack, "contentPack");
            if (doc.Icon is not null) RequirePath(doc.Icon, "icon");
            Require(doc.Accent?.Dark, "accent.dark", @"^#[0-9a-fA-F]{6}$");
            Require(doc.Accent?.Light, "accent.light", @"^#[0-9a-fA-F]{6}$");
            Require(doc.Accent?.OnDark, "accent.onDark", @"^#[0-9a-fA-F]{6}$");
            Require(doc.Accent?.OnLight, "accent.onLight", @"^#[0-9a-fA-F]{6}$");
            RequireCapabilities(doc.Runtimes, "runtimes", allowEmpty: true);
            RequireCapabilities(doc.StepTypes, "stepTypes", allowEmpty: false);
            return new AppManifest(doc);
        }
        catch (YamlException ex)
        {
            throw new AppManifestException($"App manifest YAML is invalid: {ex.Message}", ex);
        }
    }

    private static void Require(string? value, string field, string pattern)
    {
        if (value is null || !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))
            throw new AppManifestException($"App manifest has an invalid or missing '{field}'.");
    }

    private static void RequirePath(string? value, string field)
    {
        Require(value, field, @"^[a-zA-Z0-9_][a-zA-Z0-9_./-]*$");
        if (value!.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new AppManifestException($"App manifest '{field}' must be a relative asset path without traversal.");
    }

    private static void RequireCapabilities(string[]? values, string field, bool allowEmpty)
    {
        if (values is null || (!allowEmpty && values.Length == 0))
            throw new AppManifestException($"App manifest requires '{field}'.");
        foreach (var value in values) Require(value, field, @"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new AppManifestException($"App manifest '{field}' contains duplicate capabilities.");
    }

    // Mutable only while deserializing; callers receive an immutable, validated manifest.
    private sealed class Document
    {
        public string? AppId { get; set; }
        public string? DisplayName { get; set; }
        public string? Icon { get; set; }
        public AccentDocument? Accent { get; set; }
        public string? ContentPack { get; set; }
        public string[]? Runtimes { get; set; }
        public string[]? StepTypes { get; set; }
    }

    private sealed class AccentDocument
    {
        public string? Dark { get; set; }
        public string? Light { get; set; }
        public string? OnDark { get; set; }
        public string? OnLight { get; set; }
    }
}

/// <summary>Accent and text-on-accent colours for the system's dark and light appearances.</summary>
public sealed record AppAccent(string Dark, string Light, string OnDark, string OnLight);
