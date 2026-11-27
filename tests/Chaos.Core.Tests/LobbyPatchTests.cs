using System.Buffers.Binary;
using Chaos.Client;

namespace Chaos.Tests;

public class LobbyPatchTests
{
    static byte[] Hex(string text) => Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    static readonly byte[] RoutineBytes = Hex("""
        c6413100837c2410237c146681bfca00000001f0740983fe017e04c6413101e98ba7acffc64131000fbf412c3de80300
        007d176681bfca00000001f0741083fe027c07837c2410237d04c6413101e95ca7acffc64610018a4531340188462c84
        c07508c6461100c64612008a452fc38b6c242c8b7c2428807e2c007505bd60000000e9ac9cacff
        """);

    static readonly byte[] NumberBytes = Hex("6681 7d2ce803 7d0a 6820a97100 e96ca4acff 68af88b900 e962a4acff");
    static readonly byte[] RegionBytes = Hex("80792f07 0f850570b0ff 6681792ce803 0f8df96fb0ff e9ed6fb0ff");

    static readonly byte[] EventBytes = Hex("""
        c64131000fbf412c3de80300007d176681bfca00000001f0741083fe027c07837c2410007d04c6413101e980a6acff
        80782f09 0f85cb6fb0ff 6681782ce803 0f8dbf6fb0ff e9b36fb0ff
        80782f08 0f854870b0ff 6681782ce803 0f8d3c70b0ff e93070b0ff
        """);

    const uint Rank1 = 0xB98800, Rank7 = 0xB98824, Fill = 0xB98853, Draw = 0xB9886F;
    const uint Number = 0xB98890, Region = 0xB988B0;
    const uint Rank8 = 0xB98900, Trial = 0xB9892F, Championship = 0xB9894A;

    // Branches, decoded from the listing.
    static readonly (uint Address, int Length, uint Target)[] Branches =
    [
        (0xB98809, 2, 0xB9881F),
        (0xB98814, 2, 0xB9881F),
        (0xB98819, 2, 0xB9881F),
        (0xB9881F, 5, 0x662FAF),
        (0xB98831, 2, 0xB9884A),
        (0xB9883C, 2, 0xB9884E),
        (0xB98841, 2, 0xB9884A),
        (0xB98848, 2, 0xB9884E),
        (0xB9884E, 5, 0x662FAF),
        (0xB98861, 2, 0xB9886B),
        (0xB9887B, 2, 0xB98882),
        (0xB98882, 5, 0x662533),    // back into the draw
        (0xB98896, 2, 0xB988A2),
        (0xB9889D, 5, 0x662D0E),
        (0xB988A7, 5, 0x662D0E),
        (0xB988B4, 6, 0x69F8BF),    // the next record
        (0xB988C0, 6, 0x69F8BF),
        (0xB988C6, 5, 0x69F8B8),
        (0xB9890D, 2, 0xB98926),
        (0xB98918, 2, 0xB9892A),
        (0xB9891D, 2, 0xB98926),
        (0xB98924, 2, 0xB9892A),
        (0xB9892A, 5, 0x662FAF),
        (0xB98933, 6, 0x69F904),
        (0xB9893F, 6, 0x69F904),
        (0xB98945, 5, 0x69F8FD),
        (0xB9894E, 6, 0x69F99C),
        (0xB9895A, 6, 0x69F99C),
        (0xB98960, 5, 0x69F995),
    ];

