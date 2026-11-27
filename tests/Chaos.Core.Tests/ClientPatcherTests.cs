using System.Buffers.Binary;
using System.Text;
using Chaos.Client;

namespace Chaos.Tests;

public class ClientPatcherTests
{
    static readonly ServerEndpoints Local = new("127.0.0.1", 5731, new Uri("http://127.0.0.1:8080/"));

    static string ReadHost(byte[] image) =>
        Encoding.ASCII.GetString(image, TestImage.HostOffset, 32).TrimEnd('\0');

    static int ReadPort(byte[] image) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(TestImage.PortOffset + 4));

    [Fact]
    public void ReplacesHost()
    {
        var patched = ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);
        Assert.Equal("battle-city.net", ReadHost(patched));
        Assert.All(patched.AsSpan(TestImage.HostOffset + 15, 17).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal("version error.", Encoding.ASCII.GetString(patched, TestImage.HostOffset + 32, 14));
    }

    static readonly byte[] Stub =
    [
        0x68, 0xE1, 0xCF, 0x6E, 0x00, 0xFF, 0x15, 0x38, 0xD2, 0x6E, 0x00, 0xFF, 0x25, 0xE0, 0xD3, 0x6E, 0x00,
        .. "ddraw.dll\0"u8,
    ];

    [Fact]
    public void ChangesOnlyPatchedBytes()
    {
        var retail = TestImage.Build();
        var patched = ClientPatcher.Patch(retail, ServerEndpoints.Public);
        Assert.Equal(5730, ReadPort(patched));
        static bool In(int i, int start, int count) => i >= start && i < start + count;
        var listSites = TestImage.ListSites.Select(site => (Start: TestImage.ListOffset(site.Va), Count: site.Bytes.Length)).ToArray();
        var chaosSites = TestImage.ChaosSites.Select(site => (Start: TestImage.SiteOffset(site.Va), Count: site.Bytes.Length))
            .Concat(TestImage.WebLinks.Select(link => (Start: TestImage.SiteOffset(link.Va), Count: link.Url.Length + 1))).ToArray();
        int optional = TestImage.OptionalHeader;
        Assert.Equal(retail.Length + TestImage.ChaosLength, patched.Length);
        for (int i = 0; i < retail.Length; i++)
        {
            bool slot = In(i, TestImage.HostOffset, 32);
            bool root = i == TestImage.RegistryOffset + 1;
            bool jump = i == TestImage.UpdaterOffset;
            bool activation = i == TestImage.ActivationOffset + 2;
            bool call = In(i, TestImage.DirectDrawOffset, 6);
            bool stub = In(i, TestImage.StubOffset, Stub.Length);
            bool size = In(i, TestImage.TextVirtualSizeOffset, 4) || In(i, TestImage.DataVirtualSizeOffset, 4);
            bool lists = listSites.Any(site => In(i, site.Start, site.Count));
            bool chaos = chaosSites.Any(site => In(i, site.Start, site.Count));
            bool cave = In(i, TestImage.CardCaveOffset, TestImage.CardCaveLength);
            bool held = In(i, TestImage.ProcHeldOffset, TestImage.ProcHeldLength);
            bool checksum = In(i, TestImage.ChecksumOffset, 4);
            // Section header and size fields.
            bool header = In(i, TestImage.NextSectionHeader, 40) || In(i, TestImage.PeOffset + 6, 2) ||
                          In(i, optional + 4, 4) || In(i, optional + 56, 4);
            if (!slot && !root && !jump && !activation && !call && !stub && !size && !lists && !chaos && !cave && !held &&
                !checksum && !header)
                Assert.True(retail[i] == patched[i], $"byte 0x{i:x} changed");
        }
        Assert.Equal(TestImage.ReferenceChecksum(patched, TestImage.ChecksumOffset),
                     BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(TestImage.ChecksumOffset)));
    }

    [Fact]
    public void AcceptsRecordlessActivation()
    {
        var patched = ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);
        Assert.Equal([0x0F, 0x84, 0xA1, 0x00, 0x00, 0x00], patched.AsSpan(TestImage.ActivationOffset, 6).ToArray());
    }

    [Fact]
    public void RejectsOtherActivationCode()
    {
        var image = TestImage.Build();
        image[TestImage.ActivationOffset + 2] = 0x91;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void LoadsWrapperThroughStub()
    {
        var patched = ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);
        Assert.Equal([0xE8, 0xC8, 0x1E, 0x21, 0x00, 0x90], patched.AsSpan(TestImage.DirectDrawOffset, 6).ToArray());
        Assert.Equal(Stub, patched.AsSpan(TestImage.StubOffset, Stub.Length).ToArray());
        int target = (int)TestImage.DirectDrawVa + 5 + BinaryPrimitives.ReadInt32LittleEndian(patched.AsSpan(TestImage.DirectDrawOffset + 1));
        Assert.Equal(TestImage.StubVa, (uint)target);
        // The stub pushes its own trailing DLL name.
        Assert.Equal(TestImage.StubVa + 17, BinaryPrimitives.ReadUInt32LittleEndian(Stub.AsSpan(1)));
    }

    [Fact]
    public void WidensText()
    {
        var patched = ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);
        var text = new PeImage(patched).GetSection(".text");
        Assert.Equal(0x1000u, text.VirtualSize);
        Assert.Equal(text.RawSize, text.VirtualSize);
        Assert.True(TestImage.StubVa + Stub.Length <= 0x400000 + text.VirtualAddress + text.VirtualSize);
    }

    [Fact]
    public void KeepsTextSize()
    {
        var image = TestImage.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(TestImage.TextVirtualSizeOffset), 0xFF0);
        var patched = ClientPatcher.Patch(image, ServerEndpoints.Public);
        Assert.Equal(0xFF0u, new PeImage(patched).GetSection(".text").VirtualSize);
    }

    [Fact]
    public void RejectsOtherDirectDrawCode()
    {
        var image = TestImage.Build();
        image[TestImage.DirectDrawOffset + 2] = 0xE4;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(26)]
    public void RejectsNonZeroStubSlot(int at)
    {
        var image = TestImage.Build();
        image[TestImage.StubOffset + at] = 0xCC;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void RejectsMissingText()
    {
        var image = TestImage.Build();
        image[TestImage.TextVirtualSizeOffset - 8 + 1] = (byte)'T';
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void PatchingAgainChangesNothing()
    {
        var once = ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);
        Assert.Equal(once, ClientPatcher.Patch(once, ServerEndpoints.Public));
    }

    [Fact]
    public void ReadsRegistryFromCurrentUser()
    {
        var patched = ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);
        Assert.Equal([0x68, 0x01, 0x00, 0x00, 0x80], patched.AsSpan(TestImage.RegistryOffset, 5).ToArray());
    }

    [Fact]
    public void SkipsUpdaterCopy()
    {
        var patched = ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);
        Assert.Equal([0xEB, 0x3D], patched.AsSpan(TestImage.UpdaterOffset, 2).ToArray());
    }

    [Fact]
    public void RejectsOtherUpdaterCode()
    {
        var image = TestImage.Build();
        image[TestImage.UpdaterOffset] = 0x74;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void RejectsOtherRegistryCode()
    {
        var image = TestImage.Build();
        image[TestImage.RegistryOffset + 1] = 0x03;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void LeavesInputUnchanged()
    {
        var retail = TestImage.Build();
        var copy = (byte[])retail.Clone();
        ClientPatcher.Patch(retail, Local);
        Assert.Equal(copy, retail);
    }

    [Fact]
    public void ReplacesPort()
    {
        var patched = ClientPatcher.Patch(TestImage.Build(), Local);
        Assert.Equal(5731, ReadPort(patched));
        Assert.Equal("127.0.0.1", ReadHost(patched));
    }

    [Fact]
    public void RepatchesPatchedClient()
    {
        var once = ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);
        var twice = ClientPatcher.Patch(once, Local);
        Assert.Equal("127.0.0.1", ReadHost(twice));
        Assert.Equal(ClientPatcher.Patch(TestImage.Build(), Local), twice);
    }

    [Fact]
    public void HostFitsUpTo31Chars()
    {
        var fits = new string('a', 27) + ".net";
        Assert.Equal(fits, ReadHost(ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public with { GateHost = fits })));
        var tooLong = new string('a', 28) + ".net";
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public with { GateHost = tooLong }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad host")]
    [InlineData("-dash.net")]
    [InlineData("a..b")]
    [InlineData("host.net\n")]
    [InlineData("exämple.net")]
    public void RejectsBadHost(string host)
    {
        Assert.False(ClientPatcher.IsHostName(host));
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public with { GateHost = host }));
    }

    [Theory]
    [InlineData("battle-city.net")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("yugi-gate01.yugioh-online.net")]
    public void AcceptsHostsAndAddresses(string host) => Assert.True(ClientPatcher.IsHostName(host));

    [Fact]
    public void RejectsBytesAfterHost()
    {
        var image = TestImage.Build();
        image[TestImage.HostOffset + 30] = (byte)'x';
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void RejectsEmptyHostSlot()
    {
        var image = TestImage.Build();
        Array.Clear(image, TestImage.HostOffset, 32);
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void RejectsOtherHostSlot()
    {
        var image = TestImage.Build(host: "%s:%d bad");
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void RejectsOtherPortCode()
    {
        var image = TestImage.Build();
        image[TestImage.PortOffset + 3] = 0x10;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void RejectsBadPort(int port) =>
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public with { GatePort = port }));

    [RetailClientFact]
    public void PatchesRetailToPublic()
    {
        var retail = File.ReadAllBytes(RetailClient.Path!);
        Assert.Equal(SupportedClient.Sha256, SupportedClient.Sha256Of(retail));
        var patched = ClientPatcher.Patch(retail, ServerEndpoints.Public);
        Assert.Equal(SupportedClient.PublicSha256, SupportedClient.Sha256Of(patched));
        Assert.Equal(3_313_664, retail.Length);
        Assert.Equal(3_330_048, patched.Length);
        // Changed bytes, with .chaos compared to zeros.
        Assert.Equal(2221, patched.Where((b, i) => b != (i < retail.Length ? retail[i] : 0)).Count());
        Assert.Equal(patched, ClientPatcher.Patch(patched, ServerEndpoints.Public));
    }

    [RetailClientFact]
    public void PatchesRetailClient()
    {
        var retail = new PeImage(File.ReadAllBytes(RetailClient.Path!));
        Assert.Equal([0x0F, 0x84, 0x90, 0x00, 0x00, 0x00], retail.Read(ClientPatcher.RecordlessCheckVa, 6).ToArray());
        Assert.Equal([0xFF, 0x15, 0xE0, 0xD3, 0x6E, 0x00], retail.Read(ClientPatcher.DirectDrawCallVa, 6).ToArray());
        Assert.Equal(new byte[Stub.Length], retail.Read(ClientPatcher.WrapperStubVa, Stub.Length).ToArray());
        var text = retail.GetSection(".text");
        Assert.Equal((0x1000u, 0x2EB792u, 0x2EC000u), (text.VirtualAddress, text.VirtualSize, text.RawSize));

        var patched = new PeImage(ClientPatcher.Patch(retail.Bytes, ServerEndpoints.Public));
        Assert.Equal([0x0F, 0x84, 0xA1, 0x00, 0x00, 0x00], patched.Read(ClientPatcher.RecordlessCheckVa, 6).ToArray());
        Assert.Equal([0xE8, 0xC8, 0x1E, 0x21, 0x00, 0x90], patched.Read(ClientPatcher.DirectDrawCallVa, 6).ToArray());
        Assert.Equal(Stub, patched.Read(ClientPatcher.WrapperStubVa, Stub.Length).ToArray());
        Assert.Equal(0x2EC000u, patched.GetSection(".text").VirtualSize);
        Assert.Equal(PeImage.Checksum(patched.Bytes, patched.ChecksumOffset), patched.StoredChecksum);
    }

    [RetailClientFact]
    public void RetailChecksumMatchesPefile()
    {
        var image = new PeImage(File.ReadAllBytes(RetailClient.Path!));
        Assert.Equal(0u, image.StoredChecksum);
        Assert.Equal(0x32AB65u, PeImage.Checksum(image.Bytes, image.ChecksumOffset));
        Assert.Equal(TestImage.ReferenceChecksum(image.Bytes, image.ChecksumOffset),
                     PeImage.Checksum(image.Bytes, image.ChecksumOffset));
    }
}
