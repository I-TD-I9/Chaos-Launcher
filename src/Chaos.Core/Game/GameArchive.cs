using System.Buffers.Binary;
using System.Text;

namespace Chaos.Game;

/// <summary>
/// One of Konami's KCEJYUGI archives, the .dat files the game reads its data from, opened to
/// read an entry out of it.
/// </summary>
/// <remarks>
/// <para>
/// The layout, as the client reads it (sub_56b580 opens one and searches its index, sub_56c4d0
/// loads an entry): <c>char[8] "KCEJYUGI"</c>, <c>u32 count</c>, then <c>count</c> records of 268
/// bytes, each <c>char[256]</c> name with every byte up to its NUL nibble-swapped (sub_56be20),
/// <c>u32 offset</c> of the entry from the start of the file, <c>u32 size</c> and <c>u32 stored</c>
/// size. An entry whose stored size differs from its size is LZSS-packed (sub_573080), and one
/// that then begins with "YPK1" is scrambled behind that tag.
/// </para>
/// <para>
/// The file is opened read-only and shared for reading and writing, so the running game is never
/// kept from its own data, and only the index and the entry asked for are read, never the whole
/// file, which in a February folder is close to 500 MB.
/// </para>
/// </remarks>
public sealed class GameArchive : IDisposable
{
    const int Record = 268;
    const int NameLength = 256;

    // No entry of the game's comes near this, and February's merge.dat holds under 6000 entries:
    // an index that claims more is not to be believed.
    const long LargestEntry = 64L * 1024 * 1024;
    const long MostEntries = 100_000;

    static ReadOnlySpan<byte> Tag => "KCEJYUGI"u8;

    readonly FileStream file;
    readonly Dictionary<string, Entry> entries;

    readonly record struct Entry(long Offset, int Size, int Stored);

    GameArchive(FileStream file, Dictionary<string, Entry> entries)
    {
        this.file = file;
        this.entries = entries;
    }

    /// <summary>
    /// Opens the archive at <paramref name="path"/> and reads its index. Fails with an
    /// InvalidDataException when the file is no KCEJYUGI archive or its index does not fit in it,
    /// and with an IOException or UnauthorizedAccessException when it cannot be read.
    /// </summary>
    public static GameArchive Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return new GameArchive(file, ReadIndex(file));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>How many entries the archive holds.</summary>
    public int Count => entries.Count;

    /// <summary>
    /// Whether the archive holds the entry <paramref name="name"/>: a path inside it such as
    /// <c>data\y\deck_c\kabegami\dialog_base01.bmp</c>, matched without regard to case, with
    /// <c>/</c> taken for <c>\</c>.
    /// </summary>
    public bool Contains(string name) => entries.ContainsKey(Key(name));

    /// <summary>
    /// The entry <paramref name="name"/> (as <see cref="Contains"/> matches it) as the game sees
    /// it, unpacked and unscrambled, or null when the archive has no such entry. Fails with an
    /// InvalidDataException when the entry does not unpack to its size, and with an IOException
    /// when the file cannot be read.
    /// </summary>
    public byte[]? Read(string name)
    {
        if (!entries.TryGetValue(Key(name), out var entry))
            return null;
        var stored = new byte[entry.Stored];
        file.Position = entry.Offset;
        file.ReadExactly(stored);
        var bytes = entry.Stored == entry.Size ? stored : Unpack(stored, entry.Size);
        return Unwrap(bytes);
    }

    /// <summary>
    /// The entry <paramref name="name"/> from the first of <paramref name="archives"/>, in their
    /// order, that holds it, or null when none does. An archive that is missing or will not open
    /// is passed over, as the client passes over one.
    /// </summary>
    public static byte[]? Read(IEnumerable<string> archives, string name)
    {
        foreach (var path in archives)
        {
            if (!File.Exists(path))
                continue;
            try
            {
                using var archive = Open(path);
                if (archive.Read(name) is { } bytes)
                    return bytes;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // The next archive, as the client does.
            }
        }
        return null;
    }

    /// <summary>
    /// The archives the game in <paramref name="folder"/> reads its data from, in the order it
    /// looks in them: each one its patch.txt names (GameFolder.Archives). The client takes a file
    /// from the first of them that has it (sub_578140 goes down the list and stops at the first
    /// hit), so the earlier line wins. The client would also follow a line to an absolute path,
    /// even one on another computer; the launcher only ever needs the archives in the game's own
    /// folder and skips any line that leads out of it. Empty when there is no patch.txt or it
    /// cannot be read.
    /// </summary>
    public static List<string> InFolder(string folder)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            string list = Path.Combine(folder, GameFolder.PatchList);
            if (!File.Exists(list))
                return [];
            var paths = new List<string>();
            foreach (var name in GameFolder.Archives(File.ReadAllBytes(list)))
            {
                try
                {
                    string path = Path.GetFullPath(Path.Combine(root, name + ".dat"));
                    if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        paths.Add(path);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // A line no path can be made of names no archive.
                }
            }
            return paths;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    public void Dispose() => file.Dispose();

