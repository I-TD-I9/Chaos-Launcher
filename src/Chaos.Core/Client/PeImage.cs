using System.Buffers.Binary;
using System.Text;

namespace Chaos.Client;

/// <summary>A PE32 image, read and written by virtual address.</summary>
public sealed class PeImage
{
    byte[] data;
    Section[] sections;
    readonly int sectionTable;
    readonly int optional;

    /// <summary>A section header.</summary>
    public readonly record struct Section(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize,
                                          uint RawOffset);

    public PeImage(byte[] data)
    {
        this.data = data;
        if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z')
            throw new PatchException("no MZ header");
        int pe = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C));
        // Must be aligned for the checksum.
        if (pe < 0x40 || pe > data.Length - 24 || (pe & 3) != 0 || U32(pe) != 0x00004550)
            throw new PatchException("no PE header");
        if (U16(pe + 4) != 0x14C)
            throw new PatchException("not a 32-bit x86 file");
        int count = U16(pe + 6);
        int optionalSize = U16(pe + 20);
        optional = pe + 24;
        if (optionalSize < 68 || optional + optionalSize > data.Length || U16(optional) != 0x10B)
            throw new PatchException("no PE32 optional header");
        ImageBase = U32(optional + 28);
        ChecksumOffset = optional + 64;
        int table = optional + optionalSize;
        if (count == 0 || table + count * 40 > data.Length)
            throw new PatchException("section table runs past the end");
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

    public int ChecksumOffset { get; }

    public byte[] Bytes => data;

    public uint StoredChecksum => U32(ChecksumOffset);

    public uint SizeOfImage => U32(optional + 56);

    public uint SizeOfCode => U32(optional + 4);

    public IReadOnlyList<Section> Sections => sections;

    uint SectionAlignment => U32(optional + 32);

    uint FileAlignment => U32(optional + 36);

    uint SizeOfHeaders => U32(optional + 60);

    public int Offset(uint va, int count)
    {
        foreach (var s in sections)
        {
            ulong start = (ulong)ImageBase + s.VirtualAddress;
            if (va < start || va >= start + Math.Max(s.VirtualSize, s.RawSize))
                continue;
            ulong into = va - start;
            if (into + (ulong)count > s.RawSize || (ulong)s.RawOffset + s.RawSize > (ulong)data.Length)
                throw new PatchException($"{count} bytes at 0x{va:x} run past {s.Name}");
            return (int)(s.RawOffset + into);
        }
        throw new PatchException($"0x{va:x} is outside every section");
    }

    public ReadOnlySpan<byte> Read(uint va, int count) => data.AsSpan(Offset(va, count), count);

    public void Write(uint va, ReadOnlySpan<byte> bytes) => bytes.CopyTo(data.AsSpan(Offset(va, bytes.Length)));

    public Section GetSection(string name) => sections[IndexOf(name)];

    public bool HasSection(string name) => sections.Any(s => s.Name == name);

    public uint GetCharacteristics(string name) => U32(sectionTable + IndexOf(name) * 40 + 36);

    public void SetVirtualSize(string name, uint size)
    {
        int i = IndexOf(name);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(sectionTable + i * 40 + 8), size);
        sections[i] = sections[i] with { VirtualSize = size };
    }

    public Section AddSection(string name, uint size, uint characteristics)
    {
        if (name.Length is 0 or > 8 || !Ascii.IsValid(name))
            throw new PatchException($"invalid section name: {name}");
        if (HasSection(name))
            throw new PatchException($"section {name} already exists");
        if (size == 0)
            throw new PatchException("empty section");
        uint memory = SectionAlignment, disk = FileAlignment;
        if (!uint.IsPow2(memory) || !uint.IsPow2(disk) || disk > memory)
            throw new PatchException($"bad alignments: 0x{memory:x} in memory, 0x{disk:x} on disk");

        int header = sectionTable + sections.Length * 40;
        if (header + 40 > SizeOfHeaders || header + 40 > data.Length)
            throw new PatchException($"no room for another section header at 0x{header:x}");
        if (data.AsSpan(header, 40).ContainsAnyExcept((byte)0))
            throw new PatchException($"section header slot at 0x{header:x} is not empty");

        // Sections span max(VirtualSize, RawSize).
        ulong end = sections.Max(s => (ulong)s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
        uint va = checked((uint)Align(end, memory));
        if (SizeOfImage > va)
            throw new PatchException($"SizeOfImage 0x{SizeOfImage:x} is past the last section's end at 0x{va:x}");
        uint raw = checked((uint)Align((ulong)data.Length, disk));
        uint rawSize = checked((uint)Align(size, disk));
        uint imageSize = checked((uint)Align((ulong)va + size, memory));

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
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(optional + 56), imageSize);
        // A code section.
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
                throw new PatchException($"more than one {name} section");
            found = i;
        }
        return found >= 0 ? found : throw new PatchException($"no {name} section");
    }

    public void UpdateChecksum() =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(ChecksumOffset), Checksum(data, ChecksumOffset));

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
            // Odd last byte: pad with zero.
            sum += file[i];
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return sum + (uint)file.Length;
    }

    ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));

    uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
}
