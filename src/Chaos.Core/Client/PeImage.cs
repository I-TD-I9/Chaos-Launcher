using System.Buffers.Binary;
using System.Text;

namespace Chaos.Client;

/// <summary>
/// A 32-bit Windows program held as bytes and addressed by virtual address: enough to change its
/// data and code in place, give it a section of its own, and keep the checksum in its header right.
/// </summary>
public sealed class PeImage
{
    byte[] data;
    Section[] sections;
    readonly int sectionTable;
    readonly int optional;

    /// <summary>
    /// A section as its header gives it: where it starts in memory and how far it reaches there,
    /// and where its bytes lie in the file.
    /// </summary>
    public readonly record struct Section(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize,
                                          uint RawOffset);

    /// <summary>
    /// Reads the headers of <paramref name="data"/>, which the image then changes in place until
    /// <see cref="AddSection"/> grows it into a new array.
    /// </summary>
    public PeImage(byte[] data)
    {
        this.data = data;
        if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z')
            throw new PatchException("the file is not a Windows program: it has no MZ header");
        int pe = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C));
        // Aligned, as linkers place it, so that the checksum field covers two whole words.
        if (pe < 0x40 || pe > data.Length - 24 || (pe & 3) != 0 || U32(pe) != 0x00004550)
            throw new PatchException("the file has no PE header");
        if (U16(pe + 4) != 0x14C)
            throw new PatchException("the file is not a 32-bit x86 program");
        int count = U16(pe + 6);
        int optionalSize = U16(pe + 20);
        optional = pe + 24;
        if (optionalSize < 68 || optional + optionalSize > data.Length || U16(optional) != 0x10B)
            throw new PatchException("the file has no PE32 optional header");
        ImageBase = U32(optional + 28);
        ChecksumOffset = optional + 64;
        int table = optional + optionalSize;
        if (count == 0 || table + count * 40 > data.Length)
            throw new PatchException("the file's section table runs past its end");
        sectionTable = table;
        sections = new Section[count];
        for (int i = 0; i < count; i++)
        {
            int s = table + i * 40;
            sections[i] = new Section(Encoding.ASCII.GetString(data, s, 8).TrimEnd('\0'),
                                      VirtualSize: U32(s + 8), VirtualAddress: U32(s + 12),
                                      RawSize: U32(s + 16), RawOffset: U32(s + 20));
        }
    }

    public uint ImageBase { get; }

    /// <summary>The file offset of the optional header's CheckSum field.</summary>
    public int ChecksumOffset { get; }

    /// <summary>The image's bytes, with every change made so far.</summary>
    public byte[] Bytes => data;

    public uint StoredChecksum => U32(ChecksumOffset);

    /// <summary>How far the image reaches in memory from its base: the header's SizeOfImage.</summary>
    public uint SizeOfImage => U32(optional + 56);

    /// <summary>The header's SizeOfCode, the bytes on disk of its sections of code.</summary>
    public uint SizeOfCode => U32(optional + 4);

    /// <summary>The sections in the order of their headers.</summary>
    public IReadOnlyList<Section> Sections => sections;

    uint SectionAlignment => U32(optional + 32);

    uint FileAlignment => U32(optional + 36);

    uint SizeOfHeaders => U32(optional + 60);

    /// <summary>
    /// The file offset of the <paramref name="count"/> bytes at <paramref name="va"/>, which must
    /// all lie in one section's bytes on disk: past them a section exists only in memory, as the
    /// zeros the loader supplies.
    /// </summary>
    public int Offset(uint va, int count)
    {
        foreach (var s in sections)
        {
            ulong start = (ulong)ImageBase + s.VirtualAddress;
            if (va < start || va >= start + Math.Max(s.VirtualSize, s.RawSize))
                continue;
            ulong into = va - start;
            if (into + (ulong)count > s.RawSize || (ulong)s.RawOffset + s.RawSize > (ulong)data.Length)
                throw new PatchException($"the {count} bytes at 0x{va:x} are not all in the file's {s.Name} section");
            return (int)(s.RawOffset + into);
        }
        throw new PatchException($"0x{va:x} is outside every section of the file");
    }

    public ReadOnlySpan<byte> Read(uint va, int count) => data.AsSpan(Offset(va, count), count);

    public void Write(uint va, ReadOnlySpan<byte> bytes) => bytes.CopyTo(data.AsSpan(Offset(va, bytes.Length)));

    /// <summary>The section called <paramref name="name"/>, which the file must have once.</summary>
    public Section SectionNamed(string name) => sections[IndexOf(name)];

    /// <summary>Whether the file has a section called <paramref name="name"/>.</summary>
    public bool HasSection(string name) => sections.Any(s => s.Name == name);

    /// <summary>The Characteristics of the section called <paramref name="name"/>: what it holds and how it may be used.</summary>
    public uint CharacteristicsOf(string name) => U32(sectionTable + IndexOf(name) * 40 + 36);

    /// <summary>
    /// Sets how far the section called <paramref name="name"/> reaches in memory. Code written
    /// past that size, into the padding that fills the section's bytes on disk out to the file's
    /// alignment, lies outside the section until the size covers it.
    /// </summary>
    public void SetVirtualSize(string name, uint size)
    {
        int i = IndexOf(name);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(sectionTable + i * 40 + 8), size);
        sections[i] = sections[i] with { VirtualSize = size };
    }

    /// <summary>
    /// Adds a section of <paramref name="size"/> bytes of zeros called <paramref name="name"/>
    /// after every other, in memory and in the file. Its header goes in the room after the section
    /// table, which must be zeros and inside the headers, and the file grows by its bytes on disk.
    /// SizeOfImage counts it, and SizeOfCode too for a section of code; the checksum is left to
    /// <see cref="UpdateChecksum"/>. <see cref="Bytes"/> is a new array afterwards.
    /// </summary>
    public Section AddSection(string name, uint size, uint characteristics)
    {
        if (name.Length is 0 or > 8 || !Ascii.IsValid(name))
            throw new PatchException($"'{name}' cannot name a section");
        if (HasSection(name))
            throw new PatchException($"the file already has a section called {name}");
        if (size == 0)
            throw new PatchException("a section needs bytes");
        uint memory = SectionAlignment, disk = FileAlignment;
        if (!uint.IsPow2(memory) || !uint.IsPow2(disk) || disk > memory)
            throw new PatchException($"the file's alignments, 0x{memory:x} in memory and 0x{disk:x} on disk, are not a PE's");

        int header = sectionTable + sections.Length * 40;
        if (header + 40 > SizeOfHeaders || header + 40 > data.Length)
            throw new PatchException($"the headers have no room for another section after 0x{header:x}");
        if (data.AsSpan(header, 40).ContainsAnyExcept((byte)0))
            throw new PatchException($"the room for another section header at 0x{header:x} is not zeros");

        // The loader maps a section over the larger of its two sizes.
        ulong end = sections.Max(s => (ulong)s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
        uint va = checked((uint)Align(end, memory));
        if (SizeOfImage > va)
            throw new PatchException($"the file's SizeOfImage, 0x{SizeOfImage:x}, reaches past its last section's end at 0x{va:x}");
        uint raw = checked((uint)Align((ulong)data.Length, disk));
        uint rawSize = checked((uint)Align(size, disk));
        uint image = checked((uint)Align((ulong)va + size, memory));

        var grown = new byte[checked(raw + rawSize)];
        data.CopyTo(grown, 0);
        data = grown;
        Encoding.ASCII.GetBytes(name, data.AsSpan(header, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(header + 8), size);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(header + 12), va);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(header + 16), rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(header + 20), raw);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(header + 36), characteristics);
        int pe = optional - 24;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pe + 6), (ushort)(sections.Length + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(optional + 56), image);
        // IMAGE_SCN_CNT_CODE
        if ((characteristics & 0x20) != 0)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(optional + 4), checked(SizeOfCode + rawSize));

        var added = new Section(name, size, va, rawSize, raw);
        sections = [.. sections, added];
        return added;
    }

    static ulong Align(ulong value, uint alignment) => (value + alignment - 1) & ~(ulong)(alignment - 1);

    int IndexOf(string name)
    {
        int found = -1;
        for (int i = 0; i < sections.Length; i++)
        {
            if (sections[i].Name != name)
                continue;
            if (found >= 0)
                throw new PatchException($"the file has more than one section called {name}");
            found = i;
        }
        return found >= 0 ? found : throw new PatchException($"the file has no section called {name}");
    }

    public void UpdateChecksum() =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(ChecksumOffset), Checksum(data, ChecksumOffset));

    /// <summary>
    /// The checksum Windows' CheckSumMappedFile computes: the file summed as little-endian 16-bit
    /// words with every carry folded back into the low 16 bits, the checksum field itself left
    /// out, and the file's length added at the end.
    /// </summary>
    public static uint Checksum(ReadOnlySpan<byte> file, int checksumOffset)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 1 < file.Length; i += 2)
        {
            if (i == checksumOffset || i == checksumOffset + 2)
                continue;
            sum += BinaryPrimitives.ReadUInt16LittleEndian(file[i..]);
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        if (i < file.Length)
        {
            // An odd last byte counts as a word whose high byte is zero.
            sum += file[i];
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return sum + (uint)file.Length;
    }

    ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));

    uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
}