    static byte[] PatchedImage() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);

    static byte[] Code(uint address) => address switch
    {
        >= Rank8 => EventBytes,
        >= Region => RegionBytes,
        >= Number => NumberBytes,
        _ => RoutineBytes,
    };

    static uint Base(uint address) =>
        address >= Rank8 ? Rank8 : address >= Region ? Region : address >= Number ? Number : Rank1;

    [Fact]
    public void RoutinesMatchListing()
    {
        Assert.Equal(135, RoutineBytes.Length);
        Assert.Equal(28, NumberBytes.Length);
        Assert.Equal(27, RegionBytes.Length);
        Assert.Equal(101, EventBytes.Length);
        Assert.Equal(Convert.ToHexStringLower(RoutineBytes), Convert.ToHexStringLower(ClientPatcher.LockedLobbyStub));
        Assert.Equal(Convert.ToHexStringLower(NumberBytes), Convert.ToHexStringLower(ClientPatcher.ClosedNumberStub));
        Assert.Equal(Convert.ToHexStringLower(RegionBytes), Convert.ToHexStringLower(ClientPatcher.RegionEffectStub));
        Assert.Equal(Convert.ToHexStringLower(EventBytes), Convert.ToHexStringLower(ClientPatcher.EventLobbyStub));
        Assert.Equal((Rank1, Number, Region), (ClientPatcher.LockedLobbyVa, ClientPatcher.ClosedNumberVa, ClientPatcher.RegionEffectVa));
        Assert.Equal(Rank8, ClientPatcher.EventLobbyVa);
        Assert.True(Rank1 + RoutineBytes.Length <= Number);
        Assert.True(Number + NumberBytes.Length < ClientPatcher.EmptyStringVa);
        Assert.True(ClientPatcher.EmptyStringVa < Region);
        Assert.True(Region + RegionBytes.Length <= 0xB98900);
        // Event routines fit after it, before the title code.
        Assert.True(Rank8 >= 0xB98900);
        Assert.True(Rank8 + EventBytes.Length <= ClientPatcher.OwnTitleNameVa);
        Assert.Equal(RoutineBytes[0x24..0x47], EventBytes[..0x23]);
        Assert.Equal(0x23, RoutineBytes[0x47]);
        Assert.Equal(0x00, EventBytes[0x23]);
        Assert.Equal(RoutineBytes[0x48..0x4F], EventBytes[0x24..0x2B]);
        foreach (var (at, rank) in new[] { (0x2F, 9), (0x4A, 8) })
        {
            Assert.Equal([0x80, 0x78, 0x2F, (byte)rank], EventBytes[at..(at + 4)]);
            Assert.Equal([0x0F, 0x85], EventBytes[(at + 4)..(at + 6)]);
            Assert.Equal([0x66, 0x81, 0x78, 0x2C, 0xE8, 0x03], EventBytes[(at + 10)..(at + 16)]);
            Assert.Equal([0x0F, 0x8D], EventBytes[(at + 16)..(at + 18)]);
            Assert.Equal(0xE9, EventBytes[at + 22]);
        }
        Assert.Equal(Trial, Rank8 + 0x2F);
        Assert.Equal(Championship, Rank8 + 0x4A);
        // The closed number pushes the empty string.
        Assert.Equal(ClientPatcher.EmptyStringVa, BinaryPrimitives.ReadUInt32LittleEndian(NumberBytes.AsSpan(0x13)));
        Assert.Equal(0xF001, BinaryPrimitives.ReadUInt16LittleEndian(RoutineBytes.AsSpan(0x12)));
        Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x2D)));
        Assert.Equal(1000, BinaryPrimitives.ReadUInt16LittleEndian(NumberBytes.AsSpan(4)));
        Assert.Equal(1000, BinaryPrimitives.ReadUInt16LittleEndian(RegionBytes.AsSpan(14)));
        // A faded plate is drawn at 0x60.
        Assert.Equal(0x60, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x7E)));
    }

    [Fact]
    public void BranchesMatchListing()
    {
        foreach (var (address, length, target) in Branches)
        {
            var code = Code(address);
            int at = (int)(address - Base(address));
            int relative = length == 2 ? (sbyte)code[at + 1] : BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(at + length - 4));
            Assert.Equal(target, (uint)(address + (uint)length + relative));
        }
    }

    [Fact]
    public void SitesJumpToRoutines()
    {
        static ClientPatcher.SitePatch Site(uint va) => ClientPatcher.LobbySites.Single(site => site.Va == va);
        static uint Relative(ClientPatcher.SitePatch site) =>
            site.Va + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(site.Patched.AsSpan(1));
        // New slot addresses.
        Assert.Equal(Rank1, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x662FE8).Patched));
        Assert.Equal(Rank7, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x662FF8).Patched));
        Assert.Equal(Rank7, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x663000).Patched));
        Assert.Equal(Rank8, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x663004).Patched));
        Assert.Equal(Rank7, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x663008).Patched));
        // The client's original slot values.
        Assert.Equal(0x662F23u, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x662FE8).Original));
        Assert.Equal(0x662F7Eu, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x662FF8).Original));
        Assert.Equal(0x662F7Eu, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x663000).Original));
        Assert.Equal(0x662F97u, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x663004).Original));
        Assert.Equal(0x662F7Eu, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x663008).Original));
        Assert.Equal(0xE8, Site(0x662D7F).Patched[0]);
        Assert.Equal(Fill, Relative(Site(0x662D7F)));
        Assert.Equal([0x90, 0x90], Site(0x662D7F).Patched[5..]);
        // The rest are jumps.
        Assert.Equal(0xE9, Site(0x66252B).Patched[0]);
        Assert.Equal(Draw, Relative(Site(0x66252B)));
        Assert.Equal([0x90, 0x90, 0x90], Site(0x66252B).Patched[5..]);
        Assert.Equal(0xE9, Site(0x662D09).Patched[0]);
        Assert.Equal(Number, Relative(Site(0x662D09)));
        Assert.Equal(0xE9, Site(0x69F8B2).Patched[0]);
        Assert.Equal(Region, Relative(Site(0x69F8B2)));
        Assert.Equal(0x90, Site(0x69F8B2).Patched[5]);
        Assert.Equal(0xE9, Site(0x69F8F7).Patched[0]);
        Assert.Equal(Trial, Relative(Site(0x69F8F7)));
        Assert.Equal(0x90, Site(0x69F8F7).Patched[5]);
        Assert.Equal(0xE9, Site(0x69F98F).Patched[0]);
        Assert.Equal(Championship, Relative(Site(0x69F98F)));
        Assert.Equal(0x90, Site(0x69F98F).Patched[5]);
        Assert.Equal([0xC7, 0x40, 0x30, 0xFF, 0x00, 0x00, 0x00], Site(0x67AE15).Patched);
        // Fill and draw rerun the code they replace.
        Assert.Equal(Site(0x662D7F).Original[..4], RoutineBytes[0x53..0x57]);
        Assert.Equal(Site(0x662D7F).Original[4..], RoutineBytes[0x6B..0x6E]);
        Assert.Equal(Site(0x66252B).Original, RoutineBytes[0x6F..0x77]);
        Assert.Equal(Site(0x662D09).Original, NumberBytes[8..13]);
        Assert.Equal(Site(0x69F8B2).Original[..4], RegionBytes[..4]);
        Assert.Equal(Site(0x69F8F7).Original[..4], EventBytes[0x2F..0x33]);
        Assert.Equal(Site(0x69F98F).Original[..4], EventBytes[0x4A..0x4E]);
        foreach (var site in ClientPatcher.LobbySites)
            Assert.Equal(site.Original.Length, site.Patched.Length);
        Assert.Equal(12, ClientPatcher.LobbySites.Length);
    }

    [Fact]
    public void WritesSitesAndRoutines()
    {
        var patched = PatchedImage();
        foreach (var site in ClientPatcher.LobbySites)
            Assert.Equal(site.Patched, patched.AsSpan(TestImage.SiteOffset(site.Va), site.Patched.Length).ToArray());
        Assert.Equal(RoutineBytes, patched.AsSpan(TestImage.ChaosOffset(Rank1), RoutineBytes.Length).ToArray());
        Assert.Equal(NumberBytes, patched.AsSpan(TestImage.ChaosOffset(Number), NumberBytes.Length).ToArray());
        Assert.Equal(RegionBytes, patched.AsSpan(TestImage.ChaosOffset(Region), RegionBytes.Length).ToArray());
        Assert.Equal(EventBytes, patched.AsSpan(TestImage.ChaosOffset(Rank8), EventBytes.Length).ToArray());
        Assert.Equal(0, patched[TestImage.ChaosOffset(ClientPatcher.EmptyStringVa)]);
        Assert.Equal(Rank8, BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(TestImage.SiteOffset(0x663004))));
        Assert.Equal(Rank7, BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(TestImage.SiteOffset(0x663008))));
        Assert.Equal(Hex("e933904f00 90"), patched.AsSpan(TestImage.SiteOffset(0x69F8F7), 6).ToArray());
        Assert.Equal(Hex("e9b68f4f00 90"), patched.AsSpan(TestImage.SiteOffset(0x69F98F), 6).ToArray());
        // Ranks 2, 3, 4 and 6 keep their slots.
        foreach (uint slot in new uint[] { 0x662FEC, 0x662FF0, 0x662FF4, 0x662FFC })
            Assert.Equal(TestImage.Build().AsSpan(TestImage.SiteOffset(slot), 4).ToArray(), patched.AsSpan(TestImage.SiteOffset(slot), 4).ToArray());
    }

    public static TheoryData<uint, int> Places => new()
    {
        { 0x662FE8, 0 },
        { 0x662FF8, 1 },
        { 0x663000, 3 },
        { 0x663004, 0 },
        { 0x663008, 2 },
        { 0x662D7F, 6 },
        { 0x66252B, 4 },
        { 0x67AE15, 2 },
        { 0x662D09, 0 },
        { 0x69F8B2, 5 },
        { 0x69F8F7, 3 },
        { 0x69F98F, 4 },
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
        Assert.Throws<PatchException>(() => ClientPatcher.GreyLockedLobbies(new PeImage(TestImage.Build())));
        foreach (uint va in new[] { Rank1 + 0x40, Number + 3, ClientPatcher.EmptyStringVa, Region + 26, Rank8 + 0x23, Trial + 3, Championship + 26 })
        {
            var image = new PeImage(TestImage.Build());
            ClientPatcher.BuildChaosSection(image);
            image.Write(va, [(byte)(image.Read(va, 1)[0] ^ 0x01)]);
            Assert.Throws<PatchException>(() => ClientPatcher.GreyLockedLobbies(image));
        }
    }

    [RetailClientFact]
    public void PatchesRetailClient()
    {
        var retail = new PeImage(File.ReadAllBytes(RetailClient.Path!));
        foreach (var site in ClientPatcher.LobbySites)
            Assert.Equal(site.Original, retail.Read(site.Va, site.Original.Length).ToArray());
        var patched = new PeImage(ClientPatcher.Patch(retail.Bytes, ServerEndpoints.Public));
        foreach (var site in ClientPatcher.LobbySites)
            Assert.Equal(site.Patched, patched.Read(site.Va, site.Patched.Length).ToArray());
        Assert.Equal(RoutineBytes, patched.Read(Rank1, RoutineBytes.Length).ToArray());
        Assert.Equal(NumberBytes, patched.Read(Number, NumberBytes.Length).ToArray());
        Assert.Equal(RegionBytes, patched.Read(Region, RegionBytes.Length).ToArray());
        Assert.Equal(EventBytes, patched.Read(Rank8, EventBytes.Length).ToArray());
        Assert.Equal(0, patched.Read(ClientPatcher.EmptyStringVa, 1)[0]);
        // Open plates keep the "% 5d" format.
        Assert.Equal("% 5d\0"u8.ToArray(), patched.Read(0x71A920, 5).ToArray());
        Assert.Equal(Hex("6681bfca00000001f0 740d 83fe02 7c12 8b442410 85c0 7c0a"), retail.Read(0x662F97, 24).ToArray());
        // Return targets.
        Assert.Equal(0x51, retail.Read(0x662FAF, 1)[0]);
        foreach (uint next in new uint[] { 0x69F904, 0x69F99C })
            Assert.Equal(Hex("8b54241c"), retail.Read(next, 4).ToArray());
        foreach (uint index in new uint[] { 0x69F8FD, 0x69F995 })
            Assert.Equal(Hex("8a5030"), retail.Read(index, 3).ToArray());
        // Ranks 2, 3, 4 and 6 are unchanged.
        foreach (var (slot, rule) in new[] { (0x662FECu, 0x662F40u), (0x662FF0u, 0x662F57u), (0x662FF4u, 0x662F6Eu), (0x662FFCu, 0x662F7Eu) })
            Assert.Equal(rule, BinaryPrimitives.ReadUInt32LittleEndian(patched.Read(slot, 4)));
    }
}
