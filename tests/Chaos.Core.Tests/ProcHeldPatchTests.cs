using System.Buffers.Binary;
using Chaos.Client;

namespace Chaos.Tests;

public class ProcHeldPatchTests
{
    static byte[] Hex(string text) => Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    internal static readonly byte[] CheckBytes = Hex("""
        e8400000000f8287f9ffffbb000c0000e9d884efffe82b0000000f82cd84efffe98184efff89e8e814f8ffffa808740b
        e8100000000f82ff83efffbb000c0000e9a884efff83ff0b731789f083e00169c0140e00008b800472b8008d4f050fa3
        c8c3
        """);

    const uint ProcA = 0x6ECF60, ProcB = 0x6ECF75, ProcC = 0x6ECF85, Held = 0x6ECFA5;

    // Branches, decoded from the listing.
    static readonly (uint Address, int Length, uint Target)[] Branches =
    [
        (0x6ECF60, 5, Held),
        (0x6ECF65, 6, 0x6EC8F2),
        (0x6ECF70, 5, 0x5E544D),
        (0x6ECF75, 5, Held),
        (0x6ECF7A, 6, 0x5E544D),
        (0x6ECF80, 5, 0x5E5406),
        (0x6ECF87, 5, 0x6EC7A0),
        (0x6ECF8E, 2, 0x6ECF9B),
        (0x6ECF90, 5, Held),
        (0x6ECF95, 6, 0x5E539A),
        (0x6ECFA0, 5, 0x5E544D),
        (0x6ECFA8, 2, 0x6ECFC1),
    ];

