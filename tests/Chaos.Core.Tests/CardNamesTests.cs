using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Chaos.Game;
using Xunit.Abstractions;

namespace Chaos.Tests;

public sealed class CardNamesTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "bc-names-" + Guid.NewGuid().ToString("N"));

    string Bc => Path.Combine(root, "bc");
    string Game => Path.Combine(root, "game");
    string Outside => Path.Combine(root, "outside");

    string Table(CardNames.Language language) => Path.Combine(Bc, language.Entry);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    // Name bytes and table hashes, from a real install.
    sealed record Pin(string Code, string Bytes, string InputSha, string OutputSha);

    static readonly Pin[] Pins =
    [
        new("ENG", "4D 61 73 6B 20 6F 66 20 57 69 6C 6C 20 61 6E 64 20 54 65 73 74 69 6D 6F 6E 79",
            "993f3019649cb5d9f4c0963b918459bf1600b922d7236d59985f74c6e05850f3",
            "8b111526b87cbc506cb433c2edc1a418ea927f10b36c47392e590adac3a1a82a"),
        new("GER", "4D 61 73 6B 65 20 64 65 73 20 57 69 6C 6C 65 6E 73 20 75 6E 64 20 5A 65 75 67 6E 69 73 73 65 73",
            "99c4ffebd3a31f10e7a72b9667bdb4efdb9a9cca208add66450a187c89da5061",
            "8906eb84f5c8d48f5b9b5f578498b88edc5b789689f682fb4959e8435fc3d478"),
        new("FRA", "4D 61 73 71 75 65 20 64 65 20 56 6F 6C 6F 6E 74 E9 20 65 74 20 64 65 20 4A 75 67 65 6D 65 6E 74",
            "b72f05c3acb5a1365b1f52d4f4958b465a718b2d2d7423fcf1d92d310928a5c9",
            "2ed748848c244e0a1860dd456b1f6248ee2f88f6c63e0ca97790b46d53db2709"),
        new("ITA", "4D 61 73 63 68 65 72 61 20 64 65 6C 6C 61 20 56 6F 6C 6F 6E 74 E0 20 65 20 64 65 6C 6C 61 20 50 72 6F 76 61",
            "7c7fcf79e3e8070e8d0d2c0abb6a1c0a5092b0dc1c3bb707ed0da244dcdb1008",
            "b9d737735c605ff6021157144472f0619c3bcf4cebf5849e0f055e642a490144"),
        new("SPA", "4D E1 73 63 61 72 61 20 64 65 20 56 6F 6C 75 6E 74 61 64 20 79 20 54 65 73 74 69 6D 6F 6E 69 6F",
            "b4aaba6a300690603149f3f0ad670b5718e9447d60bf19b6187da279c72fb2c0",
            "0ea2ab4ea31d3cee254810daaadbc1f9601cf8eacee7a6f9bec283d63bfe9b4f"),
    ];

    static CardNames.Language Lang(string code) => CardNames.Languages.Single(language => language.Code == code);

    // Slots 0 and 1024 empty, the rest named.
    static byte[] Names()
    {
        var table = new byte[CardNames.Records * CardNames.RecordLength];
        for (int slot = 1; slot < CardNames.Records; slot++)
            if (slot != CardNames.Slot)
                Encoding.ASCII.GetBytes($"Card {slot}", table.AsSpan(slot * CardNames.RecordLength));
        return table;
    }

    static byte[] Numbers(ushort number = CardNames.Number)
    {
        var table = new byte[CardNames.Records * 2];
        for (int slot = 0; slot < CardNames.Records; slot++)
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(slot * 2), (ushort)(slot + 234));
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(CardNames.Slot * 2), number);
        return table;
    }

    // Reads a record like the client.
    static string ReadLikeClient(byte[] table, int slot)
    {
        var record = table.AsSpan((slot & 0xFFFF) * 64, 64);
        return Encoding.Latin1.GetString(record[..record.IndexOf((byte)0)]);
    }

    static byte[] Named()
    {
        var table = Names();
        Encoding.ASCII.GetBytes("Other", table.AsSpan(CardNames.Slot * CardNames.RecordLength));
        return table;
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // An archive with a table per language, unless names gives null.
    static void Archive(string path, Func<CardNames.Language, byte[]?>? names = null, byte[]? numbers = null)
    {
        var entries = new List<GameArchiveTests.Stored>();
        foreach (var language in CardNames.Languages)
            if ((names is null ? Names() : names(language)) is { } table)
                entries.Add(GameArchiveTests.Stored.Raw(language.Entry, table));
        entries.Add(GameArchiveTests.Stored.Raw(CardNames.NumberEntry, numbers ?? Numbers()));
        GameArchiveTests.Write(path, [.. entries]);
    }

    void Install(Func<CardNames.Language, byte[]?>? names = null, byte[]? numbers = null, string patch = "merge.dat\r\n")
    {
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "patch.txt"), patch);
        Archive(Path.Combine(Game, "merge.dat"), names, numbers);
    }

    void PutOld(CardNames.Language language, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Table(language))!);
        File.WriteAllBytes(Table(language), bytes);
    }

    [Fact]
    public void NamesAreKonamis()
    {
        Assert.Equal(["ENG", "GER", "FRA", "ITA", "SPA"], CardNames.Languages.Select(language => language.Code));
        Assert.Equal([26, 32, 32, 36, 32], CardNames.Languages.Select(language => language.Bytes.Length));
        foreach (var pin in Pins)
        {
            var bytes = Lang(pin.Code).Bytes;
            Assert.Equal(pin.Bytes.Replace(" ", ""), Convert.ToHexString(bytes));
            Assert.True(bytes.Length < CardNames.RecordLength);
            Assert.DoesNotContain((byte)'<', bytes);
            Assert.DoesNotContain((byte)'>', bytes);
            Assert.DoesNotContain((byte)0, bytes);
        }
        Assert.Equal(
            [@"data\bin\CARD_NameENG.bin", @"data\bin\CARD_NameGER.bin", @"data\bin\CARD_NameFRA.bin",
             @"data\bin\CARD_NameITA.bin", @"data\bin\CARD_NameSPA.bin"],
            CardNames.Entries);
        // Accented letters are single bytes.
        Assert.Contains((byte)0xE9, Lang("FRA").Bytes);
        Assert.Contains((byte)0xE0, Lang("ITA").Bytes);
        Assert.Contains((byte)0xE1, Lang("SPA").Bytes);
    }

    [Theory]
    [InlineData("ENG")]
    [InlineData("GER")]
    [InlineData("FRA")]
    [InlineData("ITA")]
    [InlineData("SPA")]
    public void FillsOnlyRecord1024(string code)
    {
        var language = Lang(code);
        var input = Names();
        var before = (byte[])input.Clone();
        var built = CardNames.Build(language, input, Numbers());
        Assert.NotNull(built);
        Assert.Equal(before, input);
        Assert.Equal(150_464, built.Length);
        Assert.Equal(language.Name, ReadLikeClient(built, 1024));
        int length = language.Bytes.Length;
        var changed = Enumerable.Range(0, built.Length).Where(i => built[i] != input[i]);
        Assert.Equal(Enumerable.Range(0x10000, length), changed);
        Assert.All(built.AsSpan(0x10000 + length, CardNames.RecordLength - length).ToArray(), b => Assert.Equal(0, b));
        for (int slot = 0; slot < CardNames.Records; slot++)
            if (slot != CardNames.Slot)
                Assert.Equal(ReadLikeClient(input, slot), ReadLikeClient(built, slot));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("named")]
    [InlineData("last byte")]
    [InlineData("other number")]
    [InlineData("short numbers")]
    [InlineData("long numbers")]
    [InlineData("no numbers")]
    [InlineData("no names")]
    [InlineData("ypk1")]
    public void LeavesOtherTablesAlone(string how)
    {
        byte[]? names = Names(), numbers = Numbers();
        switch (how)
        {
            case "short":
                names = names[..^64];
                break;
            case "long":
                names = [.. names, .. new byte[64]];
                break;
            case "named":
                names = Named();
                break;
            case "last byte":
                names[CardNames.Slot * CardNames.RecordLength + 63] = 1;
                break;
            case "other number":
                numbers = Numbers(1257);
                break;
            case "short numbers":
                numbers = numbers[..4700];
                break;
            case "long numbers":
                numbers = [.. numbers, 0, 0];
                break;
            case "no numbers":
                numbers = null;
                break;
            case "no names":
                names = null;
                break;
            case "ypk1":
                "YPK1"u8.CopyTo(names);
                break;
        }
        Assert.All(CardNames.Languages, language => Assert.Null(CardNames.Build(language, names, numbers)));
    }

    [Fact]
    public void WritesTablesToBc()
    {
        Install();
        Assert.Equal(CardNamesOutcome.Written, CardNames.Write(Bc, Game));
        foreach (var language in CardNames.Languages)
            Assert.Equal(CardNames.Build(language, Names(), Numbers()), File.ReadAllBytes(Table(language)));
        Assert.Equal(CardNames.Languages.Select(Table).Order(), Directory.GetFiles(Bc, "*", SearchOption.AllDirectories).Order());
        Assert.Equal(CardNamesOutcome.Written, CardNames.Write(Bc, Game));
    }

    [Fact]
    public void GuardsEachTable()
    {
        var german = Lang("GER");
        var french = Lang("FRA");
        PutOld(german, [1, 2, 3]);
        PutOld(french, [4, 5, 6]);
        // GER names the record already, and FRA is not in the archive.
        Install(language => language == german ? Named() : language == french ? null : Names());
        Assert.Equal(CardNamesOutcome.Written, CardNames.Write(Bc, Game));
        foreach (var language in CardNames.Languages)
            Assert.Equal(language != german && language != french, File.Exists(Table(language)));
        Assert.Equal(CardNames.Build(Lang("SPA"), Names(), Numbers()), File.ReadAllBytes(Table(Lang("SPA"))));
    }

    [Fact]
    public void UnscramblesTable()
    {
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "patch.txt"), "merge.dat\r\n");
        Archive(Path.Combine(Game, "merge.dat"), _ => GameArchiveTests.Scrambled(Names()));
        Assert.Equal(CardNamesOutcome.Written, CardNames.Write(Bc, Game));
        var table = File.ReadAllBytes(Table(Lang("ENG")));
        Assert.Equal(150_464, table.Length);
        Assert.Equal(CardNames.Build(Lang("ENG"), Names(), Numbers()), table);
        Assert.All(table[..64], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ReadsTheClientsTable()
    {
        Install(patch: "newer.dat\r\nmerge.dat\r\n");
        Archive(Path.Combine(Game, "newer.dat"), _ => Named());
        Assert.Equal(CardNamesOutcome.None, CardNames.Write(Bc, Game));
        Assert.False(Directory.Exists(Bc));
    }

    [Fact]
    public void OddPatchListWritesNothing()
    {
        Install(patch: "..\\elsewhere.dat\r\nmerge.dat\r\n");
        PutOld(Lang("ENG"), [1, 2, 3]);
        Assert.Equal(CardNamesOutcome.None, CardNames.Write(Bc, Game));
        Assert.Empty(Directory.GetFiles(Bc, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void NamedTableRemovesFile()
    {
        Install(_ => Named());
        PutOld(Lang("ENG"), [1, 2, 3]);
        Assert.Equal(CardNamesOutcome.None, CardNames.Write(Bc, Game));
        Assert.False(File.Exists(Table(Lang("ENG"))));
    }

    [Fact]
    public void NoGameRemovesFile()
    {
        PutOld(Lang("ENG"), [1, 2, 3]);
        Directory.CreateDirectory(Game);
        Assert.Equal(CardNamesOutcome.None, CardNames.Write(Bc, Game));
        Assert.False(File.Exists(Table(Lang("ENG"))));
        Directory.Delete(Bc, recursive: true);
        Assert.Equal(CardNamesOutcome.None, CardNames.Write(Bc, Game));
        Assert.False(Directory.Exists(Bc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("data")]
    [InlineData(@"data\bin")]
    public void RefusesLinkedFolder(string under)
    {
        Install();
        Directory.CreateDirectory(Outside);
        string link = Path.Combine(Bc, under);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CardPicturesTests.Link("/J", link, Outside);
        try
        {
            Assert.Equal(CardNamesOutcome.Unwritable, CardNames.Write(Bc, Game));
            Assert.Empty(Directory.GetFileSystemEntries(Outside));
        }
        finally
        {
            // The link only, not what it points to.
            Directory.Delete(link);
        }
    }

    [Fact]
    public void LockedFileStays()
    {
        Install();
        var english = Lang("ENG");
        PutOld(english, [1, 2, 3]);
        using (new FileStream(Table(english), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(CardNamesOutcome.Unwritable, CardNames.Write(Bc, Game));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Table(english)));
        Assert.False(File.Exists(Table(english) + ".new"));
        foreach (var language in CardNames.Languages.Where(language => language != english))
            Assert.Equal(CardNames.Build(language, Names(), Numbers()), File.ReadAllBytes(Table(language)));
    }

    [InstalledGameFact]
    public void InstalledGameGetsNames()
    {
        var archives = GameArchive.InFolder(InstalledGame.Folder!);
        var numbers = GameArchive.Read(archives, CardNames.NumberEntry);
        Assert.NotNull(numbers);
        foreach (var pin in Pins)
        {
            var language = Lang(pin.Code);
            var input = GameArchive.Read(archives, language.Entry);
            Assert.NotNull(input);
            var built = CardNames.Build(language, input, numbers);
            Assert.NotNull(built);
            output.WriteLine($"{language.Entry}: {Hash(input)} -> {Hash(built)}");
            Assert.Equal(pin.InputSha, Hash(input));
            Assert.Equal(pin.OutputSha, Hash(built));
            var changed = Enumerable.Range(0, built.Length).Where(i => built[i] != input[i]).ToList();
            Assert.Equal(Enumerable.Range(0x10000, language.Bytes.Length), changed);
            Assert.Equal(language.Bytes, built[0x10000..(0x10000 + language.Bytes.Length)]);
            Assert.Equal(language.Name, ReadLikeClient(built, 1024));
            for (int slot = 0; slot < CardNames.Records; slot++)
                if (slot != CardNames.Slot)
                    Assert.Equal(ReadLikeClient(input, slot), ReadLikeClient(built, slot));
        }
    }
}
