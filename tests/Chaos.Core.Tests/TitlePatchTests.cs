using System.Buffers.Binary;
using Chaos.Client;

namespace Chaos.Tests;

public class TitlePatchTests
{
    static byte[] Hex(string text) => Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    static readonly byte[] RoutineBytes = Hex("""
        0fb7c12d001000003d0010000073145150e84f0000005985c074088d7004e9ccf7adff33f6b820b67100e943f8adff
        25ffff00008d9000f0ffff81fa00100000731e52e81d00000085c0740c8b00a9000000ff7503c20400b810101000c2
        0400e957f9adff803da430b90000754fc605a430b9000168e18ab900b97030b900e82c9786ffb97030b900e832e79d
        ff85c074268b0d7c30b90083e910721981384348414f75118178045354544c7508c1e906394808760233c0a3a030b9
        00a1a030b90085c074188b4c24043b4808730dc1e1068d44081080780400750233c0c204006368616f735f7469746c
        65732e62696e00
        """);

    const uint Name = 0xB98A00, Colour = 0xB98A2F, Record = 0xB98A65, FileName = 0xB98AE1;
    const uint Tried = 0xB930A4, Loaded = 0xB930A0, FileObject = 0xB93070;

    // Branches, decoded from the listing.
    static readonly (uint Address, int Length, uint Target)[] Branches =
    [
        (0xB98A0D, 2, 0xB98A23),
        (0xB98A11, 5, Record),
        (0xB98A19, 2, 0xB98A23),
        (0xB98A1E, 5, 0x6781EF),    // jump back with our name
        (0xB98A2A, 5, 0x678272),    // Konami's table
        (0xB98A40, 2, 0xB98A60),
        (0xB98A43, 5, Record),
        (0xB98A4A, 2, 0xB98A58),
        (0xB98A53, 2, 0xB98A58),
        (0xB98A60, 5, 0x6783BC),    // the client's colours
        (0xB98A6C, 2, 0xB98ABD),
        (0xB98A7F, 5, 0x4021B0),    // file object constructor
        (0xB98A89, 5, 0x5771C0),    // its bytes
        (0xB98A90, 2, 0xB98AB8),
        (0xB98A9B, 2, 0xB98AB6),
        (0xB98AA3, 2, 0xB98AB6),
        (0xB98AAC, 2, 0xB98AB6),
        (0xB98AB4, 2, 0xB98AB8),
        (0xB98AC4, 2, 0xB98ADE),
        (0xB98ACD, 2, 0xB98ADC),
        (0xB98ADA, 2, 0xB98ADE),
    ];

    static readonly (int At, uint Address)[] Addresses =
    [
        (0x67, Tried),
        (0x70, Tried),
        (0x76, FileName),
        (0x7B, FileObject),
        (0x85, FileObject),
        (0x94, FileObject + 0xC),   // byte count
        (0xB9, Loaded),
        (0xBE, Loaded),
    ];

