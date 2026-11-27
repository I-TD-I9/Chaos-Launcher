using System.Text;

namespace Chaos.Client;

public static partial class ClientPatcher
{
    // Replaces the client's dead Konami links.
    internal const string Website = "https://chaos-rebirth.com";

    internal sealed record WebLink(uint Va, string Original, string Replacement);

    internal static readonly WebLink[] WebLinks =
    [
        new(0x715824, "http://www.yugioh-online.net/", "https://chaos-rebirth.com/"),
        new(0x7161AC, "http://download.yugioh-online.net/duellogs/last", "https://chaos-rebirth.com/duellogs/last"),
        new(0x7192B8, "http://www.yugioh-online.ne.kr/", "https://chaos-rebirth.com/"),
        new(0x71A658, "http://www.yugioh-online.ne.kr/top/korean/", "https://chaos-rebirth.com/top/korean/"),
        new(0x71A684, "http://www.yugioh-online.net/top/spanish/", "https://chaos-rebirth.com/top/spanish/"),
        new(0x71A6B0, "http://www.yugioh-online.net/top/italian/", "https://chaos-rebirth.com/top/italian/"),
        new(0x71A6DC, "http://www.yugioh-online.net/top/french/", "https://chaos-rebirth.com/top/french/"),
        new(0x71A708, "http://www.yugioh-online.net/top/german/", "https://chaos-rebirth.com/top/german/"),
        new(0x71A734, "http://www.yugioh-online.net/top/english/", "https://chaos-rebirth.com/top/english/"),
        new(0x71A760, "http://www.yugioh-online.net/top/japanese/", "https://chaos-rebirth.com/top/japanese/"),
        new(0x71B304, "http://www.yugioh-online.net/man/english/", "https://chaos-rebirth.com/man/english/"),
        new(0x71B330, "http://www.yugioh-online.ne.kr/man/korean/", "https://chaos-rebirth.com/man/korean/"),
        new(0x71B35C, "http://www.yugioh-online.net/man/spanish/", "https://chaos-rebirth.com/man/spanish/"),
        new(0x71B388, "http://www.yugioh-online.net/man/italian/", "https://chaos-rebirth.com/man/italian/"),
        new(0x71B3B4, "http://www.yugioh-online.net/man/french/", "https://chaos-rebirth.com/man/french/"),
        new(0x71B3E0, "http://www.yugioh-online.net/man/german/", "https://chaos-rebirth.com/man/german/"),
        new(0x71B40C, "http://www.yugioh-online.net/man/japanese/", "https://chaos-rebirth.com/man/japanese/"),
    ];

    internal const uint StarPageVa = 0xB98C00;
    internal static ReadOnlySpan<byte> StarPage => "event/gunslinger/index.html\0"u8;

    internal static readonly SitePatch[] StarSites =
    [
        new("star strlen", 0x66DAAF, [0xBF, 0xA4, 0xB1, 0x71, 0x00], [0xBF, 0x00, 0x8C, 0xB9, 0x00]),
        new("star name", 0x66DACF, [0x68, 0xA4, 0xB1, 0x71, 0x00], [0x68, 0x00, 0x8C, 0xB9, 0x00]),
    ];

    internal static void PointWebLinks(PeImage image)
    {
        var writes = new List<(uint Va, byte[] Bytes)>();
        foreach (var link in WebLinks)
        {
            // Original or already replaced.
            byte[] original = Encoding.ASCII.GetBytes(link.Original + "\0");
            if (link.Replacement.Length >= original.Length)
                throw new PatchException($"{link.Replacement} is longer than {link.Original}");
            var replacement = new byte[original.Length];
            Encoding.ASCII.GetBytes(link.Replacement, replacement);
            var found = image.Read(link.Va, original.Length);
            if (!found.SequenceEqual(replacement) && !found.SequenceEqual(original))
                throw new PatchException($"expected \"{link.Original}\" at 0x{link.Va:x}, found {Convert.ToHexString(found)}");
            writes.Add((link.Va, replacement));
        }
        RequireInChaos(image, StarPageVa, StarPage, "star page");
        PatchSites(image, StarSites);
        foreach (var (va, bytes) in writes)
            image.Write(va, bytes);
    }
}
