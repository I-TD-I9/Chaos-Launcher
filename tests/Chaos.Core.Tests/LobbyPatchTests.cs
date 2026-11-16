using System.Buffers.Binary;
using Chaos.Client;

namespace Chaos.Tests;

public class LobbyPatchTests
{
    static byte[] Hex(string text) => Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    // The 135 bytes of the lobby menu's routines as listed, from 0xb98800: rank1, rank7, fill, draw.
    static readonly byte[] RoutineBytes = Hex("""
        c6413100837c2410237c146681bfca00000001f0740983fe017e04c6413101e98ba7acffc64131000fbf412c3de80300
        007d176681bfca00000001f0741083fe027c07837c2410237d04c6413101e95ca7acffc64610018a4531340188462c84
        c07508c6461100c64612008a452fc38b6c242c8b7c2428807e2c007505bd60000000e9ac9cacff
        """);

    // The closed plate's number, 28 bytes from 0xb98890, and the region screen's test, 27 bytes from 0xb988b0.
    static readonly byte[] NumberBytes = Hex("6681 7d2ce803 7d0a 6820a97100 e96ca4acff 68af88b900 e962a4acff");
    static readonly byte[] RegionBytes = Hex("80792f07 0f850570b0ff 6681792ce803 0f8df96fb0ff e9ed6fb0ff");

    const uint Rank1 = 0xB98800, Rank7 = 0xB98824, Fill = 0xB98853, Draw = 0xB9886F;
    const uint Number = 0xB98890, Region = 0xB988B0;

    // Each jump in the routines, decoded from the listing: where the instruction is, how long it is
    // and where it goes.
    static readonly (uint Address, int Length, uint Target)[] Branches =
    [
        (0xB98809, 2, 0xB9881F),
        (0xB98814, 2, 0xB9881F),
        (0xB98819, 2, 0xB9881F),
        (0xB9881F, 5, 0x662FAF),    // the add
        (0xB98831, 2, 0xB9884A),
        (0xB9883C, 2, 0xB9884E),
        (0xB98841, 2, 0xB9884A),
        (0xB98848, 2, 0xB9884E),
        (0xB9884E, 5, 0x662FAF),
        (0xB98861, 2, 0xB9886B),
        (0xB9887B, 2, 0xB98882),
        (0xB98882, 5, 0x662533),    // the draw, after its two loads
        (0xB98896, 2, 0xB988A2),
        (0xB9889D, 5, 0x662D0E),    // push edx, after the push of the format
        (0xB988A7, 5, 0x662D0E),
        (0xB988B4, 6, 0x69F8BF),    // the next record
        (0xB988C0, 6, 0x69F8BF),
        (0xB988C6, 5, 0x69F8B8),    // the test of the area
    ];

