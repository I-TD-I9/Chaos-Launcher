using System.Buffers.Binary;
using System.Text;

namespace Chaos.Tests;

/// <summary>
/// A small PE32 image laid out like the game's client where the launcher patches it: the jump
/// past the updater's copy at 0x4149a8, the registry root's push at 0x4a40db, the call that makes
/// DirectDraw at 0x4db103, the activation check's jump at 0x5e2338 and the port's instruction at
/// 0x67a9f2, each in a code section; the end of .text, which reaches 0x6ec792 in memory and
/// 0x6ed000 on disk, the rest zeros; the gate's host name in a data section at 0x71e064, which
/// reaches 0xb93070 in memory as the client's .data does; in four
/// more code sections the nine places the card lists are extended from (<see cref="ListSites"/>);
/// in two more and the port's the places that jump into .chaos or read from it
/// (<see cref="ChaosSites"/>); the client's web links in a data section of their own
/// (<see cref="WebLinks"/>); and a last section that ends in memory at 0x7972e0, as the client's
/// .rsrc does, so that .chaos goes at 0xb98000. The real client cannot be part of the repository.
/// </summary>
static class TestImage
{
    public const int PeOffset = 0x80;
    public const int UpdaterRaw = 0x400;
    public const int RegistryRaw = 0x1400;
    public const int CodeRaw = 0x2400;
    public const int DataRaw = 0x3400;
    public const int DirectDrawRaw = 0x4400;
    public const int ActivationRaw = 0x5400;
    public const int TextRaw = 0x6400;
    public const int ListsRaw = 0x7400;
    public const int LobbyRaw = 0xC400;
    public const int RegionRaw = 0x23400;
    public const int LinksRaw = 0x24400;
    public const int ResourceRaw = 0x2B400;
    public const int Length = 0x2B800;
    public const uint UpdaterRva = 0x14000;
    public const uint RegistryRva = 0xA4000;
    public const uint CodeRva = 0x27A000;
    public const uint DataRva = 0x31E000;
    // To 0xb93070, as the client's .data, so that the titles' state goes past it at 0xb93070.
    public const uint DataVirtualSize = 0xB93070 - 0x400000 - DataRva;
    public const uint DirectDrawRva = 0xDB000;
    public const uint ActivationRva = 0x1E2000;
    public const uint TextRva = 0x2EC000;
    // 0x662000 to 0x679000, the lobby menu, the plates, the star and the titles' two routines;
    // 0x69f000, the region screen; 0x715000 to 0x71c000, the web links.
    public const uint LobbyRva = 0x262000;
    public const int LobbySize = 0x17000;
    public const uint RegionRva = 0x29F000;
    public const uint LinksRva = 0x315000;
    public const int LinksSize = 0x7000;
    public const uint ResourceRva = 0x794000;
    public const uint ResourceVirtualSize = 0x32E0;

    // The headers as the client's give them where adding a section reads them, but for the
    // alignment on disk, which is smaller here to keep the image small.
    public const uint SectionAlignment = 0x1000;
    public const uint FileAlignment = 0x200;
    public const uint SizeOfHeaders = 0x400;
    public const uint SizeOfImage = 0x798000;
    public const uint SizeOfCode = 0x9000;
    public const int SectionCount = 15;

    // The code sections that hold the card lists' places, each of whole pages: 0x59e000 to
    // 0x5a0000, 0x5db000, 0x5bc000 and 0x5e5000, each 0x1000 long but the first.
    static readonly (uint Rva, int Raw, int Size)[] ListSections =
    [
        (0x19E000, ListsRaw, 0x2000),
        (0x1DB000, ListsRaw + 0x2000, 0x1000),
        (0x1BC000, ListsRaw + 0x3000, 0x1000),
        (0x1E5000, ListsRaw + 0x4000, 0x1000),
    ];

