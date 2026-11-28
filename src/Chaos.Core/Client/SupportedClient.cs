using System.Security.Cryptography;

namespace Chaos.Client;

/// <summary>The client build Battle City supports.</summary>
public static class SupportedClient
{
    public const string FileName = "online_pc.dat";
    public const string Version = "Ver.060110.00";
    public const long Length = 3_313_664;
    public const string Sha256 = "c57a405e6b890c17fe89551d36e6df64b56d826f101c675b67308c56381cf9c2";

    public const string PublicSha256 = "0d40c80998bccd817642203c3468ee9d6a8098d8846c1fcd5453a3219466a65c";

    public static bool IsSupported(string path) =>
        new FileInfo(path).Length == Length && Sha256Of(path) == Sha256;

    public static string Sha256Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256Of(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}
