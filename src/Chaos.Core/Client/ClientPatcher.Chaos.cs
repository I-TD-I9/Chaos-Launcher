namespace Chaos.Client;

public static partial class ClientPatcher
{
    internal const string ChaosSection = ".chaos";
    internal const uint ChaosVa = 0xB98000;
    internal const uint ChaosSize = 0x4000;
    internal const uint ChaosEnd = ChaosVa + ChaosSize;
    // Executable and readable, like .text.
    internal const uint ChaosCharacteristics = 0x60000020;

    internal static void EnsureChaosSection(PeImage image)
    {
        if (!image.HasSection(ChaosSection))
        {
            var added = image.AddSection(ChaosSection, ChaosSize, ChaosCharacteristics);
            if (image.ImageBase + added.VirtualAddress != ChaosVa)
                throw new PatchException($"{ChaosSection} would start at 0x{image.ImageBase + added.VirtualAddress:x}, not 0x{ChaosVa:x}");
            return;
        }

        var chaos = image.GetSection(ChaosSection);
        var others = image.Sections.Where(s => s.Name != ChaosSection).ToArray();
        bool last = image.Sections[^1].Name == ChaosSection &&
                    others.All(s => s.VirtualAddress + (ulong)Math.Max(s.VirtualSize, s.RawSize) <= chaos.VirtualAddress &&
                                    s.RawOffset + (ulong)s.RawSize <= chaos.RawOffset);
        if (!last || image.ImageBase + chaos.VirtualAddress != ChaosVa || chaos.VirtualSize != ChaosSize ||
            chaos.RawSize != ChaosSize || chaos.RawOffset + chaos.RawSize != (uint)image.Bytes.Length ||
            image.GetCharacteristics(ChaosSection) != ChaosCharacteristics ||
            image.SizeOfImage != ChaosEnd - image.ImageBase)
            throw new PatchException($"unexpected {ChaosSection} section: {chaos}");
    }

    internal static byte[] ChaosContents()
    {
        var contents = new byte[ChaosSize];
        LockedLobbyStub.CopyTo(contents.AsSpan((int)(LockedLobbyVa - ChaosVa)));
        ClosedNumberStub.CopyTo(contents.AsSpan((int)(ClosedNumberVa - ChaosVa)));
        RegionEffectStub.CopyTo(contents.AsSpan((int)(RegionEffectVa - ChaosVa)));
        EventLobbyStub.CopyTo(contents.AsSpan((int)(EventLobbyVa - ChaosVa)));
        OwnTitlesStub.CopyTo(contents.AsSpan((int)(OwnTitleNameVa - ChaosVa)));
        StarPage.CopyTo(contents.AsSpan((int)(StarPageVa - ChaosVa)));
        ClosedPlateStub.CopyTo(contents.AsSpan((int)(ClosedPlateVa - ChaosVa)));
        return contents;
    }

    internal static void BuildChaosSection(PeImage image)
    {
        var contents = ChaosContents();
        EnsureChaosSection(image);
        image.Write(ChaosVa, contents);
    }

    static void RequireInChaos(PeImage image, uint va, ReadOnlySpan<byte> code, string what)
    {
        if (!image.HasSection(ChaosSection) || !image.Read(va, code.Length).SequenceEqual(code))
            throw new PatchException($"{what} not found at 0x{va:x}");
    }

    internal sealed record SitePatch(string Name, uint Va, byte[] Original, byte[] Patched);

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