    /// <summary>
    /// The client's bytes at the nine places the card lists jump from, by virtual address. They
    /// are listed again here, from the client, apart from the patcher's own.
    /// </summary>
    public static readonly (uint Va, byte[] Bytes)[] ListSites =
    [
        (0x59EFE0, [0x56, 0x8B, 0x74, 0x24, 0x08]),
        (0x59F460, [0x8B, 0x44, 0x24, 0x04, 0x50]),
        (0x59E5A0, [0x8B, 0x44, 0x24, 0x04, 0x50]),
        (0x59F510, [0x56, 0x8B, 0x74, 0x24, 0x08]),
        (0x5DB110, [0x8B, 0x4C, 0x24, 0x0C, 0x56]),
        (0x5BC863, [0x66, 0x8B, 0x17, 0x52, 0xE8, 0xE4, 0x58, 0xE4, 0xFF]),
        (0x5E5355, [0x8B, 0x7C, 0x24, 0x1C, 0x57]),
        (0x5E53FF, [0x3D, 0xF0, 0x05, 0x00, 0x00, 0x75, 0x47]),
        (0x5E5390, [0xBB, 0x00, 0x0C, 0x00, 0x00]),
    ];

    /// <summary>
    /// The client's bytes at the places that jump into .chaos or read from it, by virtual address,
    /// from the client: the lobby menu's three slots, the plate's fill and draw, the record's
    /// constructor, the plate's number and the region screen's test, then the star's two, then
    /// the title's name and colour.
    /// </summary>
    public static readonly (uint Va, byte[] Bytes)[] ChaosSites =
    [
        (0x662FE8, [0x23, 0x2F, 0x66, 0x00]),
        (0x662FF8, [0x7E, 0x2F, 0x66, 0x00]),
        (0x663000, [0x7E, 0x2F, 0x66, 0x00]),
        (0x662D7F, [0xC6, 0x46, 0x10, 0x01, 0x8A, 0x45, 0x2F]),
        (0x66252B, [0x8B, 0x6C, 0x24, 0x2C, 0x8B, 0x7C, 0x24, 0x28]),
        (0x67AE15, [0x88, 0x48, 0x30, 0x66, 0x89, 0x58, 0x32]),
        (0x662D09, [0x68, 0x20, 0xA9, 0x71, 0x00]),
        (0x69F8B2, [0x80, 0x79, 0x2F, 0x07, 0x75, 0x07]),
        (0x66DAAF, [0xBF, 0xA4, 0xB1, 0x71, 0x00]),
        (0x66DACF, [0x68, 0xA4, 0xB1, 0x71, 0x00]),
        (0x67826B, [0x33, 0xF6, 0xB8, 0x20, 0xB6, 0x71, 0x00]),
        (0x6783B7, [0x25, 0xFF, 0xFF, 0x00, 0x00]),
    ];

    /// <summary>The client's 17 web links, by virtual address, from the client.</summary>
    public static readonly (uint Va, string Url)[] WebLinks =
    [
        (0x715824, "http://www.yugioh-online.net/"),
        (0x7161AC, "http://download.yugioh-online.net/duellogs/last"),
        (0x7192B8, "http://www.yugioh-online.ne.kr/"),
        (0x71A658, "http://www.yugioh-online.ne.kr/top/korean/"),
        (0x71A684, "http://www.yugioh-online.net/top/spanish/"),
        (0x71A6B0, "http://www.yugioh-online.net/top/italian/"),
        (0x71A6DC, "http://www.yugioh-online.net/top/french/"),
        (0x71A708, "http://www.yugioh-online.net/top/german/"),
        (0x71A734, "http://www.yugioh-online.net/top/english/"),
        (0x71A760, "http://www.yugioh-online.net/top/japanese/"),
        (0x71B304, "http://www.yugioh-online.net/man/english/"),
        (0x71B330, "http://www.yugioh-online.ne.kr/man/korean/"),
        (0x71B35C, "http://www.yugioh-online.net/man/spanish/"),
        (0x71B388, "http://www.yugioh-online.net/man/italian/"),
        (0x71B3B4, "http://www.yugioh-online.net/man/french/"),
        (0x71B3E0, "http://www.yugioh-online.net/man/german/"),
        (0x71B40C, "http://www.yugioh-online.net/man/japanese/"),
    ];

    /// <summary>"index.html", which message 0x1d keeps and the star no longer reads.</summary>
    public const uint IndexPageVa = 0x71B1A4;

    /// <summary>
    /// Where the card lists' routine begins in the zeros of .text, and how far the routine, map
    /// and table may reach: up to the procedure check, which is 98 bytes long.
    /// </summary>
    public const uint CardCaveVa = 0x6EC7A0;
    public const int CardCaveLength = 0x7C0;
    public static int CardCaveOffset => TextRaw + (int)(CardCaveVa - 0x400000 - TextRva);
    public const uint ProcHeldVa = 0x6ECF60;
    public const int ProcHeldLength = 98;
    public static int ProcHeldOffset => TextRaw + (int)(ProcHeldVa - 0x400000 - TextRva);

