namespace Brilliant.Data;

public static class DeviceId
{
    /// <summary>Returns this install's stable device ID, creating and persisting it on first use.</summary>
    public static string GetOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "device-id");
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } existing) return existing;
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, id);
        return id;
    }
}
