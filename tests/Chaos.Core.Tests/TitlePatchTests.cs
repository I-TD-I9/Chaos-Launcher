using System.Buffers.Binary;
using Chaos.Client;

namespace Chaos.Tests;

public class TitlePatchTests
{
    static byte[] Hex(string text) => Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    // The titles' routines as listed, 225 bytes from 0xb98a00, then "chaos_titles.bin" and its zero.
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

    // Each jump and call in the routines, decoded from the listing: where the instruction is, how
    // long it is and where it goes.
    static readonly (uint Address, int Length, uint Target)[] Branches =
    [
        (0xB98A0D, 2, 0xB98A23),
        (0xB98A11, 5, Record),
        (0xB98A19, 2, 0xB98A23),
        (0xB98A1E, 5, 0x6781EF),    // a card's name made into the result
        (0xB98A2A, 5, 0x678272),    // Konami's table
        (0xB98A40, 2, 0xB98A60),
        (0xB98A43, 5, Record),
        (0xB98A4A, 2, 0xB98A58),
        (0xB98A53, 2, 0xB98A58),
        (0xB98A60, 5, 0x6783BC),    // the client's colours
        (0xB98A6C, 2, 0xB98ABD),
        (0xB98A7F, 5, 0x4021B0),    // the file object's constructor
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

    // Where the routines name an address of the state or the file's name, as offsets into them.
    static readonly (int At, uint Address)[] Addresses =
    [
        (0x67, Tried),
        (0x70, Tried),
        (0x76, FileName),
        (0x7B, FileObject),
        (0x85, FileObject),
        (0x94, FileObject + 0xC),   // the bytes' size
        (0xB9, Loaded),
        (0xBE, Loaded),
    ];

    static byte[] PatchedImage() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);

    static ClientPatcher.SitePatch Site(uint va) => ClientPatcher.OwnTitleSites.Single(site => site.Va == va);