    /// <summary>Where .chaos goes, in memory and in the file, and how long it is.</summary>
    public const uint ChaosVa = 0xB98000;
    public const int ChaosRaw = Length;
    public const int ChaosLength = 0x4000;

    /// <summary>The file offset of the byte at <paramref name="va"/> in .chaos, once it is there.</summary>
    public static int ChaosOffset(uint va) => ChaosRaw + (int)(va - ChaosVa);

    /// <summary>
    /// The file offset of the byte at <paramref name="va"/> in the sections of the places that jump
    /// into .chaos or of the web links.
    /// </summary>
    public static int SiteOffset(uint va)
    {
        foreach (var (rva, raw, size) in new[] { (LobbyRva, LobbyRaw, LobbySize), (RegionRva, RegionRaw, 0x1000),
                                                 (CodeRva, CodeRaw, 0x1000), (LinksRva, LinksRaw, LinksSize) })
            if (va - 0x400000 >= rva && va - 0x400000 < rva + size)
                return raw + (int)(va - 0x400000 - rva);
        throw new ArgumentOutOfRangeException(nameof(va), $"0x{va:x} is in none of the sections of the places that jump into .chaos");
    }

    /// <summary>The file offset of the byte at <paramref name="va"/> in one of the card lists' sections.</summary>
    public static int ListOffset(uint va)
    {
        foreach (var (rva, raw, size) in ListSections)
            if (va - 0x400000 >= rva && va - 0x400000 < rva + size)
                return raw + (int)(va - 0x400000 - rva);
        throw new ArgumentOutOfRangeException(nameof(va), $"0x{va:x} is in none of the card lists' sections");
    }

    /// <summary>How far .text reaches in memory: to 0x6ec792, as in the client.</summary>
    public const uint TextVirtualSize = 0x792;

    public const uint UpdaterVa = 0x4149A8;
    public const uint RegistryVa = 0x4A40DB;
    public const uint HostVa = 0x71E064;
    public const uint PortVa = 0x67A9F2;
    public const uint DirectDrawVa = 0x4DB103;
    public const uint ActivationVa = 0x5E2338;
    public const uint StubVa = 0x6ECFD0;

    public static int UpdaterOffset => UpdaterRaw + (int)(UpdaterVa - 0x400000 - UpdaterRva);
    public static int RegistryOffset => RegistryRaw + (int)(RegistryVa - 0x400000 - RegistryRva);
    public static int HostOffset => DataRaw + (int)(HostVa - 0x400000 - DataRva);
    public static int PortOffset => CodeRaw + (int)(PortVa - 0x400000 - CodeRva);
    public static int DirectDrawOffset => DirectDrawRaw + (int)(DirectDrawVa - 0x400000 - DirectDrawRva);
    public static int ActivationOffset => ActivationRaw + (int)(ActivationVa - 0x400000 - ActivationRva);
    public static int StubOffset => TextRaw + (int)(StubVa - 0x400000 - TextRva);
    public static int ChecksumOffset => PeOffset + 24 + 64;
    public static int OptionalHeader => PeOffset + 24;

    // The section table follows the optional header; .text is the seventh of its entries, and the
    // card lists' sections come after it, then the sections of the places that jump into .chaos,
    // the web links' and the last one.
    public static int SectionTable => PeOffset + 24 + 0xE0;
    public static int TextVirtualSizeOffset => SectionTable + 6 * 40 + 8;
    public static int DataVirtualSizeOffset => SectionTable + 3 * 40 + 8;

    /// <summary>Where the header of a section after the image's own goes.</summary>
    public static int NextSectionHeader => SectionTable + SectionCount * 40;

