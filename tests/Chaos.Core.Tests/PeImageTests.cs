using Chaos.Client;

namespace Chaos.Tests;

public class PeImageTests
{
    [Fact]
    public void MapsAddresses()
    {
        var image = new PeImage(TestImage.Build());
        Assert.Equal(0x400000u, image.ImageBase);
        Assert.Equal(TestImage.HostOffset, image.Offset(TestImage.HostVa, 32));
        Assert.Equal(TestImage.PortOffset, image.Offset(TestImage.PortVa, 6));
        Assert.Equal(TestImage.ChecksumOffset, image.ChecksumOffset);
    }

    [Fact]
    public void MapsPadding()
    {
        var image = new PeImage(TestImage.Build());
        Assert.Equal(TestImage.StubOffset, image.Offset(TestImage.StubVa, 27));
        // Last byte on disk, but not past it.
        Assert.Equal(TestImage.TextRaw + 0xFFF, image.Offset(0x6ECFFF, 1));
        Assert.Throws<PatchException>(() => image.Offset(0x6ECFFF, 2));
    }

    [Fact]
    public void FindsSectionByName()
    {
        var image = new PeImage(TestImage.Build());
        Assert.Equal(new PeImage.Section(".data", TestImage.DataVirtualSize, TestImage.DataRva, 0x1000, TestImage.DataRaw),
                     image.GetSection(".data"));
        Assert.Equal(new PeImage.Section(".text", TestImage.TextVirtualSize, TestImage.TextRva, 0x1000, TestImage.TextRaw),
                     image.GetSection(".text"));
        Assert.Throws<PatchException>(() => image.GetSection(".reloc"));
        Assert.Throws<PatchException>(() => image.GetSection(".TEXT"));
    }

    [Fact]
    public void RejectsDuplicateName()
    {
        var data = TestImage.Build();
        // .text5 becomes a second .text.
        data[TestImage.TextVirtualSizeOffset - 40 - 8 + 5] = 0;
        Assert.Throws<PatchException>(() => new PeImage(data).GetSection(".text"));
    }

    [Fact]
    public void SetVirtualSizeWritesHeader()
    {
        var data = TestImage.Build();
        var image = new PeImage(data);
        image.SetVirtualSize(".text", 0x1000);
        Assert.Equal(0x1000u, image.GetSection(".text").VirtualSize);
        Assert.Equal([0x00, 0x10, 0x00, 0x00], data.AsSpan(TestImage.TextVirtualSizeOffset, 4).ToArray());
        Assert.Equal(0x1000u, new PeImage(data).GetSection(".text").VirtualSize);
        // Nothing else in the header moves.
        var before = TestImage.Build();
        for (int i = 0; i < 0x400; i++)
            if (i < TestImage.TextVirtualSizeOffset || i >= TestImage.TextVirtualSizeOffset + 4)
                Assert.True(before[i] == data[i], $"byte 0x{i:x} changed");
        Assert.Equal(TestImage.StubOffset, image.Offset(TestImage.StubVa, 27));
    }

    [Fact]
    public void RejectsAddressOutsideSections()
    {
        var image = new PeImage(TestImage.Build());
        Assert.Throws<PatchException>(() => image.Offset(0x400000, 4));
        Assert.Throws<PatchException>(() => image.Offset(0x900000, 4));
    }

    [Fact]
    public void RejectsMemoryOnlyBytes()
    {
        var image = new PeImage(TestImage.Build());
        uint pastDisk = 0x400000 + TestImage.DataRva + 0x1000;
        Assert.Throws<PatchException>(() => image.Offset(pastDisk, 4));
        // Straddles the end of the raw data.
        Assert.Throws<PatchException>(() => image.Offset(pastDisk - 2, 4));
    }

    [Theory]
    [InlineData(0, (byte)'X')]
    [InlineData(TestImage.PeOffset, (byte)'X')]
    public void RejectsNonPe(int at, byte value)
    {
        var data = TestImage.Build();
        data[at] = value;
        Assert.Throws<PatchException>(() => new PeImage(data));
    }

    [Fact]
    public void Rejects64Bit()
    {
        var data = TestImage.Build();
        data[TestImage.PeOffset + 4] = 0x64;
        data[TestImage.PeOffset + 5] = 0x86;
        Assert.Throws<PatchException>(() => new PeImage(data));
    }

    [Fact]
    public void RejectsTruncatedFile()
    {
        Assert.Throws<PatchException>(() => new PeImage(TestImage.Build()[..0x100]));
        Assert.Throws<PatchException>(() => new PeImage(new byte[16]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ChecksumMatchesReference(int extra)
    {
        var data = TestImage.Build();
        Array.Resize(ref data, data.Length + extra);
        for (int i = 0; i < extra; i++)
            data[^(i + 1)] = (byte)(0xF0 + i);
        new Random(7).NextBytes(data.AsSpan(TestImage.DataRaw + 0x100, 0x400));
        Assert.Equal(TestImage.ReferenceChecksum(data, TestImage.ChecksumOffset),
                     PeImage.Checksum(data, TestImage.ChecksumOffset));
    }

    [Fact]
    public void ChecksumSkipsItsField()
    {
        var data = TestImage.Build();
        uint before = PeImage.Checksum(data, TestImage.ChecksumOffset);
        data[TestImage.ChecksumOffset] = 0x5A;
        data[TestImage.ChecksumOffset + 3] = 0xA5;
        Assert.Equal(before, PeImage.Checksum(data, TestImage.ChecksumOffset));
    }

    [Fact]
    public void UpdateChecksumWritesHeader()
    {
        var image = new PeImage(TestImage.Build());
        image.UpdateChecksum();
        Assert.NotEqual(0u, image.StoredChecksum);
        Assert.Equal(TestImage.ReferenceChecksum(image.Bytes, TestImage.ChecksumOffset), image.StoredChecksum);
    }
}
