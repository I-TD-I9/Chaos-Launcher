using System.Buffers.Binary;
using System.Text;
using Chaos.Client;

namespace Chaos.Tests;

public class ChaosSectionTests
{
    static byte[] PatchedImage() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);

    static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));

    [Fact]
    public void The_section_follows_the_others_in_memory_and_in_the_file()
    {
        var patched = PatchedImage();
        int header = TestImage.NextSectionHeader;
        Assert.Equal(".chaos\0\0", Encoding.ASCII.GetString(patched, header, 8));
        Assert.Equal(0x4000u, U32(patched, header + 8));                    // VirtualSize
        Assert.Equal(0x798000u, U32(patched, header + 12));                 // VirtualAddress
        Assert.Equal(0x4000u, U32(patched, header + 16));                   // SizeOfRawData
        Assert.Equal((uint)TestImage.Length, U32(patched, header + 20));    // PointerToRawData
        Assert.Equal(0x60000020u, U32(patched, header + 36));               // code, execute, read
        Assert.All(patched.AsSpan(header + 24, 12).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(TestImage.SectionCount + 1, BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(TestImage.PeOffset + 6)));
        Assert.Equal(0x79C000u, U32(patched, TestImage.OptionalHeader + 56));          // SizeOfImage
        Assert.Equal(TestImage.SizeOfCode + 0x4000, U32(patched, TestImage.OptionalHeader + 4));
        Assert.Equal(TestImage.Length + 0x4000, patched.Length);

        var image = new PeImage(patched);
        Assert.Equal(new PeImage.Section(".chaos", 0x4000, 0x798000, 0x4000, (uint)TestImage.Length), image.Sections[^1]);
        Assert.Equal(TestImage.ChaosRaw, image.Offset(ClientPatcher.ChaosVa, 0x4000));
        Assert.Equal(0x60000020u, image.CharacteristicsOf(".chaos"));
    }

    [Fact]
    public void The_section_holds_the_lobby_and_title_routines_and_the_stars_page_and_zeros_else()
    {
        var patched = PatchedImage();
        Assert.Equal(ClientPatcher.ChaosContents(), patched[TestImage.ChaosRaw..]);
        var contents = ClientPatcher.ChaosContents();
        // Where each piece goes, as the plan for the section books it.
        (int At, byte[] Bytes)[] pieces =
        [
            (0x800, ClientPatcher.LockedLobbyStub.ToArray()),
            (0x890, ClientPatcher.ClosedNumberStub.ToArray()),
            (0x8B0, ClientPatcher.RegionEffectStub.ToArray()),
            (0xA00, ClientPatcher.OwnTitlesStub.ToArray()),
            (0xC00, ClientPatcher.StarPage.ToArray()),
        ];
        Assert.Equal([135, 28, 27, 242, 28], pieces.Select(piece => piece.Bytes.Length));
        var written = new bool[contents.Length];
        foreach (var (at, bytes) in pieces)
        {
            Assert.Equal(bytes, contents.AsSpan(at, bytes.Length).ToArray());
            Array.Fill(written, true, at, bytes.Length);
        }
        for (int i = 0; i < contents.Length; i++)
            if (!written[i])
                Assert.True(contents[i] == 0, $".chaos+0x{i:x} is not zero");
    }

    [Fact]
    public void The_room_kept_for_the_other_patches_is_zeros()
    {
        var contents = ClientPatcher.ChaosContents();
        // The marked links and the record-less gate, the rest of the titles' room, and the card
        // table.
        Assert.All(contents[..0x480], b => Assert.Equal(0, b));
        Assert.All(contents[0xAF2..0xC00], b => Assert.Equal(0, b));
        Assert.All(contents[0x1000..], b => Assert.Equal(0, b));
        // The empty string the closed plate's number is formatted with.
        Assert.Equal(0, contents[ClientPatcher.EmptyStringVa - ClientPatcher.ChaosVa]);
        Assert.Equal(ClientPatcher.EmptyStringVa, ClientPatcher.ClosedNumberVa + (uint)ClientPatcher.ClosedNumberStub.Length + 3);
    }

    [Fact]
    public void Patching_the_patched_client_again_changes_nothing()
    {
        var once = PatchedImage();
        Assert.Equal(once, ClientPatcher.Patch(once, ServerEndpoints.Public));
    }

    [Theory]
    [InlineData(0x200)]
    [InlineData(0x810)]
    [InlineData(0xA40)]
    [InlineData(0xC05)]
    [InlineData(0x3FFF)]
    public void Whatever_the_section_held_it_is_written_whole_again(int at)
    {
        var once = PatchedImage();
        var image = (byte[])once.Clone();
        image[TestImage.ChaosRaw + at] ^= 0x5A;
        Assert.Equal(once, ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(39)]
    public void Anything_but_zeros_where_its_header_goes_is_refused(int at)
    {
        var image = TestImage.Build();
        image[TestImage.NextSectionHeader + at] = 0x01;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void Headers_with_no_room_for_another_section_are_refused()
    {
        var image = TestImage.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(TestImage.OptionalHeader + 60), (uint)TestImage.NextSectionHeader + 39);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void A_client_whose_sections_end_elsewhere_is_refused()
    {
        // .rsrc a page longer in memory would put .chaos at 0xb99000.
        var image = TestImage.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(TestImage.NextSectionHeader - 40 + 8), TestImage.ResourceVirtualSize + 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(TestImage.OptionalHeader + 56), TestImage.SizeOfImage + 0x1000);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void An_image_size_past_the_last_section_is_refused()
    {
        var image = TestImage.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(TestImage.OptionalHeader + 56), TestImage.SizeOfImage + 0x1000);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    public static TheoryData<int, uint> OtherChaos => new()
    {
        { 8, 0x3000 },                  // VirtualSize
        { 12, 0x799000 },               // VirtualAddress
        { 16, 0x3000 },                 // SizeOfRawData
        { 36, 0xE0000020 },             // characteristics, writable too
    };

    [Theory]
    [MemberData(nameof(OtherChaos))]
    public void A_chaos_section_other_than_the_launchers_is_refused(int field, uint value)
    {
        var image = PatchedImage();
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(TestImage.NextSectionHeader + field), value);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void A_chaos_section_with_bytes_after_it_is_refused()
    {
        var image = PatchedImage();
        Array.Resize(ref image, image.Length + 0x200);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void A_section_called_chaos_somewhere_else_is_refused()
    {
        // .rsrc renamed .chaos.
        var image = TestImage.Build();
        Encoding.ASCII.GetBytes(".chaos").CopyTo(image, TestImage.NextSectionHeader - 40);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void AddSection_refuses_a_name_the_image_has_and_a_bad_name()
    {
        var image = new PeImage(TestImage.Build());
        Assert.Throws<PatchException>(() => image.AddSection(".text", 0x1000, 0x60000020));
        Assert.Throws<PatchException>(() => image.AddSection(".toolong9", 0x1000, 0x60000020));
        Assert.Throws<PatchException>(() => image.AddSection(".none", 0, 0x60000020));
    }

    [Fact]
    public void An_added_section_is_aligned_and_addressed_like_the_others()
    {
        // A file whose length is not a multiple of FileAlignment: the section starts at the next one.
        var data = TestImage.Build();
        Array.Resize(ref data, data.Length + 3);
        var image = new PeImage(data);
        var added = image.AddSection(".extra", 0x1234, 0x40000040);
        Assert.Equal(new PeImage.Section(".extra", 0x1234, 0x798000, 0x1400, (uint)TestImage.Length + 0x200), added);
        Assert.Equal(TestImage.Length + 0x200 + 0x1400, image.Bytes.Length);
        // Data, not code: SizeOfCode stays; SizeOfImage takes the section in, page-aligned.
        Assert.Equal(TestImage.SizeOfCode, image.SizeOfCode);
        Assert.Equal(0x79A000u, image.SizeOfImage);
        image.Write(0xB98000 + 0x1233, [0xAB]);
        Assert.Equal(0xAB, image.Bytes[TestImage.Length + 0x200 + 0x1233]);
        Assert.Equal(added, new PeImage(image.Bytes).SectionNamed(".extra"));
    }

    [RetailClientFact]
    public void The_retail_client_has_room_for_the_section_where_it_goes()
    {
        var retail = File.ReadAllBytes(RetailClient.Path!);
        int pe = BinaryPrimitives.ReadInt32LittleEndian(retail.AsSpan(0x3C));
        int optional = pe + 24;
        int table = optional + BinaryPrimitives.ReadUInt16LittleEndian(retail.AsSpan(pe + 20));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(retail.AsSpan(pe + 6)));
        Assert.Equal(0x1F8, table);
        Assert.All(retail.AsSpan(table + 4 * 40, 40).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0x1000u, U32(retail, optional + 60));      // SizeOfHeaders
        Assert.Equal(0x1000u, U32(retail, optional + 32));      // SectionAlignment
        Assert.Equal(0x1000u, U32(retail, optional + 36));      // FileAlignment
        Assert.Equal(0x798000u, U32(retail, optional + 56));    // SizeOfImage
        Assert.Equal(0x2EC000u, U32(retail, optional + 4));     // SizeOfCode
        Assert.Equal(0x329000, retail.Length);
        var image = new PeImage(retail);
        var rsrc = image.Sections[^1];
        Assert.Equal(".rsrc", rsrc.Name);
        Assert.Equal(0x7972E0u, rsrc.VirtualAddress + rsrc.VirtualSize);
        // No base relocations, no certificate, no bound imports (data directories 5, 4 and 11).
        foreach (int directory in new[] { 4, 5, 11 })
            Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(retail.AsSpan(optional + 96 + directory * 8)));

        var patched = new PeImage(ClientPatcher.Patch(retail, ServerEndpoints.Public));
        Assert.Equal(new PeImage.Section(".chaos", 0x4000, 0x798000, 0x4000, 0x329000), patched.Sections[^1]);
        Assert.Equal(0x60000020u, patched.CharacteristicsOf(".chaos"));
        Assert.Equal(5, patched.Sections.Count);
        Assert.Equal(0x79C000u, patched.SizeOfImage);
        Assert.Equal(0x2F0000u, patched.SizeOfCode);
        Assert.Equal(0x32D000, patched.Bytes.Length);
        Assert.Equal(PeImage.Checksum(patched.Bytes, patched.ChecksumOffset), patched.StoredChecksum);
        Assert.Equal(ClientPatcher.ChaosContents(), patched.Bytes[0x329000..]);
        // .data reaches over the titles' state at 0xb93070, and still ends in the page before .rsrc.
        Assert.Equal(0x484070u, image.SectionNamed(".data").VirtualSize);
        Assert.Equal(0x484100u, patched.SectionNamed(".data").VirtualSize);
    }
}
