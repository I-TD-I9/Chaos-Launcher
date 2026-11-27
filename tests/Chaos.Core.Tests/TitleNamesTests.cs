using System.Buffers.Binary;
using System.Net;
using System.Text;
using Chaos.Titles;

namespace Chaos.Tests;

public sealed class TitleNamesTests : IDisposable
{
    static readonly Uri Api = new("https://api.example.test/");
    readonly string root = Path.Combine(Path.GetTempPath(), "bc-titles-" + Guid.NewGuid().ToString("N"));
    readonly FakeServer server = new();
    readonly ManualClock clock = new();

    string Bc => Path.Combine(root, "bc");
    string TitlesFile => Path.Combine(Bc, TitleNames.FileName);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    sealed class FakeServer : HttpMessageHandler
    {
        public Func<HttpResponseMessage>? Answer { get; set; }

        public Func<CancellationToken, Task>? Wait { get; set; }

        public List<string> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            lock (Asked)
                Asked.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (Wait is { } wait)
                await wait(cancel);
            return Answer?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    TitleNames Names() => new(new HttpClient(server), Api, clock);

    void Serve(string json) => server.Answer = () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)),
    };

    // A sample GET /api/titles reply (docs/WEB-API.md).
    const string Catalog = """
        {"ok": true, "own_worn": false,
         "rarities": [{"name": "Common", "colour": 4294967295, "rgb": null}],
         "titles": [
           {"number": 0, "name": "Novice Duelist", "rarity": "Common", "game": true, "colour": 4294967295},
           {"number": 61696, "name": "DUELIST", "rarity": "Common", "game": true, "colour": 4294967295},
           {"number": 4096, "name": "Star Chip Holder", "rarity": "Uncommon", "group": "Level",
            "how": "Reach Lv. 3.", "hidden": false, "earnable": true, "game": false,
            "colour": 4294967295, "rgb": null},
           {"number": 4097, "name": "Duelist of the Roses", "rarity": "Epic", "colour": 12632256, "rgb": "#c0c0c0"},
           {"number": null, "name": "King of Games (Season N)", "series": true, "colour": 255},
           {"number": 4100, "name": null, "how": null, "hidden": true, "colour": 4294967295},
           {"number": 7936, "name": "King of Games (Season 0)", "season": 0, "series": false, "colour": 255}],
         "game": [{"number": 61697, "name": "GUNSLINGER", "rarity": "Rare"}]}
        """;

    static (string? Name, uint Colour) ReadLikeClient(byte[]? file, int title)
    {
        const uint Konami = 0x101010;
        if (title is < 0x1000 or > 0x1FFF || file is null || file.Length < 16 || !file.AsSpan(0, 8).SequenceEqual("CHAOSTTL"u8))
            return (null, Konami);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        if (count > (uint)(file.Length - 16) >> 6)
            return (null, Konami);
        int i = title - 0x1000;
        if (i >= count || file[16 + i * 64 + 4] == 0)
            return (null, Konami);
        var record = file.AsSpan(16 + i * 64, 64);
        uint colour = BinaryPrimitives.ReadUInt32LittleEndian(record);
        string name = Encoding.ASCII.GetString(record[4..][..record[4..].IndexOf((byte)0)]);
        return (name, (colour & 0xFF000000) != 0 ? Konami : colour);
    }

    [Fact]
    public void WritesRecordPerNumber()
    {
        var file = TitleNames.ParseReply(Encoding.UTF8.GetBytes(Catalog))!;
        int count = 7936 - 4096 + 1;
        Assert.Equal(16 + count * 64, file.Length);
        Assert.Equal("CHAOSTTL"u8.ToArray(), file[..8]);
        Assert.Equal((uint)count, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12)));

        Assert.Equal(("Star Chip Holder", 0x101010u), ReadLikeClient(file, 0x1000));
        Assert.Equal(("Duelist of the Roses", 0xC0C0C0u), ReadLikeClient(file, 0x1001));
        Assert.Equal(("King of Games (Season 0)", 0xFFu), ReadLikeClient(file, 0x1F00));
        // Hidden and missing titles read as "Unknown".
        Assert.Equal((null, 0x101010u), ReadLikeClient(file, 0x1004));
        Assert.Equal((null, 0x101010u), ReadLikeClient(file, 0x1002));
        Assert.Equal((null, 0x101010u), ReadLikeClient(file, 0x1F01));

        // Record: colour, then the name padded with zeros.
        var record = file.AsSpan(16 + 64, 64);
        Assert.Equal(0xC0C0C0u, BinaryPrimitives.ReadUInt32LittleEndian(record));
        Assert.Equal("Duelist of the Roses"u8.ToArray(), record[4..24].ToArray());
        Assert.All(record[24..].ToArray(), b => Assert.Equal(0, b));
        // No name: default colour and zeros.
        var empty = file.AsSpan(16 + 2 * 64, 64);
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(empty));
        Assert.All(empty[4..].ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void NoOwnTitlesGivesEmptyFile()
    {
        var file = TitleNames.ParseReply("""{"ok": true, "titles": [{"number": 0, "name": "Novice Duelist"}, {"number": 61696, "name": "DUELIST"}]}"""u8)!;
        Assert.Equal("CHAOSTTL\0\0\0\0\0\0\0\0"u8.ToArray(), file);
        Assert.Equal((null, 0x101010u), ReadLikeClient(file, 0x1000));
    }

    [Theory]
    [InlineData("Star Chip Holder", true)]
    [InlineData("It's Time to Duel!", true)]
    [InlineData("King of Games (Season 12)", true)]
    [InlineData("~ !\"#$%'()*+,-./:;=?@[\\]^_`{|}", true)]
    [InlineData("<B>Bold</B>", false)]
    [InlineData("Duelist >", false)]
    [InlineData("Ray & Temperature", false)]
    [InlineData("Duelist &lt;", false)]
    [InlineData("Café Duelist", false)]
    [InlineData("決闘者", false)]
    [InlineData("Tab\tbed", false)]
    [InlineData("Nul\0led", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("   ", false)]
    [InlineData(" Duelist", false)]
    [InlineData("Duelist ", false)]
    [InlineData("Duel  King", true)]
    public void NamesOnlyPlainAscii(string name, bool named)
    {
        Assert.Equal(named, TitleNames.Fits(name));
        var file = TitleNames.Build([new(0x1000, 0xFFFFFFFF, name)]);
        Assert.Equal(named ? name : null, ReadLikeClient(file, 0x1000).Name);
    }

    [Fact]
    public void NameFitsUpTo59Bytes()
    {
        string longest = new('x', 59);
        Assert.True(TitleNames.Fits(longest));
        Assert.False(TitleNames.Fits(longest + "x"));
        Assert.False(TitleNames.Fits(null));
        var file = TitleNames.Build([new(0x1000, 0xFFFFFFFF, longest), new(0x1001, 0xFFFFFFFF, longest + "x")]);
        // The NUL is the record's last byte.
        Assert.Equal(0, file[16 + 63]);
        Assert.Equal(longest, ReadLikeClient(file, 0x1000).Name);
        Assert.Null(ReadLikeClient(file, 0x1001).Name);
    }

    [Fact]
    public void SkipsDuplicatesAndOutOfRange()
    {
        var file = TitleNames.ParseReply("""
            {"ok": true, "titles": [
              {"number": 4096, "name": "First", "colour": 255},
              {"number": 4096, "name": "Second", "colour": 65280},
              {"number": 4095, "name": "Below"},
              {"number": 8192, "name": "Above"},
              {"number": 4097.5, "name": "Half"},
              {"number": "4098", "name": "Text"},
              {"number": 4099, "name": "No colour"},
              {"number": 4100, "name": "Odd colour", "colour": -1},
              ["not", "a", "title"]]}
            """u8)!;
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)));
        Assert.Equal(("First", 0xFFu), ReadLikeClient(file, 0x1000));
        Assert.Equal((null, 0x101010u), ReadLikeClient(file, 0x1001));
        Assert.Equal((null, 0x101010u), ReadLikeClient(file, 0x1002));
        Assert.Equal(("No colour", 0x101010u), ReadLikeClient(file, 0x1003));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16 + 3 * 64)));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16 + 4 * 64)));
    }

    [Fact]
    public void WholeRangeFits()
    {
        var file = TitleNames.Build([new(0x1FFF, 0xFF, "Last")]);
        Assert.Equal(16 + 0x1000 * 64, file.Length);
        Assert.Equal(("Last", 0xFFu), ReadLikeClient(file, 0x1FFF));
    }

    [Theory]
    [InlineData("<html><body>Sign in to the Wi-Fi</body></html>")]
    [InlineData("""{"ok": false, "error": "rate_limited"}""")]
    [InlineData("""{"ok": true}""")]
    [InlineData("""{"ok": true, "titles": {"number": 4096}}""")]
    [InlineData("""[{"number": 4096, "name": "Star Chip Holder"}]""")]
    [InlineData("""{"ok": true, "titles": [{"number": 4096, "name": "Star""")]
    public void BadReplyGivesNoFile(string reply) =>
        Assert.Null(TitleNames.ParseReply(Encoding.UTF8.GetBytes(reply)));

    [Fact]
    public async Task WritesTitlesToBc()
    {
        Serve(Catalog);
        Assert.Equal(TitlesOutcome.Written, await Names().UpdateAsync(Bc));
        Assert.Equal(TitleNames.ParseReply(Encoding.UTF8.GetBytes(Catalog)), File.ReadAllBytes(TitlesFile));
        Assert.Equal(["GET /api/titles"], server.Asked);
        // No temp files left behind.
        Assert.Equal([TitlesFile], Directory.GetFileSystemEntries(Bc));
    }

    [Fact]
    public async Task NewListReplacesFile()
    {
        Directory.CreateDirectory(Bc);
        File.WriteAllBytes(TitlesFile, TitleNames.Build([new(0x1005, 0xFF, "Old")]));
        Serve(Catalog);
        Assert.Equal(TitlesOutcome.Written, await Names().UpdateAsync(Bc));
        Assert.Equal(TitleNames.ParseReply(Encoding.UTF8.GetBytes(Catalog)), File.ReadAllBytes(TitlesFile));
    }

    static byte[] LastGood => TitleNames.Build([new(0x1000, 0xFFFFFFFF, "Star Chip Holder")]);

    void PutLastGood()
    {
        Directory.CreateDirectory(Bc);
        File.WriteAllBytes(TitlesFile, LastGood);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ServerErrorKeepsFile(HttpStatusCode status)
    {
        PutLastGood();
        server.Answer = () => new HttpResponseMessage(status);
        Assert.Equal(TitlesOutcome.Unreachable, await Names().UpdateAsync(Bc));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }

    [Fact]
    public async Task OfflineKeepsFile()
    {
        PutLastGood();
        server.Wait = _ => throw new HttpRequestException("No such host is known.");
        Assert.Equal(TitlesOutcome.Unreachable, await Names().UpdateAsync(Bc));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }

    [Fact]
    public async Task BadReplyKeepsFile()
    {
        PutLastGood();
        Serve("<html><body>Sign in to the Wi-Fi</body></html>");
        Assert.Equal(TitlesOutcome.Refused, await Names().UpdateAsync(Bc));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }

    [Fact]
    public async Task RejectsLargeReply()
    {
        PutLastGood();
        Serve("{\"ok\": true, \"titles\": [" + string.Concat(Enumerable.Repeat("{}, ", 300_000)) + "{}]}");
        Assert.Equal(TitlesOutcome.Refused, await Names().UpdateAsync(Bc));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }

    // Only a 404 says the server has no titles.
    [Fact]
    public async Task NoTitlesRemovesFile()
    {
        PutLastGood();
        Assert.Equal(TitlesOutcome.None, await Names().UpdateAsync(Bc));
        Assert.False(File.Exists(TitlesFile));
        // Also fine with no bc folder.
        Directory.Delete(Bc);
        Assert.Equal(TitlesOutcome.None, await Names().UpdateAsync(Bc));
        Assert.False(Directory.Exists(Bc));
    }

    [Fact]
    public async Task WaitsFiveSecondsForServer()
    {
        PutLastGood();
        Serve(Catalog);
        using var blocked = new SemaphoreSlim(0);
        server.Wait = async cancel =>
        {
            blocked.Release();
            await Task.Delay(Timeout.Infinite, cancel);
        };
        var update = Names().UpdateAsync(Bc);
        Assert.True(await blocked.WaitAsync(TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(4.9));
        await Task.Delay(50);
        Assert.False(update.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(TitlesOutcome.Unreachable, await update);
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }

    [Fact]
    public async Task ThrowsWhenCancelled()
    {
        server.Wait = cancel => Task.Delay(Timeout.Infinite, cancel);
        using var cancel = new CancellationTokenSource();
        var update = Names().UpdateAsync(Bc, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
    }

    [Fact]
    public void LockedFileStays()
    {
        PutLastGood();
        var reply = new TitlesReply(TitlesOutcome.Written, TitleNames.ParseReply(Encoding.UTF8.GetBytes(Catalog)));
        using (new FileStream(TitlesFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(TitlesOutcome.Unwritable, TitleNames.Write(Bc, reply));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
        Assert.False(File.Exists(TitlesFile + ".new"));
    }

    [Fact]
    public void NoFileWritesNothing()
    {
        PutLastGood();
        Assert.Equal(TitlesOutcome.Refused, TitleNames.Write(Bc, new TitlesReply(TitlesOutcome.Written, null)));
        Assert.Equal(TitlesOutcome.Unreachable, TitleNames.Write(Bc, new TitlesReply(TitlesOutcome.Unreachable, null)));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }
}