    static Dictionary<string, Entry> ReadIndex(FileStream file)
    {
        Span<byte> head = stackalloc byte[12];
        if (file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) != head.Length || !head[..8].SequenceEqual(Tag))
            throw new InvalidDataException("not a KCEJYUGI archive");
        long count = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
        long length = file.Length;
        if (count > MostEntries || 12 + count * Record > length)
            throw new InvalidDataException("the archive's index runs past its end");
        var index = new byte[count * Record];
        file.ReadExactly(index);
        var entries = new Dictionary<string, Entry>((int)count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            var record = index.AsSpan(i * Record, Record);
            string name = Name(record[..NameLength]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(record[NameLength..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(record[(NameLength + 4)..]);
            long stored = BinaryPrimitives.ReadUInt32LittleEndian(record[(NameLength + 8)..]);
            if (size > LargestEntry || stored > LargestEntry || offset + stored > length)
                throw new InvalidDataException($"the archive's entry {name} runs past its end");
            // LZSS makes at most 144 bytes of 17 (a flag byte and eight 2-byte references of 18
            // bytes each), so a packed entry claiming more than 9 times its stored size is broken,
            // and is not worth making room for.
            if (stored != size && size > stored * 9)
                throw new InvalidDataException($"the archive's entry {name} claims more than it can unpack to");
            // The index is sorted and names each entry once; should one come twice, the client's
            // binary search would find either, and the first is as good.
            entries.TryAdd(Key(name), new Entry(offset, (int)size, (int)stored));
        }
        return entries;
    }

    // Every byte of a name up to its NUL has its two halves swapped.
    static string Name(ReadOnlySpan<byte> raw)
    {
        int end = raw.IndexOf((byte)0);
        if (end < 0)
            end = raw.Length;
        var name = new byte[end];
        for (int i = 0; i < end; i++)
            name[i] = (byte)((raw[i] >> 4) | (raw[i] << 4));
        return Encoding.Latin1.GetString(name);
    }

    static string Key(string name) => name.Replace('/', '\\');

    /// <summary>
    /// Okumura's LZSS, as sub_573080 has it: a ring of 4096 bytes, zero to start with, written
    /// from 0xFEE; a flag byte, read from its lowest bit, says of each of the next eight items
    /// whether it is a byte as it is (1) or two bytes (0) giving a place in the ring, the low
    /// eight bits in the first and the high four in the second's top half, and in its low half
    /// how many bytes to copy from there, less three.
    /// </summary>
    internal static byte[] Unpack(ReadOnlySpan<byte> source, int size)
    {
        var output = new byte[size];
        var ring = new byte[4096];
        int r = 0xFEE;
        int written = 0;
        int read = 0;
        int flags = 0;
        while (written < size)
        {
            flags >>= 1;
            if ((flags & 0x100) == 0)
            {
                if (read >= source.Length)
                    break;
                flags = source[read++] | 0xFF00;
            }
            if ((flags & 1) != 0)
            {
                if (read >= source.Length)
                    break;
                byte c = source[read++];
                output[written++] = c;
                ring[r] = c;
                r = (r + 1) & 0xFFF;
            }
            else
            {
                if (read + 1 >= source.Length)
                    break;
                int i = source[read];
                int j = source[read + 1];
                read += 2;
                int at = i | ((j & 0xF0) << 4);
                int count = (j & 0x0F) + 3;
                for (int k = 0; k < count && written < size; k++)
                {
                    byte c = ring[(at + k) & 0xFFF];
                    output[written++] = c;
                    ring[r] = c;
                    r = (r + 1) & 0xFFF;
                }
            }
        }
        if (written != size)
            throw new InvalidDataException("an entry unpacked short of its size");
        return output;
    }

    /// <summary>
    /// An entry behind the tag "YPK1" (sub_56c4d0, 0x56c6cd..0x56c717): every byte after the tag
    /// has its halves swapped and is then XORed with 0xCC, and what the game sees starts after
    /// the tag. Any other entry is as it is.
    /// </summary>
    internal static byte[] Unwrap(byte[] bytes)
    {
        if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual("YPK1"u8))
            return bytes;
        var plain = new byte[bytes.Length - 4];
        for (int k = 0; k < plain.Length; k++)
        {
            int c = bytes[k + 4];
            plain[k] = (byte)((((c & 0x0F) << 4) | (c >> 4)) ^ 0xCC);
        }
        return plain;
    }
}