    static byte[] PatchedImage() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);

    // A client patched by 0.5.2.
    static byte[] EarlierImage(int cards)
    {
        var image = new PeImage(TestImage.Build());
        ClientPatcher.LoadWrapperFirst(image);
        var cave = new byte[TestImage.CardCaveLength];
        ClientPatcher.CardListStub.CopyTo(cave);
        foreach (var (offset, bytes) in ClientPatcher.EarlierRoutine)
            bytes.CopyTo(cave, offset);
        CardListTable.EncodeKindMap().CopyTo(cave, ClientPatcher.KindMapVa - ClientPatcher.CardStubVa);
        var table = CardListTable.EncodeTable();
        int at = (int)(ClientPatcher.CardTableVa - ClientPatcher.CardStubVa);
        table.AsSpan(0, cards * 4).CopyTo(cave.AsSpan(at));
        table.AsSpan(table.Length - 4).CopyTo(cave.AsSpan(at + cards * 4));
        image.Write(ClientPatcher.CardStubVa, cave);
        foreach (var site in ClientPatcher.CardListSites.Where(site => site.Va != 0x5E5390))
            image.Write(site.Va, site.Patched);
        image.SetVirtualSize(".text", 0x1000u);
        return image.Bytes;
    }

    [Fact]
    public void CheckMatchesListing()
    {
        Assert.Equal(98, CheckBytes.Length);
        Assert.Equal(Convert.ToHexStringLower(CheckBytes), Convert.ToHexStringLower(ClientPatcher.ProcHeldStub));
        Assert.Equal(TestImage.ProcHeldVa, ClientPatcher.ProcHeldVa);
        Assert.Equal(ClientPatcher.CardTableVa + (uint)CardListTable.Capacity, ClientPatcher.ProcHeldVa);
        Assert.True(ClientPatcher.ProcHeldVa + CheckBytes.Length <= ClientPatcher.WrapperStubVa);
        Assert.Equal([0x83, 0xFF, 0x0B], CheckBytes[(int)(Held - ProcA)..(int)(Held - ProcA + 3)]);
        Assert.Equal(0xE14u, BinaryPrimitives.ReadUInt32LittleEndian(CheckBytes.AsSpan(0x51)));
        Assert.Equal(0xB87204u, BinaryPrimitives.ReadUInt32LittleEndian(CheckBytes.AsSpan(0x57)));
        Assert.Equal([0x8D, 0x4F, 0x05, 0x0F, 0xA3, 0xC8, 0xC3], CheckBytes[^7..]);
    }

    [Fact]
    public void BranchesMatchListing()
    {
        foreach (var (address, length, target) in Branches)
        {
            int at = (int)(address - ProcA);
            int relative = length == 2 ? (sbyte)CheckBytes[at + 1] : BinaryPrimitives.ReadInt32LittleEndian(CheckBytes.AsSpan(at + length - 4));
            Assert.Equal(target, (uint)(address + (uint)length + relative));
        }
    }

    [Fact]
    public void PathsJumpToCheck()
    {
        var routine = ClientPatcher.CardListStub.ToArray();
        Assert.Equal(0xE9, routine[0x148]);
        Assert.Equal(ProcA, ClientPatcher.CardStubVa + 0x148 + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(routine.AsSpan(0x149)));
        Assert.All(routine[0x14D..0x152], b => Assert.Equal(0x90, b));
        Assert.Equal([0x0F, 0x85], routine[0x16D..0x16F]);
        Assert.Equal(ProcB, ClientPatcher.CardStubVa + 0x16D + 6 + (uint)BinaryPrimitives.ReadInt32LittleEndian(routine.AsSpan(0x16F)));
        var site = ClientPatcher.CardListSites.Single(site => site.Va == 0x5E5390);
        Assert.Equal([0xBB, 0x00, 0x0C, 0x00, 0x00], site.Original);
        Assert.Equal(ProcC, site.Va + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(site.Patched.AsSpan(1)));
        Assert.Equal(9, ClientPatcher.CardListSites.Length);
    }

    [Fact]
    public void WritesCheckAfterTable()
    {
        var patched = PatchedImage();
        Assert.Equal(CheckBytes, patched.AsSpan(TestImage.ProcHeldOffset, CheckBytes.Length).ToArray());
        Assert.All(patched.AsSpan(TestImage.ProcHeldOffset + CheckBytes.Length, (int)(ClientPatcher.WrapperStubVa - ClientPatcher.ProcHeldVa) - CheckBytes.Length).ToArray(),
                   b => Assert.Equal(0, b));
        Assert.Equal([0xE9, 0xF0, 0x7B, 0x10, 0x00], patched.AsSpan(TestImage.ListOffset(0x5E5390), 5).ToArray());
    }

    [Theory]
    [InlineData(143)]
    [InlineData(141)]
    [InlineData(1)]
    public void UpgradesEarlierPatch(int cards)
    {
        var earlier = EarlierImage(cards);
        Assert.Equal(PatchedImage(), ClientPatcher.Patch(earlier, ServerEndpoints.Public));
    }

    [Fact]
    public void RejectsUnsortedTable()
    {
        var earlier = EarlierImage(143);
        int at = TestImage.CardCaveOffset + (int)(ClientPatcher.CardTableVa - ClientPatcher.CardStubVa);
        BinaryPrimitives.WriteUInt16LittleEndian(earlier.AsSpan(at + 4), 0x0001);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(earlier, ServerEndpoints.Public));
    }

    [Fact]
    public void RejectsBytesAfterTerminator()
    {
        var earlier = EarlierImage(141);
        earlier[TestImage.CardCaveOffset + TestImage.CardCaveLength - 1] = 0xCC;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(earlier, ServerEndpoints.Public));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x30)]
    [InlineData(0x61)]
    public void RejectsOtherCheckBytes(int at)
    {
        var image = TestImage.Build();
        image[TestImage.ProcHeldOffset + at] = 0xCC;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
        var patched = PatchedImage();
        patched[TestImage.ProcHeldOffset + at] ^= 0x01;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(patched, ServerEndpoints.Public));
    }

    [Fact]
    public void RejectsChangedSite()
    {
        var image = TestImage.Build();
        image[TestImage.ListOffset(0x5E5390) + 1] = 0x01;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }
}
