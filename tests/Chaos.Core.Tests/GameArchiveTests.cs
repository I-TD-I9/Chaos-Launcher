using System.Buffers.Binary;
using System.Text;
using Chaos.Game;
using Xunit.Abstractions;

namespace Chaos.Tests;

public sealed class GameArchiveTests(ITestOutputHelper output) : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("bc-archive-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    /// <summary>An entry as it is stored: its name, its bytes in the file, and the size they unpack to.</summary>
    internal sealed record Stored(string Name, byte[] Bytes, int Size)
    {
        public static Stored Raw(string name, byte[] bytes) => new(name, bytes, bytes.Length);
    }

    /// <summary>Writes a KCEJYUGI archive of <paramref name="entries"/>, their names scrambled as Konami's are.</summary>
    internal static void Write(string path, params Stored[] entries)
    {
        const int record = 268;
        long offset = 12 + entries.Length * record;
        using var file = File.Create(path);
        file.Write("KCEJYUGI"u8);
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)entries.Length);
        file.Write(u32);
        foreach (var entry in entries)
        {
            var row = new byte[record];
            var name = Encoding.Latin1.GetBytes(entry.Name);
            for (int i = 0; i < name.Length; i++)
                row[i] = (byte)((name[i] >> 4) | (name[i] << 4));
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(256), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(260), (uint)entry.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(264), (uint)entry.Bytes.Length);
            file.Write(row);
            offset += entry.Bytes.Length;
        }
        foreach (var entry in entries)
            file.Write(entry.Bytes);
    }

    /// <summary>The bytes the game sees as <paramref name="plain"/>, behind the tag YPK1.</summary>
    internal static byte[] Scrambled(byte[] plain) =>
        [.. "YPK1"u8, .. plain.Select(p => (byte)(((p ^ 0xCC) >> 4) | ((p ^ 0xCC) << 4)))];

    // "ABC", then a copy of six bytes from where the A went, which runs into the bytes it writes:
    // flags 0b0111, three bytes as they are and a pair, ring place 0xFEE and a length of 6 - 3.
    static readonly byte[] Packed = [0x07, (byte)'A', (byte)'B', (byte)'C', 0xEE, 0xF3];

    string Path(string name) => System.IO.Path.Combine(folder, name);

    [Fact]
    public void An_entry_stored_as_it_is_reads_back()
    {
        Write(Path("a.dat"), Stored.Raw(@"data\x\one.txt", "one"u8.ToArray()), Stored.Raw(@"data\x\two.txt", "two"u8.ToArray()));
        using var archive = GameArchive.Open(Path("a.dat"));
        Assert.Equal(2, archive.Count);
        Assert.Equal("two"u8.ToArray(), archive.Read(@"data\x\two.txt"));
    }

    [Fact]
    public void Names_match_without_regard_to_case_or_slashes()
    {
        Write(Path("a.dat"), Stored.Raw(@"data\Y\Deck.BMP", [1, 2, 3]));
        using var archive = GameArchive.Open(Path("a.dat"));
        Assert.True(archive.Contains("DATA/y/deck.bmp"));
        Assert.Equal([1, 2, 3], archive.Read("data/y/deck.bmp"));
        Assert.Null(archive.Read("data/y/other.bmp"));
    }

    [Fact]
    public void A_packed_entry_unpacks_through_the_ring()
    {
        Write(Path("a.dat"), new Stored("packed", Packed, 9));
        using var archive = GameArchive.Open(Path("a.dat"));
        Assert.Equal("ABCABCABC"u8.ToArray(), archive.Read("packed"));
    }

    [Fact]
    public void The_ring_starts_out_as_zeros() =>
        // A copy of three bytes from ring place 0, then a byte as it is.
        Assert.Equal([0, 0, 0, (byte)'Z'], GameArchive.Unpack([0b10, 0x00, 0x00, (byte)'Z'], 4));

    [Fact]
    public void A_packed_entry_cut_short_is_refused() =>
        Assert.Throws<InvalidDataException>(() => GameArchive.Unpack(Packed, 12));

    [Fact]
    public void A_scrambled_entry_is_unscrambled()
    {
        var plain = "BM hieroglyphs"u8.ToArray();
        Write(Path("a.dat"), Stored.Raw("scrambled", Scrambled(plain)));
        using var archive = GameArchive.Open(Path("a.dat"));
        Assert.Equal(plain, archive.Read("scrambled"));
    }

    [Fact]
    public void A_file_without_the_tag_is_no_archive()
    {
        File.WriteAllBytes(Path("a.dat"), "NOTKONAMI\0\0\0"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => GameArchive.Open(Path("a.dat")).Dispose());
    }

    [Fact]
    public void An_index_that_runs_past_the_end_is_refused()
    {
        Write(Path("a.dat"), Stored.Raw("one", [1, 2, 3, 4]));
        var bytes = File.ReadAllBytes(Path("a.dat"));
        File.WriteAllBytes(Path("a.dat"), bytes[..^2]);
        Assert.Throws<InvalidDataException>(() => GameArchive.Open(Path("a.dat")).Dispose());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 50);
        File.WriteAllBytes(Path("b.dat"), bytes);
        Assert.Throws<InvalidDataException>(() => GameArchive.Open(Path("b.dat")).Dispose());
    }

    [Fact]
    public void An_archive_the_game_has_open_can_still_be_read()
    {
        Write(Path("a.dat"), Stored.Raw("one", [7]));
        using var held = new FileStream(Path("a.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var archive = GameArchive.Open(Path("a.dat"));
        Assert.Equal([7], archive.Read("one"));
    }

    [Fact]
    public void The_first_archive_patch_txt_names_that_has_an_entry_wins()
    {
        File.WriteAllText(Path("patch.txt"), "# comment\r\nnewer.dat\r\nolder.dat\r\nmissing.dat\r\n");
        File.WriteAllBytes(Path("broken.dat"), [1, 2, 3]);
        Write(Path("newer.dat"), Stored.Raw("both", "newer"u8.ToArray()));
        Write(Path("older.dat"), Stored.Raw("both", "older"u8.ToArray()), Stored.Raw("only-older", "old"u8.ToArray()));
        var archives = GameArchive.InFolder(folder);
        Assert.Equal([Path("newer.dat"), Path("older.dat"), Path("missing.dat")], archives);
        Assert.Equal("newer"u8.ToArray(), GameArchive.Read(archives, "both"));
        Assert.Equal("old"u8.ToArray(), GameArchive.Read(archives, "only-older"));
        Assert.Null(GameArchive.Read(archives, "nowhere"));
        Assert.Equal("old"u8.ToArray(), GameArchive.Read([Path("broken.dat"), Path("older.dat")], "only-older"));
    }

    [Fact]
    public void A_folder_without_patch_txt_has_no_archives() => Assert.Empty(GameArchive.InFolder(folder));

    [Fact]
    public void A_patch_txt_line_that_leads_out_of_the_folder_is_skipped()
    {
        File.WriteAllText(Path("patch.txt"), "..\\elsewhere.dat\r\n\\\\host\\share\\x.dat\r\nC:\\Windows\\x.dat\r\nsub\\inside.dat\r\n");
        Assert.Equal([Path(@"sub\inside.dat")], GameArchive.InFolder(folder));
    }

    [Fact]
    public void A_packed_entry_that_claims_more_than_lzss_can_make_is_refused()
    {
        Write(Path("a.dat"), new Stored("packed", Packed, Packed.Length * 9 + 1));
        Assert.Throws<InvalidDataException>(() => GameArchive.Open(Path("a.dat")).Dispose());
    }

    // --- The slab ---------------------------------------------------------------------------

    static byte[] Bmp(int width, int height) =>
        [(byte)'B', (byte)'M', .. new byte[16], .. BitConverter.GetBytes(width), .. BitConverter.GetBytes(height), .. new byte[40]];

    [Fact]
    public void The_slab_comes_out_of_the_game_folder()
    {
        var bmp = Bmp(504, 280);
        File.WriteAllText(Path("patch.txt"), "merge.dat\r\n");
        Write(Path("merge.dat"), Stored.Raw(SlabTexture.Entry.ToUpperInvariant(), Scrambled(bmp)));
        Assert.Equal(bmp, SlabTexture.Read(folder));
    }

    [Fact]
    public void Without_the_slab_there_is_none()
    {
        Assert.Null(SlabTexture.Read((string?)null));
        Assert.Null(SlabTexture.Read(Path("nowhere")));
        Assert.Null(SlabTexture.Read(folder));
        File.WriteAllText(Path("patch.txt"), "merge.dat\r\n");
        File.WriteAllBytes(Path("merge.dat"), "KCEJYUGI\xff"u8.ToArray());
        Assert.Null(SlabTexture.Read(folder));
        Write(Path("merge.dat"), Stored.Raw(SlabTexture.Entry, "not a picture at all, not by a long way: no BM in front"u8.ToArray()));
        Assert.Null(SlabTexture.Read(folder));
    }

    [InstalledGameFact]
    public void The_installed_game_has_the_slab()
    {
        var bmp = SlabTexture.Read(InstalledGame.Folder);
        Assert.NotNull(bmp);
        int width = BitConverter.ToInt32(bmp, 18), height = BitConverter.ToInt32(bmp, 22);
        int bits = BitConverter.ToUInt16(bmp, 28);
        output.WriteLine($"{SlabTexture.Entry}: {bmp.Length} bytes, {width} x {height}, {bits} bits a pixel");
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
        Assert.Equal((504, 280, 24), (width, height, bits));
    }
}

/// <summary>The February game folder the retail client is in, when it is on the machine.</summary>
static class InstalledGame
{
    public static string? Folder { get; } =
        RetailClient.Path is { } client && System.IO.Path.GetDirectoryName(client) is { } folder
        && File.Exists(System.IO.Path.Combine(folder, GameFolder.PatchList))
            ? folder
            : null;
}

/// <summary>A test that needs the installed game's data, skipped where it is not on the machine.</summary>
sealed class InstalledGameFactAttribute : FactAttribute
{
    public InstalledGameFactAttribute()
    {
        if (InstalledGame.Folder is null)
            Skip = "needs the February 2006 game folder: set CHAOS_LAUNCHER_CLIENT to its online_pc.dat";
    }
}