    public static byte[] Build(string host = "yugi-gate01.yugioh-online.net", ushort port = 5730)
    {
        var data = new byte[Length];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x3C), PeOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(PeOffset), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(PeOffset + 4), 0x14C);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(PeOffset + 6), SectionCount);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(PeOffset + 20), 0xE0);
        int optional = OptionalHeader;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(optional), 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(optional + 4), SizeOfCode);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(optional + 28), 0x400000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(optional + 32), SectionAlignment);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(optional + 36), FileAlignment);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(optional + 56), SizeOfImage);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(optional + 60), SizeOfHeaders);
        int table = SectionTable;
        Section(data, table, ".text1", UpdaterRva, 0x1000, UpdaterRaw, 0x1000);
        Section(data, table + 40, ".text2", RegistryRva, 0x1000, RegistryRaw, 0x1000);
        Section(data, table + 80, ".text3", CodeRva, 0x1000, CodeRaw, 0x1000);
        // Larger in memory than on disk, as the client's own .data is.
        Section(data, table + 120, ".data", DataRva, DataVirtualSize, DataRaw, 0x1000);
        Section(data, table + 160, ".text4", DirectDrawRva, 0x1000, DirectDrawRaw, 0x1000);
        Section(data, table + 200, ".text5", ActivationRva, 0x1000, ActivationRaw, 0x1000);
        // Smaller in memory than on disk, as the client's own .text is.
        Section(data, table + 240, ".text", TextRva, TextVirtualSize, TextRaw, 0x1000);
        for (int i = 0; i < ListSections.Length; i++)
        {
            var (rva, raw, size) = ListSections[i];
            Section(data, table + 280 + i * 40, $".list{i}", rva, (uint)size, raw, size);
        }
        Section(data, table + 440, ".text6", LobbyRva, LobbySize, LobbyRaw, LobbySize);
        Section(data, table + 480, ".text7", RegionRva, 0x1000, RegionRaw, 0x1000);
        Section(data, table + 520, ".links", LinksRva, LinksSize, LinksRaw, LinksSize);
        Section(data, table + 560, ".rsrc", ResourceRva, ResourceVirtualSize, ResourceRaw, 0x400);

        new byte[] { 0x75, 0x3D }.CopyTo(data, UpdaterOffset);
        new byte[] { 0x68, 0x02, 0x00, 0x00, 0x80 }.CopyTo(data, RegistryOffset);
        Encoding.ASCII.GetBytes(host).CopyTo(data, HostOffset);
        Encoding.ASCII.GetBytes("version error.").CopyTo(data, HostOffset + 0x20);
        new byte[] { 0x66, 0xC7, 0x45, 0x14 }.CopyTo(data, PortOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(PortOffset + 4), port);
        new byte[] { 0xFF, 0x15, 0xE0, 0xD3, 0x6E, 0x00 }.CopyTo(data, DirectDrawOffset);
        new byte[] { 0x0F, 0x84, 0x90, 0x00, 0x00, 0x00 }.CopyTo(data, ActivationOffset);
        foreach (var (va, bytes) in ListSites)
            bytes.CopyTo(data, ListOffset(va));
        foreach (var (va, bytes) in ChaosSites)
            bytes.CopyTo(data, SiteOffset(va));
        foreach (var (va, url) in WebLinks)
            Encoding.ASCII.GetBytes(url).CopyTo(data, SiteOffset(va));
        Encoding.ASCII.GetBytes("index.html").CopyTo(data, SiteOffset(IndexPageVa));
        // Code elsewhere, so that the checksum has more than zeros to add up, and up to where
        // .text ends in memory.
        for (int i = 0; i < 0x200; i++)
            data[CodeRaw + 0x200 + i] = (byte)(i * 37 + 11);
        for (int i = 0; i < (int)TextVirtualSize; i++)
            data[TextRaw + i] = (byte)(i * 53 + 7);
        return data;
    }

    static void Section(byte[] data, int at, string name, uint rva, uint virtualSize, int raw, int rawSize)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(data, at);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 8), virtualSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 12), rva);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 16), (uint)rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 20), (uint)raw);
    }

    /// <summary>
    /// The PE checksum as pefile computes it, over 32-bit words with an end-around carry and then
    /// folded to 16 bits: a second way to the same number, to check the launcher's against.
    /// </summary>
    public static uint ReferenceChecksum(byte[] data, int checksumOffset)
    {
        ulong sum = 0;
        int remainder = data.Length % 4;
        int count = (data.Length + 3) / 4;
        for (int i = 0; i < count; i++)
        {
            if (i == checksumOffset / 4)
                continue;
            var word = new byte[4];
            Array.Copy(data, i * 4, word, 0, i + 1 == count && remainder != 0 ? remainder : 4);
            sum += BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (sum >= 1UL << 32)
                sum = (sum & 0xFFFFFFFF) + (sum >> 32);
        }
        sum = (sum & 0xFFFF) + (sum >> 16);
        sum += sum >> 16;
        return (uint)(sum & 0xFFFF) + (uint)data.Length;
    }
}
