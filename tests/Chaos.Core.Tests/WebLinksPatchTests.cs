using System.Buffers.Binary;
using System.Text;
using Chaos.Client;

namespace Chaos.Tests;

public class WebLinksPatchTests
{
    const uint StarPageVa = 0xB98C00;

    static byte[] PatchedImage() => ClientPatcher.Patch(TestImage.Build(), ServerEndpoints.Public);

    // Our site plus the original link's path.
    static string ExpectedReplacement(string original)
    {
        string rest = original[(original.IndexOf("://", StringComparison.Ordinal) + 3)..];
        return "https://chaos-rebirth.com" + rest[rest.IndexOf('/')..];
    }

    [Fact]
    public void LinksKeepPathAndFit()
    {
        Assert.Equal(17, ClientPatcher.WebLinks.Length);
        Assert.Equal(TestImage.WebLinks, ClientPatcher.WebLinks.Select(link => (link.Va, link.Original)));
        foreach (var link in ClientPatcher.WebLinks)
        {
            Assert.Equal(ExpectedReplacement(link.Original), link.Replacement);
            Assert.True(link.Replacement.Length < link.Original.Length, link.Replacement);
            Assert.True(Ascii.IsValid(link.Replacement));
        }
        Assert.Equal(17, ClientPatcher.WebLinks.Select(link => link.Va).Distinct().Count());
    }

    [Fact]
    public void PadsLinksWithZeros()
    {
        var patched = PatchedImage();
        foreach (var link in ClientPatcher.WebLinks)
        {
            int at = TestImage.SiteOffset(link.Va);
            Assert.Equal(link.Replacement, Encoding.ASCII.GetString(patched, at, link.Replacement.Length));
            Assert.All(patched.AsSpan(at + link.Replacement.Length, link.Original.Length + 1 - link.Replacement.Length).ToArray(), b => Assert.Equal(0, b));
        }
        Assert.Equal(-1, patched.AsSpan().IndexOf("yugioh-online"u8));
        Assert.Equal(-1, patched.AsSpan().IndexOf("http://"u8));
        // Message 0x1d keeps its "index.html".
        Assert.Equal("index.html", Encoding.ASCII.GetString(patched, TestImage.SiteOffset(TestImage.IndexPageVa), 10));
    }

