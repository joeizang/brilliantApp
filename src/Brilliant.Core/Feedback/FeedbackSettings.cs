using System.Text.Json;

namespace Brilliant.Core.Feedback;

/// <summary>
/// The learner's choice about where written-answer feedback comes from, on a device that can't run a model itself. Off until they
/// turn it on: an answer only leaves the device for the household server when they have said it may.
/// </summary>
public interface IFeedbackSettings
{
    /// <summary>Whether to send written answers to the household server to be graded by the Mac's model.</summary>
    bool UseServer { get; set; }
}

/// <summary>Keeps <see cref="IFeedbackSettings"/> in a small JSON file. A missing or damaged file means off; a file that can't be written still holds the choice for this run.</summary>
public sealed class FileFeedbackSettings(string path) : IFeedbackSettings
{
    private sealed record Stored(bool UseServer);

    private readonly object _gate = new();
    private bool? _value;

    public bool UseServer
    {
        get
        {
            lock (_gate) return _value ??= Read();
        }
        set
        {
            lock (_gate)
            {
                _value = value;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    var temp = path + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(value)));
                    File.Move(temp, path, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private bool Read()
    {
        try { return JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), JsonSerializerOptions.Web)?.UseServer ?? false; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
}
