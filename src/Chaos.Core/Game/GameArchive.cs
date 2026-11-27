using System.Buffers.Binary;
using System.Text;

namespace Chaos.Game;

public sealed class GameArchive : IDisposable
{
    const int Record = 268;
    const int NameLength = 256;

    const long MaxEntrySize = 64L * 1024 * 1024;
    const long MaxEntries = 100_000;

    static ReadOnlySpan<byte> Tag => "KCEJYUGI"u8;

    readonly FileStream file;
    readonly Dictionary<string, Entry> entries;

    readonly record struct Entry(long Offset, int Size, int Stored);

    GameArchive(FileStream file, Dictionary<string, Entry> entries)
    {
        this.file = file;
        this.entries = entries;
    }

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

    public int Count => entries.Count;

    public bool Contains(string name) => entries.ContainsKey(Key(name));

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
                // Try the next one, like the client.
            }
        }
        return null;
    }

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
                    // Skip lines that aren't valid paths.
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
        if (count > MaxEntries || 12 + count * Record > length)
            throw new InvalidDataException("index runs past the end");
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
            if (size > MaxEntrySize || stored > MaxEntrySize || offset + stored > length)
                throw new InvalidDataException($"entry {name} runs past the end");
            if (stored != size && size > stored * 9)
                throw new InvalidDataException($"bad size for entry {name}");
            // Sorted, one entry per name.
            entries.TryAdd(Key(name), new Entry(offset, (int)size, (int)stored));
        }
        return entries;
    }

    // Names are nibble-swapped up to the NUL.
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
            throw new InvalidDataException("entry is shorter than its size");
        return output;
    }

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
