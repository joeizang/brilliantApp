using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brilliant.Core.Content;

/// <summary>Reads cached DSA comparison objects as well as the labelled snippet lists written by current packs.</summary>
public sealed class ComparisonJsonConverter : JsonConverter<IReadOnlyList<LabelledSnippet>>
{
    public override IReadOnlyList<LabelledSnippet>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
            return JsonSerializer.Deserialize<List<LabelledSnippet>>(ref reader, options);

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A comparison must be a list of labelled snippets.");

        using var document = JsonDocument.ParseValue(ref reader);
        var legacy = document.RootElement;
        if (!legacy.TryGetProperty("cSharp", out var csharp) || csharp.ValueKind != JsonValueKind.String ||
            !legacy.TryGetProperty("python", out var python) || python.ValueKind != JsonValueKind.String)
            throw new JsonException("A legacy comparison needs cSharp and python code.");

        return [new("C#", "csharp", csharp.GetString()!), new("Python", "python", python.GetString()!)];
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<LabelledSnippet> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}
