namespace Brilliant.Lessons.UI;

/// <summary>What the Sync screen suggests this device be called on the server. A host that knows better than the machine name (a phone reports "localhost") registers one.</summary>
public sealed record DeviceLabel(string Name);