    [Fact]
    public void The_routines_are_the_listed_ones()
    {
        Assert.Equal(242, RoutineBytes.Length);
        Assert.Equal(Convert.ToHexStringLower(RoutineBytes), Convert.ToHexStringLower(ClientPatcher.OwnTitlesStub));
        Assert.Equal((Name, Colour, Record, FileName),
                     (ClientPatcher.OwnTitleNameVa, ClientPatcher.OwnTitleColourVa, ClientPatcher.OwnTitleRecordVa, ClientPatcher.TitlesFileNameVa));
        Assert.Equal("chaos_titles.bin\0"u8.ToArray(), RoutineBytes[(int)(FileName - Name)..]);
        Assert.Equal(Titles.TitleNames.FileName, "chaos_titles.bin");
        foreach (var (at, address) in Addresses)
            Assert.Equal(address, BinaryPrimitives.ReadUInt32LittleEndian(RoutineBytes.AsSpan(at)));
        // The state lies in the room .data is given, the routines in the titles' room of .chaos.
        Assert.True(FileObject >= ClientPatcher.TitlesStateVa && Tried < ClientPatcher.TitlesStateVa + ClientPatcher.TitlesStateLength);
        Assert.Equal(0xB93100u, ClientPatcher.TitlesStateVa + (uint)ClientPatcher.TitlesStateLength);
        Assert.True(Name + RoutineBytes.Length <= 0xB98C00);
        // The file's magic, "CHAO" and "STTL", and its 16-byte header and 64-byte records.
        Assert.Equal("CHAO"u8.ToArray(), RoutineBytes[0x9F..0xA3]);
        Assert.Equal("STTL"u8.ToArray(), RoutineBytes[0xA8..0xAC]);
        Assert.Equal([0x83, 0xE9, 0x10], RoutineBytes[0x98..0x9B]);
        Assert.Equal([0xC1, 0xE9, 0x06], RoutineBytes[0xAE..0xB1]);
        Assert.Equal([0x8D, 0x44, 0x08, 0x10], RoutineBytes[0xD2..0xD6]);
        // Ours are 0x1000 to 0x1fff, and the client's own colour for any other is 0x101010.
        Assert.Equal(0x1000, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x04)));
        Assert.Equal(0x1000, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x09)));
        Assert.Equal(-0x1000, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x36)));
        Assert.Equal(0x1000, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x3C)));
        Assert.Equal(0x101010, BinaryPrimitives.ReadInt32LittleEndian(RoutineBytes.AsSpan(0x59)));
    }

    [Fact]
    public void Every_jump_in_the_routines_lands_where_the_listing_says()
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
    public void Each_place_jumps_to_its_routine_and_the_routines_repeat_what_they_replace()
    {
        static uint Relative(ClientPatcher.SitePatch site) =>
            site.Va + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(site.Patched.AsSpan(1));
        Assert.Equal(2, ClientPatcher.OwnTitleSites.Length);
        Assert.Equal(0xE9, Site(0x67826B).Patched[0]);
        Assert.Equal(Name, Relative(Site(0x67826B)));
        Assert.Equal([0x90, 0x90], Site(0x67826B).Patched[5..]);
        Assert.Equal(0xE9, Site(0x6783B7).Patched[0]);
        Assert.Equal(Colour, Relative(Site(0x6783B7)));
        // xor esi, esi; mov eax, 0x71b620 before the jump to 0x678272, the instruction after them;
        // and eax, 0xffff at the colour's start, before the jump back to 0x6783bc.
        Assert.Equal(Site(0x67826B).Original, RoutineBytes[0x23..0x2A]);
        Assert.Equal(0x678272u, Site(0x67826B).Va + (uint)Site(0x67826B).Original.Length);
        Assert.Equal(Site(0x6783B7).Original, RoutineBytes[0x2F..0x34]);
        Assert.Equal(0x6783BCu, Site(0x6783B7).Va + (uint)Site(0x6783B7).Original.Length);
        foreach (var site in ClientPatcher.OwnTitleSites)
            Assert.Equal(site.Original.Length, site.Patched.Length);
    }

    [Fact]
    public void The_places_are_written_the_routines_are_in_chaos_and_data_reaches_the_state()
    {
        var patched = PatchedImage();
        foreach (var site in ClientPatcher.OwnTitleSites)
            Assert.Equal(site.Patched, patched.AsSpan(TestImage.SiteOffset(site.Va), site.Patched.Length).ToArray());
        Assert.Equal(RoutineBytes, patched.AsSpan(TestImage.ChaosOffset(Name), RoutineBytes.Length).ToArray());
        var image = new PeImage(patched);
        Assert.Equal(0xB93100u, image.ImageBase + TestImage.DataRva + image.SectionNamed(".data").VirtualSize);
        // Its bytes on disk stay as they were.
        Assert.Equal(0x1000u, image.SectionNamed(".data").RawSize);
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

    // .data already reaching to the state's end is the launcher's own, and patched again it stays.
    [Fact]
    public void Data_that_reaches_the_states_end_already_is_kept()
    {
        var once = PatchedImage();
        Assert.Equal(once, ClientPatcher.Patch(once, ServerEndpoints.Public));
    }

    // Anything else that reaches into the state is data the routines would overwrite.
    [Theory]
    [InlineData(0x10u)]
    [InlineData(0x8Fu)]
    [InlineData(0x91u)]
    [InlineData(0xF90u)]
    public void Data_that_reaches_into_the_state_is_refused(uint past)
    {
        var data = TestImage.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TestImage.DataVirtualSizeOffset), TestImage.DataVirtualSize + past);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(data, ServerEndpoints.Public));
    }

    // Shorter is fine: the loader gives every byte past the ones on disk as zeros.
    [Theory]
    [InlineData(0x1000u)]
    [InlineData(TestImage.DataVirtualSize - 1)]
    public void Data_that_ends_sooner_is_made_to_reach_the_state(uint size)
    {
        var data = TestImage.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TestImage.DataVirtualSizeOffset), size);
        var patched = new PeImage(ClientPatcher.Patch(data, ServerEndpoints.Public));
        Assert.Equal(TestImage.DataVirtualSize + 0x90, patched.SectionNamed(".data").VirtualSize);
    }

    [RetailClientFact]
    public void The_retail_client_has_the_bytes_the_patch_expects_and_gets_its_own()
    {
        var retail = new PeImage(File.ReadAllBytes(RetailClient.Path!));
        foreach (var site in ClientPatcher.OwnTitleSites)
            Assert.Equal(site.Original, retail.Read(site.Va, site.Original.Length).ToArray());
        var data = retail.SectionNamed(".data");
        Assert.Equal((0x30F000u, 0x484070u, 0x16000u), (data.VirtualAddress, data.VirtualSize, data.RawSize));
        Assert.Equal(0x794000u, retail.SectionNamed(".rsrc").VirtualAddress);
        // What the routines call, as sub_402150 calls it for CARD_ID.bin: the file object's
        // constructor (mov ecx, 0x7244d0; call 0x4021b0) and then for its bytes (mov ecx,
        // 0x7244d0; call 0x5771c0), which are [ecx + 8], or 4 past it for a scrambled file.
        Assert.Equal(Hex("b9 d0 44 72 00 e8 3d 00 00 00"), retail.Read(0x402169, 10).ToArray());
        Assert.Equal(Hex("b9 d0 44 72 00 e8 24 50 17 00"), retail.Read(0x402192, 10).ToArray());
        Assert.Equal(Hex("8a 41 24 84 c0 8b 41 08 74 03 83 c0 04 c3"), retail.Read(0x5771C0, 14).ToArray());

        var patched = new PeImage(ClientPatcher.Patch(retail.Bytes, ServerEndpoints.Public));
        foreach (var site in ClientPatcher.OwnTitleSites)
            Assert.Equal(site.Patched, patched.Read(site.Va, site.Patched.Length).ToArray());
        Assert.Equal(RoutineBytes, patched.Read(Name, RoutineBytes.Length).ToArray());
        Assert.Equal(0x484100u, patched.SectionNamed(".data").VirtualSize);
        Assert.Equal(retail.SizeOfImage + 0x4000, patched.SizeOfImage);
    }
}
