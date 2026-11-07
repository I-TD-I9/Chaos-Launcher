using System.Buffers.Binary;
using Chaos.Client;

namespace Chaos.Tests;

public class CardListPatchTests
{
    // The routine's entry points, from its listing, and the place in the client that jumps to each.
    static readonly (uint Place, uint Entry)[] Jumps =
    [
        (0x59EFE0, 0x6EC7C7),
        (0x59F460, 0x6EC7E4),
        (0x59E5A0, 0x6EC801),
        (0x59F510, 0x6EC81E),
        (0x5DB110, 0x6EC85A),
        (0x5BC863, 0x6EC88F),
        (0x5E5355, 0x6EC8AD),
        (0x5E53FF, 0x6EC8F9),
    ];

    const uint StubVa = 0x6EC7A0;
    const uint KindMapVa = 0x6EC920;
    const uint TableVa = 0x6EC960;

    static byte[] Hex(string text) => Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    static byte[] PatchedImage() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);

    static ReadOnlySpan<byte> CaveOf(byte[] image) => image.AsSpan(TestImage.CardCaveOffset, TestImage.CardCaveLength);

    [Fact]
    public void The_table_encodes_to_the_bytes_the_client_reads()
    {
        Assert.Equal(Convert.ToHexStringLower(TableBytes), Convert.ToHexStringLower(CardListTable.EncodeTable()));
        Assert.Equal(568, TableBytes.Length);
        Assert.Equal(141, CardListTable.Entries.Count);
    }

    [Fact]
    public void The_kind_map_gives_each_window_kind_its_bit()
    {
        Assert.Equal(Convert.ToHexStringLower(KindMapBytes), Convert.ToHexStringLower(CardListTable.EncodeKindMap()));
        Assert.Equal(64, KindMapBytes.Length);
    }

    [Fact]
    public void The_routine_is_the_listed_one()
    {
        Assert.Equal(376, StubBytes.Length);
        Assert.Equal(Convert.ToHexStringLower(StubBytes), Convert.ToHexStringLower(ClientPatcher.CardListStub));
        // The addresses it reads the table and the kind map at are its own.
        Assert.Equal(TableVa, ClientPatcher.CardTableVa);
        Assert.Equal(KindMapVa, ClientPatcher.KindMapVa);
        Assert.Equal(StubVa, ClientPatcher.CardStubVa);
        Assert.Equal(TableVa, BinaryPrimitives.ReadUInt32LittleEndian(StubBytes.AsSpan(0x0D)));
        Assert.Equal(KindMapVa, BinaryPrimitives.ReadUInt32LittleEndian(StubBytes.AsSpan(0x97)));
        Assert.Equal(KindMapVa, BinaryPrimitives.ReadUInt32LittleEndian(StubBytes.AsSpan(0xC6)));
    }

    [Fact]
    public void The_table_is_in_order_without_repeats_and_fits_before_the_display_stub()
    {
        var entries = CardListTable.Entries;
        for (int i = 1; i < entries.Count; i++)
            Assert.True(entries[i - 1].Number < entries[i].Number, $"0x{entries[i].Number:x} follows 0x{entries[i - 1].Number:x}");
        Assert.DoesNotContain(entries, entry => entry.Number == 0xFFFF);

        // Each list is one bit of its own, and a card is on at least one.
        var members = Enum.GetValues<CardList>();
        Assert.Equal(16, members.Length);
        Assert.All(members, member => Assert.Equal(1, int.PopCount((ushort)member)));
        ushort defined = members.Aggregate((ushort)0, (all, member) => (ushort)(all | (ushort)member));
        foreach (var (number, lists) in entries)
        {
            Assert.True(lists != 0, $"0x{number:x} is on no list");
            Assert.Equal(0, (ushort)lists & ~defined);
        }

        var table = CardListTable.EncodeTable();
        Assert.Equal([0xFF, 0xFF, 0x00, 0x00], table[^4..]);
        Assert.True(TableVa + table.Length <= TestImage.StubVa);
        Assert.True(table.Length <= CardListTable.Capacity);
        Assert.Equal(TestImage.CardCaveVa + (uint)TestImage.CardCaveLength, TableVa + (uint)table.Length);
    }

    [Fact]
    public void Each_window_kind_has_the_flag_at_its_bit()
    {
        Assert.Equal(11, CardListTable.WindowBit.Count);
        Assert.Equal(CardListTable.WindowBit.Count, CardListTable.WindowBit.Values.Distinct().Count());
        foreach (var (kind, bit) in CardListTable.WindowBit)
        {
            Assert.InRange(kind, 0, 63);
            Assert.InRange(bit, 5, 15);
            Assert.Equal((CardList)(1 << bit), Enum.Parse<CardList>($"Kind{kind}"));
        }
        Assert.Equal([1, 2, 4, 8, 16], new[] { CardList.Act, CardList.Hand, CardList.Flip, CardList.Proc, CardList.DamageStepAnyKind }.Select(list => (int)list));
    }

    [Fact]
    public void Every_jump_and_call_in_the_routine_lands_where_the_listing_says()
    {
        foreach (var (address, length, target) in Branches)
        {
            int at = (int)(address - StubVa);
            int relative = length == 2 ? (sbyte)StubBytes[at + 1] : BinaryPrimitives.ReadInt32LittleEndian(StubBytes.AsSpan(at + length - 4));
            Assert.Equal(target, (uint)(address + (uint)length + relative));
        }
    }

    [Fact]
    public void Each_place_jumps_to_its_entry_in_the_routine()
    {
        Assert.Equal(Jumps.Length, ClientPatcher.CardListSites.Length);
        foreach (var (place, entry) in Jumps)
        {
            var site = ClientPatcher.CardListSites.Single(site => site.Va == place);
            Assert.Equal(0xE9, site.Patched[0]);
            Assert.Equal(entry, place + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(site.Patched.AsSpan(1)));
            Assert.Equal(site.Original.Length, site.Patched.Length);
            Assert.All(site.Patched[5..], b => Assert.Equal(0x90, b));
            Assert.Equal(TestImage.ListSites.Single(s => s.Va == place).Bytes, site.Original);
            // The jump takes the whole of the instructions it replaces, so none is cut in two.
            Assert.True(site.Original.Length >= 5);
        }
    }

    [Fact]
    public void The_routine_and_its_tables_are_written_where_they_belong_and_the_places_jump_to_them()
    {
        var patched = PatchedImage();
        var cave = CaveOf(patched);
        Assert.Equal(StubBytes, cave[..StubBytes.Length].ToArray());
        Assert.All(cave[StubBytes.Length..(int)(KindMapVa - StubVa)].ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(KindMapBytes, cave[(int)(KindMapVa - StubVa)..(int)(TableVa - StubVa)].ToArray());
        Assert.Equal(TableBytes, cave[(int)(TableVa - StubVa)..].ToArray());
        foreach (var (place, _) in Jumps)
        {
            var site = ClientPatcher.CardListSites.Single(site => site.Va == place);
            int at = TestImage.ListOffset(place);
            Assert.Equal(site.Patched, patched.AsSpan(at, site.Patched.Length).ToArray());
        }
    }

    [Fact]
    public void A_place_with_other_bytes_is_refused()
    {
        foreach (var (place, bytes) in TestImage.ListSites)
            for (int i = 0; i < bytes.Length; i++)
            {
                var image = TestImage.Build();
                image[TestImage.ListOffset(place) + i] ^= 0x01;
                Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
            }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x177)]
    [InlineData(0x178)]
    [InlineData(0x180)]
    [InlineData(0x1C0)]
    [InlineData(0x3F7)]
    public void Anything_but_zeros_where_the_routine_and_tables_go_is_refused(int at)
    {
        var image = TestImage.Build();
        image[TestImage.CardCaveOffset + at] = 0xCC;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void A_routine_without_its_tables_is_refused()
    {
        var image = TestImage.Build();
        StubBytes.CopyTo(image, TestImage.CardCaveOffset);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void Patching_again_changes_nothing()
    {
        var once = PatchedImage();
        var image = new PeImage((byte[])once.Clone());
        ClientPatcher.ExtendCardLists(image);
        Assert.Equal(once, image.Bytes);
        Assert.Equal(once, ClientPatcher.Patch(once, ServerEndpoints.Public));
    }

    [Fact]
    public void A_place_the_client_has_again_is_jumped_again()
    {
        var once = PatchedImage();
        var site = ClientPatcher.CardListSites[4];
        var image = (byte[])once.Clone();
        site.Original.CopyTo(image, TestImage.ListOffset(site.Va));
        Assert.NotEqual(once, image);
        Assert.Equal(once, ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void Text_is_widened_to_cover_the_end_of_the_table()
    {
        var image = new PeImage(TestImage.Build());
        ClientPatcher.ExtendCardLists(image);
        Assert.Equal(0x1000u, image.SectionNamed(".text").VirtualSize);
    }

    [Theory]
    [InlineData(0xB98u, 0xB98u)]
    [InlineData(0xB97u, 0x1000u)]
    [InlineData(0xFF0u, 0xFF0u)]
    public void Text_keeps_a_size_that_covers_the_table_and_gets_one_that_does_not(uint before, uint after)
    {
        var data = TestImage.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TestImage.TextVirtualSizeOffset), before);
        var image = new PeImage(data);
        ClientPatcher.ExtendCardLists(image);
        Assert.Equal(after, image.SectionNamed(".text").VirtualSize);
    }

    [Fact]
    public void The_order_of_the_steps_makes_no_difference()
    {
        var listsFirst = new PeImage(TestImage.Build());
        ClientPatcher.ExtendCardLists(listsFirst);
        ClientPatcher.LoadWrapperFirst(listsFirst);
        var wrapperFirst = new PeImage(TestImage.Build());
        ClientPatcher.LoadWrapperFirst(wrapperFirst);
        ClientPatcher.ExtendCardLists(wrapperFirst);
        Assert.Equal(wrapperFirst.Bytes, listsFirst.Bytes);
    }

    [RetailClientFact]
    public void The_retail_client_has_the_places_and_the_zeros_the_patch_expects()
    {
        var retail = new PeImage(File.ReadAllBytes(RetailClient.Path!));
        foreach (var (place, bytes) in TestImage.ListSites)
            Assert.Equal(bytes, retail.Read(place, bytes.Length).ToArray());
        Assert.All(retail.Read(StubVa, TestImage.CardCaveLength).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(TestImage.CardCaveLength, 0x6ECB98 - 0x6EC7A0);
        Assert.True(0x6ECB98 <= ClientPatcher.WrapperStubVa);
    }

    [RetailClientFact]
    public void The_retail_client_gets_the_routine_the_tables_and_the_jumps()
    {
        var retail = File.ReadAllBytes(RetailClient.Path!);
        var patched = new PeImage(ClientPatcher.Patch(retail, ServerEndpoints.Public));
        Assert.Equal(StubBytes, patched.Read(StubVa, StubBytes.Length).ToArray());
        Assert.Equal(KindMapBytes, patched.Read(KindMapVa, 64).ToArray());
        Assert.Equal(TableBytes, patched.Read(TableVa, TableBytes.Length).ToArray());
        foreach (var (place, _) in Jumps)
        {
            var site = ClientPatcher.CardListSites.Single(site => site.Va == place);
            Assert.Equal(site.Patched, patched.Read(place, site.Patched.Length).ToArray());
        }

        // Nothing else changed in the client's code but the places the earlier patches change.
        var original = new PeImage(retail);
        var allowed = new List<(int Start, int Count)>
        {
            (original.Offset(ClientPatcher.GateHostVa, ClientPatcher.GateHostSlot), ClientPatcher.GateHostSlot),
            (original.Offset(ClientPatcher.GatePortVa + 4, 2), 2),
            (original.Offset(ClientPatcher.RegistryRootVa, 5), 5),
            (original.Offset(ClientPatcher.UpdaterCopyVa, 2), 2),
            (original.Offset(ClientPatcher.RecordlessCheckVa, 6), 6),
            (original.Offset(ClientPatcher.DirectDrawCallVa, 6), 6),
            (original.Offset(ClientPatcher.WrapperStubVa, 27), 27),
            (original.Offset(StubVa, TestImage.CardCaveLength), TestImage.CardCaveLength),
            (original.ChecksumOffset, 4),
            // .text's size in its section header.
            (retail.AsSpan(0, 0x400).IndexOf(".text"u8) + 8, 4),
        };
        foreach (var (place, bytes) in TestImage.ListSites)
            allowed.Add((original.Offset(place, bytes.Length), bytes.Length));
        for (int i = 0; i < retail.Length; i++)
            if (retail[i] != patched.Bytes[i])
                Assert.True(allowed.Any(a => i >= a.Start && i < a.Start + a.Count), $"byte 0x{i:x} changed");
    }

    // The 376 bytes of the routine as listed, from 0x6ec7a0.
    static readonly byte[] StubBytes = Hex("""
        50e8aa59d1ff83c4040fb7d0b960c96e000fb7013bc2730583c104ebf475050fb74102c333c0c38b442404e8d0ffffff
        a8017406b801000000c3568b742408e90128ebff8b442404e8b3ffffffa8027406b801000000c38b44240450e9642ceb
        ff8b442404e896ffffffa8047406b801000000c38b44240450e9871debff8b442404e879ffffffa810751f8b4c240883
        f93f771c0fb68920c96e0083f90b721083f90f770b0fa3c87306b801000000c3568b742408e9bb2cebff8b4c240c83f9
        3f77220fb68920c96e0083f90f77168b44240851e827ffffff590fa3c87306b801000000c38b4c240c56e986e8eeff0f
        b707e809ffffffa8200f850100edff668b1752e8a858d1ffe9bfffecff8b7c241c5756e818e8efff83c40885c075358b
        c5e8dafeffffa80874280fb7c525ff0f00006aff50e8668debff83c40885c075110fb7c589442418bb000c0000e95b8b
        efff33c0e96a8aefff3df00500000f84028befff8bc5e895feffffa8080f85f38aefffe9358befff
        """);

    // The kind map: a byte for each of the 64 kinds, from 0x6ec920.
    static readonly byte[] KindMapBytes = Hex("""
        ffffffffff05060708ff09ffff0b0cffffffff0dff0effffffffffff0affffffffffffffffffffffffffffffffffffff
        ffffffff0fffffffffffffffffffffff
        """);

    // The 141 cards and the terminator: 568 bytes, from 0x6ec960.
    static readonly byte[] TableBytes = Hex("""
        4b0701008a070400040808001008010057080100590801006008010063086000730801007a0801007b0801007d080100
        89080100940801009508010097080100a6080100a7080100ab080100ad080100af080100b0080100b2080100ba080100
        bd080100c1081000dc080100dd0808000e090100190900041f0901002009010027090100350901003c09010043090100
        4709010048090800490908004a0908004b0908004c0909004e09090067090900750901007d0901008209010083090100
        9f090100b0090100b2090100b9090100bd090100c4090100c5090100c9090900dd090800e1090200f3090100f4090100
        ff0901001e0a0100210a0900250a0900260a0800270a0800320a0100380a0100390a01003b0a01003f0a0100640a0800
        760a0100770a01008a0a01008f0a0100900a01009c0a0100a00a0020a90a0100b00a0100b60a0100bd0a0100c10a0100
        c20a0100c30a0100c40a0100c50a0100c90a0100cb0a0100cc0a0100e60a0800e70a0900e80a0100f60a0100f70a0800
        fc0a0800fd0a0100fe0a0100000b0100090b01000c0b0100150b0100260b0900280b01002e0b0800300b0100310b0800
        380b01003a0b01003d0b0100420b0100470b0100520b0100620b80037c0b01007d0b0100800b0100860b10048a0b0100
        930b0800940b0800960b0800a90b0800b10b0900b30b8000b50b0040b70b0900b80b0800b90b0900ba0b0800bd0b0100
        be0b0800ca0b0010cb0b0100d80b0900da0b0800e50b0100e80b0100f00b0040f30b0100ffff0000
        """);

    // Each call and jump in the routine, decoded from the listing: where the instruction is, how long it is
    // and where it goes.
    static readonly (uint Address, int Length, uint Target)[] Branches =
    [
        (0x6EC7A1, 5, 0x402150),
        (0x6EC7B6, 2, 0x6EC7BD),
        (0x6EC7BB, 2, 0x6EC7B1),
        (0x6EC7BD, 2, 0x6EC7C4),
        (0x6EC7CB, 5, 0x6EC7A0),
        (0x6EC7D2, 2, 0x6EC7DA),
        (0x6EC7DF, 5, 0x59EFE5),
        (0x6EC7E8, 5, 0x6EC7A0),
        (0x6EC7EF, 2, 0x6EC7F7),
        (0x6EC7FC, 5, 0x59F465),
        (0x6EC805, 5, 0x6EC7A0),
        (0x6EC80C, 2, 0x6EC814),
        (0x6EC819, 5, 0x59E5A5),
        (0x6EC822, 5, 0x6EC7A0),
        (0x6EC829, 2, 0x6EC84A),
        (0x6EC832, 2, 0x6EC850),
        (0x6EC83E, 2, 0x6EC850),
        (0x6EC843, 2, 0x6EC850),
        (0x6EC848, 2, 0x6EC850),
        (0x6EC855, 5, 0x59F515),
        (0x6EC861, 2, 0x6EC885),
        (0x6EC86D, 2, 0x6EC885),
        (0x6EC874, 5, 0x6EC7A0),
        (0x6EC87D, 2, 0x6EC885),
        (0x6EC88A, 5, 0x5DB115),
        (0x6EC892, 5, 0x6EC7A0),
        (0x6EC899, 6, 0x5BC8A0),
        (0x6EC8A3, 5, 0x402150),
        (0x6EC8A8, 5, 0x5BC86C),
        (0x6EC8B3, 5, 0x5EB0D0),
        (0x6EC8BD, 2, 0x6EC8F4),
        (0x6EC8C1, 5, 0x6EC7A0),
        (0x6EC8C8, 2, 0x6EC8F2),
        (0x6EC8D5, 5, 0x5A5640),
        (0x6EC8DF, 2, 0x6EC8F2),
        (0x6EC8ED, 5, 0x5E544D),
        (0x6EC8F4, 5, 0x5E5363),
        (0x6EC8FE, 6, 0x5E5406),
        (0x6EC906, 5, 0x6EC7A0),
        (0x6EC90D, 6, 0x5E5406),
        (0x6EC913, 5, 0x5E544D),
    ];
}
