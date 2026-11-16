using System.Text;

namespace Chaos.Client;

public static partial class ClientPatcher
{
    // The client's web links all lead to Konami's long-gone sites: the News and the login screen's
    // link, the "?" manual, the MENU's star, the DUELPASS failure page, the two pages it fetches
    // itself at login and the duel-log download. Each is built from one of 17 strings in .data, and
    // every use measures its string when it runs (repne scasb at 0x66b6c4, 0x66de1a and the like)
    // or passes it on as a C string, so a shorter one needs nothing else. Each becomes the same path
    // on our website, then zeros up to and including the client's own NUL; Korean, which had a
    // host of its own, gets the same one.
    internal const string Website = "https://chaos-rebirth.com";

    /// <summary>One of the client's web links.</summary>
    /// <param name="Va">Where its string is.</param>
    /// <param name="Retail">The client's string.</param>
    /// <param name="Ours">The string written in its place, no longer.</param>
    internal sealed record WebLink(uint Va, string Retail, string Ours);

    internal static readonly WebLink[] WebLinks =
    [
        // The root: DUELPASS, the login fetches, and the News, message 0x1d and the star for a
        // language past 6.
        new(0x715824, "http://www.yugioh-online.net/", "https://chaos-rebirth.com/"),
        new(0x7161AC, "http://download.yugioh-online.net/duellogs/last", "https://chaos-rebirth.com/duellogs/last"),
        // The Korean root, for DUELPASS (0x63dc1c).
        new(0x7192B8, "http://www.yugioh-online.ne.kr/", "https://chaos-rebirth.com/"),
        // The News, message 0x1d and the star by language, through sub_66b660, and the login
        // screen's table at 0x71a63c.
        new(0x71A658, "http://www.yugioh-online.ne.kr/top/korean/", "https://chaos-rebirth.com/top/korean/"),
        new(0x71A684, "http://www.yugioh-online.net/top/spanish/", "https://chaos-rebirth.com/top/spanish/"),
        new(0x71A6B0, "http://www.yugioh-online.net/top/italian/", "https://chaos-rebirth.com/top/italian/"),
        new(0x71A6DC, "http://www.yugioh-online.net/top/french/", "https://chaos-rebirth.com/top/french/"),
        new(0x71A708, "http://www.yugioh-online.net/top/german/", "https://chaos-rebirth.com/top/german/"),
        new(0x71A734, "http://www.yugioh-online.net/top/english/", "https://chaos-rebirth.com/top/english/"),
        new(0x71A760, "http://www.yugioh-online.net/top/japanese/", "https://chaos-rebirth.com/top/japanese/"),
        // The manual by language, through sub_66ddc0.
        new(0x71B304, "http://www.yugioh-online.net/man/english/", "https://chaos-rebirth.com/man/english/"),
        new(0x71B330, "http://www.yugioh-online.ne.kr/man/korean/", "https://chaos-rebirth.com/man/korean/"),
        new(0x71B35C, "http://www.yugioh-online.net/man/spanish/", "https://chaos-rebirth.com/man/spanish/"),
        new(0x71B388, "http://www.yugioh-online.net/man/italian/", "https://chaos-rebirth.com/man/italian/"),
        new(0x71B3B4, "http://www.yugioh-online.net/man/french/", "https://chaos-rebirth.com/man/french/"),
        new(0x71B3E0, "http://www.yugioh-online.net/man/german/", "https://chaos-rebirth.com/man/german/"),
        new(0x71B40C, "http://www.yugioh-online.net/man/japanese/", "https://chaos-rebirth.com/man/japanese/"),
    ];

    // The MENU's star (message 0x21, 0x66da97) opens the News page's "index.html" (0x71b1a4), the
    // same page as message 0x1d. It gets a page of its own, GUNSLINGER's, named in .chaos: the two
    // instructions that read the name, the strlen's source and the push of the string sub_402890
    // takes, point there instead. The name is relative to /top/<language>/, as "index.html" was,
    // and message 0x1d keeps "index.html".
    internal const uint StarPageVa = 0xB98C00;
    internal static ReadOnlySpan<byte> StarPage => "event/gunslinger/index.html\0"u8;

    internal static readonly SitePatch[] StarSites =
    [
        // mov edi, 0x71b1a4, for the repne scasb at 0x66dac2
        new("the star's strlen", 0x66DAAF, [0xBF, 0xA4, 0xB1, 0x71, 0x00], [0xBF, 0x00, 0x8C, 0xB9, 0x00]),  // mov edi, 0xb98c00
        // push 0x71b1a4, the string sub_402890 takes (0x66dae4)
        new("the star's name", 0x66DACF, [0x68, 0xA4, 0xB1, 0x71, 0x00], [0x68, 0x00, 0x8C, 0xB9, 0x00]),   // push 0xb98c00
    ];

    /// <summary>
    /// Points the client's web links at <see cref="Website"/>, and the star at its own page there,
    /// whose name <see cref="BuildChaosSection"/> has written.
    /// </summary>
    internal static void PointWebLinks(PeImage image)
    {
        var writes = new List<(uint Va, byte[] Bytes)>();
        foreach (var link in WebLinks)
        {
            // The client's string with its NUL, or ours written here before.
            byte[] retail = Encoding.ASCII.GetBytes(link.Retail + "\0");
            if (link.Ours.Length >= retail.Length)
                throw new PatchException($"'{link.Ours}' does not fit where the client has '{link.Retail}'");
            var ours = new byte[retail.Length];
            Encoding.ASCII.GetBytes(link.Ours, ours);
            var found = image.Read(link.Va, retail.Length);
            if (!found.SequenceEqual(ours) && !found.SequenceEqual(retail))
                throw new PatchException($"expected \"{link.Retail}\" at 0x{link.Va:x}, found {Convert.ToHexString(found)}");
            writes.Add((link.Va, ours));
        }
        RequireInChaos(image, StarPageVa, StarPage, "the star's page name");
        PatchSites(image, StarSites);
        foreach (var (va, bytes) in writes)
            image.Write(va, bytes);
    }
}
