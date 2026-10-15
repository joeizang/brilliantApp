namespace Brilliant.Core.Apps;

/// <summary>A malformed app definition, with a diagnostic suitable for the host, CLI or server.</summary>
public sealed class AppManifestException(string message, Exception? inner = null) : Exception(message, inner);
