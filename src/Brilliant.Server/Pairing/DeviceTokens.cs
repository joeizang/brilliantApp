using System.Security.Cryptography;
using System.Text;

namespace Brilliant.Server.Pairing;

public static class DeviceTokens
{
    /// <summary>A 256-bit random token, URL-safe so it travels in a header without escaping.</summary>
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