    static byte[] PatchedImage() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);

    static ClientPatcher.SitePatch Site(uint va) => ClientPatcher.OwnTitleSites.Single(site => site.Va == va);

    [Fact]
    public void RoutinesMatchListing()
    {
        Assert.Equal(242, RoutineBytes.Length);
        Assert.Equal(Convert.ToHexStringLower(RoutineBytes), Convert.ToHexStringLower(ClientPatcher.OwnTitlesStub));
        Assert.Equal((Name, Colour, Record, FileName),
                     (ClientPatcher.OwnTitleNameVa, ClientPatcher.OwnTitleColourVa, ClientPatcher.OwnTitleRecordVa, ClientPatcher.TitlesFileNameVa));
        Assert.Equal("chaos_titles.bin\0"u8.ToArray(), RoutineBytes[(int)(FileName - Name)..]);
        Assert.Equal(Titles.TitleNames.FileName, "chaos_titles.bin");
        foreach (var (at, address) in Addresses)
            Assert.Equal(address, BinaryPrimitives.ReadUInt32LittleEndian(RoutineBytes.AsSpan(at)));
        Assert.True(FileObject >= ClientPatcher.TitlesStateVa && Tried < ClientPatcher.TitlesStateVa + ClientPatcher.TitlesStateLength);
        Assert.Equal(0xB93100u, ClientPatcher.TitlesStateVa + (uint)ClientPatcher.TitlesStateLength);
        Assert.True(Name + RoutineBytes.Length <= 0xB98C00);
        // Magic "CHAO" "STTL", 16-byte header, 64-byte records.
        Assert.Equal("CHAO"u8.ToArray(), RoutineBytes[0x9F..0xA3]);
        Assert.Equal("STTL"u8.ToArray(), RoutineBytes[0xA8..0xAC]);
        Assert.Equal([0x83, 0xE9, 0x10], RoutineBytes[0x98..0x9B]);
        Assert.Equal([0xC1, 0xE9, 0x06], RoutineBytes[0xAE..0xB1]);
        Assert.Equal([0x8D, 0x44, 0x08, 0x10], RoutineBytes[0xD2..0xD6]);
        Assert.Equal(0x1000, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x04)));
        Assert.Equal(0x1000, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x09)));
        Assert.Equal(-0x1000, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x36)));
        Assert.Equal(0x1000, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x3C)));
        Assert.Equal(0x101010, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x59)));
    }

    [Fact]
    public void BranchesMatchListing()
    {
        foreach (var (address, length, target) in Branches)
        {
            int at = (int)(address - Name);
            Assert.True(length == 2 || RoutineBytes[at] is 0xE8 or 0xE9, $"0x{address:x} is no jump or call");
            int relative = length == 2 ? (sbyte)RoutineBytes[at + 1] : BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(at + 1));
            Assert.Equal(target, (uint)(address + (uint)length + relative));
        }
    }

    [Fact]
    public void SitesJumpToRoutines()
    {
        static uint Relative(ClientPatcher.SitePatch site) =>
            site.Va + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(site.Patched.AsSpan(1));
        Assert.Equal(2, ClientPatcher.OwnTitleSites.Length);
        Assert.Equal(0xE9, Site(0x67826B).Patched[0]);
        Assert.Equal(Name, Relative(Site(0x67826B)));
        Assert.Equal([0x90, 0x90], Site(0x67826B).Patched[5..]);
        Assert.Equal(0xE9, Site(0x6783B7).Patched[0]);
        Assert.Equal(Colour, Relative(Site(0x6783B7)));
        Assert.Equal(Site(0x67826B).Original, RoutineBytes[0x23..0x2A]);
        Assert.Equal(0x678272u, Site(0x67826B).Va + (uint)Site(0x67826B).Original.Length);
        Assert.Equal(Site(0x6783B7).Original, RoutineBytes[0x2F..0x34]);
        Assert.Equal(0x6783BCu, Site(0x6783B7).Va + (uint)Site(0x6783B7).Original.Length);
        foreach (var site in ClientPatcher.OwnTitleSites)
            Assert.Equal(site.Original.Length, site.Patched.Length);
    }

    [Fact]
    public void WritesSitesAndRoutines()
    {
        var patched = PatchedImage();
        foreach (var site in ClientPatcher.OwnTitleSites)
            Assert.Equal(site.Patched, patched.AsSpan(TestImage.SiteOffset(site.Va), site.Patched.Length).ToArray());
        Assert.Equal(RoutineBytes, patched.AsSpan(TestImage.ChaosOffset(Name), RoutineBytes.Length).ToArray());
        var image = new PeImage(patched);
        Assert.Equal(0xB93100u, image.ImageBase + TestImage.DataRva + image.GetSection(".data").VirtualSize);
        // Raw size unchanged.
        Assert.Equal(0x1000u, image.GetSection(".data").RawSize);
    }

    public static TheoryData<uint, int> Places => new()
    {
        { 0x67826B, 0 },
        { 0x67826B, 6 },
        { 0x6783B7, 0 },
        { 0x6783B7, 4 },
    };

    [Theory]
    [MemberData(nameof(Places))]
    public void RejectsChangedSite(uint va, int at)
    {
        var image = TestImage.Build();
        image[TestImage.SiteOffset(va) + at] ^= 0x01;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
        var patched = PatchedImage();
        patched[TestImage.SiteOffset(va) + at] ^= 0x01;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(patched, ServerEndpoints.Public));
    }

    [Fact]
    public void NoSitesWithoutRoutines()
    {
        Assert.Throws<PatchException>(() => ClientPatcher.NameOwnTitles(new PeImage(TestImage.Build())));
        foreach (uint va in new[] { Name, Colour + 3, Record + 0x40, FileName + 5, FileName + 16 })
        {
            var image = new PeImage(TestImage.Build());
            ClientPatcher.BuildChaosSection(image);
            image.Write(va, [(byte)(image.Read(va, 1)[0] ^ 0x01)]);
            Assert.Throws<PatchException>(() => ClientPatcher.NameOwnTitles(image));
            foreach (var site in ClientPatcher.OwnTitleSites)
                Assert.Equal(site.Original, image.Read(site.Va, site.Original.Length).ToArray());
        }
    }

    [Fact]
    public void KeepsDataReachingState()
    {
        var once = PatchedImage();
        Assert.Equal(once, ClientPatcher.Patch(once, ServerEndpoints.Public));
    }

    // Other data in the state area would be overwritten.
    [Theory]
    [InlineData(0x10u)]
    [InlineData(0x8Fu)]
    [InlineData(0x91u)]
    [InlineData(0xF90u)]
    public void RejectsDataOverlappingState(uint past)
    {
        var data = TestImage.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TestImage.DataVirtualSizeOffset), TestImage.DataVirtualSize + past);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(data, ServerEndpoints.Public));
    }

    // Shorter is fine: the loader zero-fills past the raw data.
    [Theory]
    [InlineData(0x1000u)]
    [InlineData(TestImage.DataVirtualSize - 1)]
    public void ExtendsShortData(uint size)
    {
        var data = TestImage.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TestImage.DataVirtualSizeOffset), size);
        var patched = new PeImage(ClientPatcher.Patch(data, ServerEndpoints.Public));
        Assert.Equal(TestImage.DataVirtualSize + 0x90, patched.GetSection(".data").VirtualSize);
    }

    [RetailClientFact]
    public void PatchesRetailClient()
    {
        var retail = new PeImage(File.ReadAllBytes(RetailClient.Path!));
        foreach (var site in ClientPatcher.OwnTitleSites)
            Assert.Equal(site.Original, retail.Read(site.Va, site.Original.Length).ToArray());
        var data = retail.GetSection(".data");
        Assert.Equal((0x30F000u, 0x484070u, 0x16000u), (data.VirtualAddress, data.VirtualSize, data.RawSize));
        Assert.Equal(0x794000u, retail.GetSection(".rsrc").VirtualAddress);
        Assert.Equal(Hex("b9 d0 44 72 00 e8 3d 00 00 00"), retail.Read(0x402169, 10).ToArray());
        Assert.Equal(Hex("b9 d0 44 72 00 e8 24 50 17 00"), retail.Read(0x402192, 10).ToArray());
        Assert.Equal(Hex("8a 41 24 84 c0 8b 41 08 74 03 83 c0 04 c3"), retail.Read(0x5771C0, 14).ToArray());

        var patched = new PeImage(ClientPatcher.Patch(retail.Bytes, ServerEndpoints.Public));
        foreach (var site in ClientPatcher.OwnTitleSites)
            Assert.Equal(site.Patched, patched.Read(site.Va, site.Patched.Length).ToArray());
        Assert.Equal(RoutineBytes, patched.Read(Name, RoutineBytes.Length).ToArray());
        Assert.Equal(0x484100u, patched.GetSection(".data").VirtualSize);
        Assert.Equal(retail.SizeOfImage + 0x4000, patched.SizeOfImage);
    }
}
