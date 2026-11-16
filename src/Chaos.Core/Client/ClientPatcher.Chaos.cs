namespace Chaos.Client;

public static partial class ClientPatcher
{
    // The code the greyed-out lobbies and the server's own titles need, and the star's page name,
    // do not fit in the zeros at the end of .text, so the copy gets a section of its own, .chaos, after the client's four. The
    // headers have room for its header (the section table ends at 0x298 and the headers at 0x1000,
    // zeros between), the client's sections end in memory at 0x7972e0, which puts it at 0xb98000,
    // and its file ends at 0x329000, which is where its bytes go. The client has no relocations, no
    // signature and no bound imports to keep right, and it never reads its own file.
    //
    // In the section: the lobby menu's routines from LockedLobbyVa, the closed plate's number at
    // ClosedNumberVa and the region screen's check at RegionEffectVa, the titles' routines and
    // their file's name from OwnTitleNameVa, and the star's page name at StarPageVa, zeros
    // everywhere else. 0xb98000-0xb98480 and 0xb99000 to the end are kept for the marked links,
    // the record-less gate and the card table, so each keeps the same address whichever comes
    // first. The whole section is written
    // each time, so whatever a client held there before, it patches to the same bytes.
    internal const string ChaosSection = ".chaos";
    internal const uint ChaosVa = 0xB98000;
    internal const uint ChaosSize = 0x4000;
    internal const uint ChaosEnd = ChaosVa + ChaosSize;
    // Code, executed and read, as .text is.
    internal const uint ChaosCharacteristics = 0x60000020;

    /// <summary>
    /// Gives the client the section .chaos, or checks that the one it has is the one added here:
    /// the last section in memory and in the file, at <see cref="ChaosVa"/>, of <see cref="ChaosSize"/>
    /// bytes, holding code.
    /// </summary>
    internal static void EnsureChaosSection(PeImage image)
    {
        if (!image.HasSection(ChaosSection))
        {
            var added = image.AddSection(ChaosSection, ChaosSize, ChaosCharacteristics);
            if (image.ImageBase + added.VirtualAddress != ChaosVa)
                throw new PatchException($"the client's sections end elsewhere than this client's: {ChaosSection} would start at 0x{image.ImageBase + added.VirtualAddress:x}, not 0x{ChaosVa:x}");
            return;
        }

        var chaos = image.SectionNamed(ChaosSection);
        var others = image.Sections.Where(s => s.Name != ChaosSection).ToArray();
        bool last = image.Sections[^1].Name == ChaosSection &&
                    others.All(s => s.VirtualAddress + (ulong)Math.Max(s.VirtualSize, s.RawSize) <= chaos.VirtualAddress &&
                                    s.RawOffset + (ulong)s.RawSize <= chaos.RawOffset);
        if (!last || image.ImageBase + chaos.VirtualAddress != ChaosVa || chaos.VirtualSize != ChaosSize ||
            chaos.RawSize != ChaosSize || chaos.RawOffset + chaos.RawSize != (uint)image.Bytes.Length ||
            image.CharacteristicsOf(ChaosSection) != ChaosCharacteristics ||
            image.SizeOfImage != ChaosEnd - image.ImageBase)
            throw new PatchException($"the client's {ChaosSection} section is not the launcher's: {chaos}, characteristics 0x{image.CharacteristicsOf(ChaosSection):x}, image size 0x{image.SizeOfImage:x}");
    }

    /// <summary>
    /// The bytes of .chaos from <see cref="ChaosVa"/>: the lobby menu's routines, the titles'
    /// routines and the star's page name, with zeros between and after.
    /// </summary>
    internal static byte[] ChaosContents()
    {
        var contents = new byte[ChaosSize];
        LockedLobbyStub.CopyTo(contents.AsSpan((int)(LockedLobbyVa - ChaosVa)));
        ClosedNumberStub.CopyTo(contents.AsSpan((int)(ClosedNumberVa - ChaosVa)));
        RegionEffectStub.CopyTo(contents.AsSpan((int)(RegionEffectVa - ChaosVa)));
        OwnTitlesStub.CopyTo(contents.AsSpan((int)(OwnTitleNameVa - ChaosVa)));
        StarPage.CopyTo(contents.AsSpan((int)(StarPageVa - ChaosVa)));
        return contents;
    }

    /// <summary>
    /// Gives the client .chaos and writes all of it. The places that jump into it are written
    /// afterwards, each by the step it belongs to, which first checks that its code is there.
    /// </summary>
    internal static void BuildChaosSection(PeImage image)
    {
        var contents = ChaosContents();
        EnsureChaosSection(image);
        image.Write(ChaosVa, contents);
    }

    /// <summary>
    /// Throws unless .chaos holds <paramref name="code"/> at <paramref name="va"/>, so that nothing
    /// is pointed at code that is not there.
    /// </summary>
    static void RequireInChaos(PeImage image, uint va, ReadOnlySpan<byte> code, string what)
    {
        if (!image.HasSection(ChaosSection) || !image.Read(va, code.Length).SequenceEqual(code))
            throw new PatchException($"{what} is not at 0x{va:x} in {ChaosSection}");
    }

    /// <summary>One place in the client that is changed: its bytes, and what they become.</summary>
    /// <param name="Name">What the place is.</param>
    /// <param name="Va">Where the bytes start.</param>
    /// <param name="Original">The client's bytes there.</param>
    /// <param name="Patched">The bytes written in their place, as many.</param>
    internal sealed record SitePatch(string Name, uint Va, byte[] Original, byte[] Patched);

    /// <summary>
    /// Writes every one of <paramref name="sites"/>, once each holds the client's bytes or the
    /// patched ones; a place that holds anything else is refused before any is written.
    /// </summary>
    static void PatchSites(PeImage image, IReadOnlyList<SitePatch> sites)
    {
        foreach (var site in sites)
        {
            var bytes = image.Read(site.Va, site.Original.Length);
            if (!bytes.SequenceEqual(site.Patched) && !bytes.SequenceEqual(site.Original))
                throw new PatchException($"expected {Convert.ToHexString(site.Original)} at 0x{site.Va:x} ({site.Name}), found {Convert.ToHexString(bytes)}");
        }
        foreach (var site in sites)
            image.Write(site.Va, site.Patched);
    }
}
