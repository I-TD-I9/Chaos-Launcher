using System.Security.Cryptography;

namespace Chaos.Client;

/// <summary>
/// The one client build Battle City speaks to: online_pc.dat of Konami's update of 9 February
/// 2006, which names itself Ver.060110.00 to the gate. It is the latest build that survives,
/// and it survives only as a copy of a game folder, which no installer registers.
/// </summary>
public static class SupportedClient
{
    public const string FileName = "online_pc.dat";
    public const string Version = "Ver.060110.00";
    public const long Length = 3_313_664;
    public const string Sha256 = "c57a405e6b890c17fe89551d36e6df64b56d826f101c675b67308c56381cf9c2";

    /// <summary>
    /// The same file patched for <see cref="ServerEndpoints.Public"/>: the gate's host name
    /// replaced, the registry read from HKEY_CURRENT_USER, the updater's copy jumped over, a
    /// recordless card's activation accepted, the display wrapper loaded first, the card lists
    /// extended to the cards past Konami's release, and the PE checksum written, 956 bytes in all.
    /// </summary>
    public const string PublicSha256 = "f67fe4b18658b293bde28efa104a6f68082c699ef3834bf46dbb2477aa248560";

    /// <summary>Whether the file at <paramref name="path"/> is the supported client.</summary>
    public static bool IsSupported(string path) =>
        new FileInfo(path).Length == Length && HashOf(path) == Sha256;

    public static string HashOf(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string HashOf(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}