    static byte[] PatchedImage() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);

    static byte[] Code(uint address) => address switch
    {
        >= Region => RegionBytes,
        >= Number => NumberBytes,
        _ => RoutineBytes,
    };

    static uint Base(uint address) => address >= Region ? Region : address >= Number ? Number : Rank1;

    [Fact]
    public void The_routines_are_the_listed_ones()
    {
        Assert.Equal(135, RoutineBytes.Length);
        Assert.Equal(28, NumberBytes.Length);
        Assert.Equal(27, RegionBytes.Length);
        Assert.Equal(Convert.ToHexStringLower(RoutineBytes), Convert.ToHexStringLower(ClientPatcher.LockedLobbyStub));
        Assert.Equal(Convert.ToHexStringLower(NumberBytes), Convert.ToHexStringLower(ClientPatcher.ClosedNumberStub));
        Assert.Equal(Convert.ToHexStringLower(RegionBytes), Convert.ToHexStringLower(ClientPatcher.RegionEffectStub));
        Assert.Equal((Rank1, Number, Region), (ClientPatcher.LockedLobbyVa, ClientPatcher.ClosedNumberVa, ClientPatcher.RegionEffectVa));
        // They fit in the room booked for them, 0xb98800 to 0xb98900, without meeting.
        Assert.True(Rank1 + RoutineBytes.Length <= Number);
        Assert.True(Number + NumberBytes.Length < ClientPatcher.EmptyStringVa);
        Assert.True(ClientPatcher.EmptyStringVa < Region);
        Assert.True(Region + RegionBytes.Length <= 0xB98900);
        // The closed number pushes the empty string.
        Assert.Equal(ClientPatcher.EmptyStringVa, BinaryPrimitives.ReadUInt32LittleEndian(NumberBytes.AsSpan(0x13)));
        // GAME MASTER is 0xf001, and the closed mark a load of 1000.
        Assert.Equal(0xF001, BinaryPrimitives.ReadUInt16LittleEndian(RoutineBytes.AsSpan(0x12)));
        Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x2D)));
        Assert.Equal(1000, BinaryPrimitives.ReadUInt16LittleEndian(NumberBytes.AsSpan(4)));
        Assert.Equal(1000, BinaryPrimitives.ReadUInt16LittleEndian(RegionBytes.AsSpan(14)));
        // A faded plate is drawn at 0x60.
        Assert.Equal(0x60, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x7E)));
    }

    [Fact]
    public void Every_jump_in_the_routines_lands_where_the_listing_says()
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
    public void Each_place_goes_to_its_routine()
    {
        static ClientPatcher.SitePatch Site(uint va) => ClientPatcher.LobbySites.Single(site => site.Va == va);
        static uint Relative(ClientPatcher.SitePatch site) =>
            site.Va + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(site.Patched.AsSpan(1));
        // The table's slots, as addresses: rank 1 to its own, ranks 5 and 7 to rank7's.
        Assert.Equal(Rank1, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x662FE8).Patched));
        Assert.Equal(Rank7, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x662FF8).Patched));
        Assert.Equal(Rank7, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x663000).Patched));
        // The client's rules the slots held: 0x662f23 for rank 1, 0x662f7e for ranks 5 to 7 and 9.
        Assert.Equal(0x662F23u, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x662FE8).Original));
        Assert.Equal(0x662F7Eu, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x662FF8).Original));
        Assert.Equal(0x662F7Eu, BinaryPrimitives.ReadUInt32LittleEndian(Site(0x663000).Original));
        // The fill is called, and returns to the nops before the instruction after the seven bytes.
        Assert.Equal(0xE8, Site(0x662D7F).Patched[0]);
        Assert.Equal(Fill, Relative(Site(0x662D7F)));
        Assert.Equal([0x90, 0x90], Site(0x662D7F).Patched[5..]);
        // The others are jumped to.
        Assert.Equal(0xE9, Site(0x66252B).Patched[0]);
        Assert.Equal(Draw, Relative(Site(0x66252B)));
        Assert.Equal([0x90, 0x90, 0x90], Site(0x66252B).Patched[5..]);
        Assert.Equal(0xE9, Site(0x662D09).Patched[0]);
        Assert.Equal(Number, Relative(Site(0x662D09)));
        Assert.Equal(0xE9, Site(0x69F8B2).Patched[0]);
        Assert.Equal(Region, Relative(Site(0x69F8B2)));
        Assert.Equal(0x90, Site(0x69F8B2).Patched[5]);
        // The record's constructor writes the same bytes at +0x30 and +0x32, and +0x31 zero.
        Assert.Equal([0xC7, 0x40, 0x30, 0xFF, 0x00, 0x00, 0x00], Site(0x67AE15).Patched);
        // The fill and draw repeat what they replace.
        Assert.Equal(Site(0x662D7F).Original[..4], RoutineBytes[0x53..0x57]);
        Assert.Equal(Site(0x662D7F).Original[4..], RoutineBytes[0x6B..0x6E]);
        Assert.Equal(Site(0x66252B).Original, RoutineBytes[0x6F..0x77]);
        Assert.Equal(Site(0x662D09).Original, NumberBytes[8..13]);
        Assert.Equal(Site(0x69F8B2).Original[..4], RegionBytes[..4]);
        foreach (var site in ClientPatcher.LobbySites)
            Assert.Equal(site.Original.Length, site.Patched.Length);
        Assert.Equal(8, ClientPatcher.LobbySites.Length);
    }

    [Fact]
    public void The_places_are_written_and_their_routines_are_in_chaos()
    {
        var patched = PatchedImage();
        foreach (var site in ClientPatcher.LobbySites)
            Assert.Equal(site.Patched, patched.AsSpan(TestImage.SiteOffset(site.Va), site.Patched.Length).ToArray());
        Assert.Equal(RoutineBytes, patched.AsSpan(TestImage.ChaosOffset(Rank1), RoutineBytes.Length).ToArray());
        Assert.Equal(NumberBytes, patched.AsSpan(TestImage.ChaosOffset(Number), NumberBytes.Length).ToArray());
        Assert.Equal(RegionBytes, patched.AsSpan(TestImage.ChaosOffset(Region), RegionBytes.Length).ToArray());
        Assert.Equal(0, patched[TestImage.ChaosOffset(ClientPatcher.EmptyStringVa)]);
        // Ranks 2, 3, 4, 6, 8 and 9 keep their slots.
        foreach (uint slot in new uint[] { 0x662FEC, 0x662FF0, 0x662FF4, 0x662FFC, 0x663004, 0x663008 })
            Assert.Equal(TestImage.Build().AsSpan(TestImage.SiteOffset(slot), 4).ToArray(), patched.AsSpan(TestImage.SiteOffset(slot), 4).ToArray());
    }

    public static TheoryData<uint, int> Places => new()
    {
        { 0x662FE8, 0 },
        { 0x662FF8, 1 },
        { 0x663000, 3 },
        { 0x662D7F, 6 },
        { 0x66252B, 4 },
        { 0x67AE15, 2 },
        { 0x662D09, 0 },
        { 0x69F8B2, 5 },
    };

    [Theory]
    [MemberData(nameof(Places))]
    public void A_place_with_other_bytes_is_refused(uint va, int at)
    {
        var image = TestImage.Build();
        image[TestImage.SiteOffset(va) + at] ^= 0x01;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
        var patched = PatchedImage();
        patched[TestImage.SiteOffset(va) + at] ^= 0x01;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(patched, ServerEndpoints.Public));
    }

    [Fact]
    public void The_places_are_not_written_without_their_routines_in_chaos()
    {
        Assert.Throws<PatchException>(() => ClientPatcher.GreyLockedLobbies(new PeImage(TestImage.Build())));
        foreach (uint va in new[] { Rank1 + 0x40, Number + 3, ClientPatcher.EmptyStringVa, Region + 26 })
        {
            var image = new PeImage(TestImage.Build());
            ClientPatcher.BuildChaosSection(image);
            image.Write(va, [(byte)(image.Read(va, 1)[0] ^ 0x01)]);
            Assert.Throws<PatchException>(() => ClientPatcher.GreyLockedLobbies(image));
        }
    }

    [RetailClientFact]
    public void The_retail_client_has_the_bytes_the_patch_expects_and_gets_its_own()
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
        Assert.Equal(0, patched.Read(ClientPatcher.EmptyStringVa, 1)[0]);
        // The format the open plate keeps, "% 5d".
        Assert.Equal("% 5d\0"u8.ToArray(), patched.Read(0x71A920, 5).ToArray());
    }
}