    [Fact]
    public void StarOpensOwnPage()
    {
        Assert.Equal(StarPageVa, ClientPatcher.StarPageVa);
        Assert.Equal("event/gunslinger/index.html\0"u8.ToArray(), ClientPatcher.StarPage.ToArray());
        var patched = PatchedImage();
        Assert.Equal(ClientPatcher.StarPage.ToArray(), patched.AsSpan(TestImage.ChaosOffset(StarPageVa), 28).ToArray());
        Assert.Equal([0xBF, 0x00, 0x8C, 0xB9, 0x00], patched.AsSpan(TestImage.SiteOffset(0x66DAAF), 5).ToArray());
        Assert.Equal([0x68, 0x00, 0x8C, 0xB9, 0x00], patched.AsSpan(TestImage.SiteOffset(0x66DACF), 5).ToArray());
        foreach (var site in ClientPatcher.StarSites)
        {
            Assert.Equal(TestImage.IndexPageVa, BinaryPrimitives.ReadUInt32LittleEndian(site.Original.AsSpan(1)));
            Assert.Equal(StarPageVa, BinaryPrimitives.ReadUInt32LittleEndian(site.Patched.AsSpan(1)));
            Assert.Equal(site.Original[0], site.Patched[0]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(16)]
    public void RejectsOtherLink(int index)
    {
        var link = ClientPatcher.WebLinks[index];
        int at = TestImage.SiteOffset(link.Va);
        // A changed character, then a missing NUL.
        var image = TestImage.Build();
        image[at + 12] ^= 0x20;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
        image = TestImage.Build();
        image[at + link.Original.Length] = (byte)'x';
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
        // Ours with bytes after it.
        var patched = PatchedImage();
        patched[at + link.Original.Length] = (byte)'x';
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(patched, ServerEndpoints.Public));
    }

    [Theory]
    [InlineData(0x66DAAF)]
    [InlineData(0x66DACF)]
    public void RejectsOtherStarCode(uint va)
    {
        var image = TestImage.Build();
        image[TestImage.SiteOffset(va) + 1] = 0xA5;
        Assert.Throws<PatchException>(() => ClientPatcher.Patch(image, ServerEndpoints.Public));
    }

    [Fact]
    public void WritesNothingOnRefusal()
    {
        var image = new PeImage(TestImage.Build());
        ClientPatcher.BuildChaosSection(image);
        var last = ClientPatcher.WebLinks[^1];
        image.Write(last.Va, "x"u8);
        var before = (byte[])image.Bytes.Clone();
        Assert.Throws<PatchException>(() => ClientPatcher.PointWebLinks(image));
        Assert.Equal(before, image.Bytes);
    }

    [Fact]
    public void StarNeedsItsPage()
    {
        Assert.Throws<PatchException>(() => ClientPatcher.PointWebLinks(new PeImage(TestImage.Build())));
        var image = new PeImage(TestImage.Build());
        ClientPatcher.BuildChaosSection(image);
        image.Write(StarPageVa + 27, [(byte)'!']);
        Assert.Throws<PatchException>(() => ClientPatcher.PointWebLinks(image));
    }

    [RetailClientFact]
    public void PatchesRetailClient()
    {
        var retail = new PeImage(File.ReadAllBytes(RetailClient.Path!));
        foreach (var link in ClientPatcher.WebLinks)
            Assert.Equal(Encoding.ASCII.GetBytes(link.Original + "\0"), retail.Read(link.Va, link.Original.Length + 1).ToArray());
        foreach (var site in ClientPatcher.StarSites)
            Assert.Equal(site.Original, retail.Read(site.Va, site.Original.Length).ToArray());
        Assert.Equal("index.html\0"u8.ToArray(), retail.Read(TestImage.IndexPageVa, 11).ToArray());
        (uint Va, int Length)[] strings =
        [
            .. ClientPatcher.WebLinks.Select(link => (link.Va, link.Original.Length)),
            (ClientPatcher.GateHostVa, ClientPatcher.RetailGateHost.Length),
        ];
        var places = new List<uint>();
        var bytes = retail.Bytes.AsSpan();
        for (int at = bytes.IndexOf("yugioh-online"u8); at >= 0;)
        {
            uint va = retail.ImageBase + (uint)at;
            Assert.Equal(at, retail.Offset(va, 1));
            places.Add(strings.Single(s => va >= s.Va && va < s.Va + s.Length).Va);
            int next = bytes[(at + 1)..].IndexOf("yugioh-online"u8);
            at = next < 0 ? -1 : at + 1 + next;
        }
        Assert.Equal(strings.Select(s => s.Va).Order(), places.Order());

        var patched = new PeImage(ClientPatcher.Patch(retail.Bytes, ServerEndpoints.Public));
        foreach (var link in ClientPatcher.WebLinks)
        {
            var found = patched.Read(link.Va, link.Original.Length + 1);
            Assert.Equal(link.Replacement, Encoding.ASCII.GetString(found[..link.Replacement.Length]));
            Assert.All(found[link.Replacement.Length..].ToArray(), b => Assert.Equal(0, b));
        }
        foreach (var site in ClientPatcher.StarSites)
            Assert.Equal(site.Patched, patched.Read(site.Va, site.Patched.Length).ToArray());
        Assert.Equal(ClientPatcher.StarPage.ToArray(), patched.Read(StarPageVa, 28).ToArray());
        Assert.Equal("index.html\0"u8.ToArray(), patched.Read(TestImage.IndexPageVa, 11).ToArray());
        Assert.Equal(-1, patched.Bytes.AsSpan().IndexOf("yugioh-online"u8));
        Assert.Equal(-1, patched.Bytes.AsSpan().IndexOf("://www."u8));
    }
}
