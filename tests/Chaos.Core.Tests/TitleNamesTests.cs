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

        /// <summary>What a request waits for before it is answered.</summary>
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

    // A reply as the server's GET /api/titles gives one (docs/WEB-API.md): the game's own titles,
    // the seasonal entry with no number, a season's own title, and a hidden title with no name.
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

    /// <summary>
    /// What the patched client makes of <paramref name="file"/> for <paramref name="title"/>, as
    /// its routines at 0xb98a00 read it: the name, or null for "Unknown"; and the plate's colour.
    /// </summary>
    static (string? Name, uint Colour) AsTheClientReads(byte[]? file, int title)
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
    public void The_file_has_a_record_for_every_number_up_to_the_highest_with_its_name_and_colour()
    {
        var file = TitleNames.FileFrom(Encoding.UTF8.GetBytes(Catalog))!;
        int count = 7936 - 4096 + 1;
        Assert.Equal(16 + count * 64, file.Length);
        Assert.Equal("CHAOSTTL"u8.ToArray(), file[..8]);
        Assert.Equal((uint)count, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12)));

        Assert.Equal(("Star Chip Holder", 0x101010u), AsTheClientReads(file, 0x1000));
        Assert.Equal(("Duelist of the Roses", 0xC0C0C0u), AsTheClientReads(file, 0x1001));
        Assert.Equal(("King of Games (Season 0)", 0xFFu), AsTheClientReads(file, 0x1F00));
        // The hidden title, and the numbers the server doesn't give, are "Unknown".
        Assert.Equal((null, 0x101010u), AsTheClientReads(file, 0x1004));
        Assert.Equal((null, 0x101010u), AsTheClientReads(file, 0x1002));
        Assert.Equal((null, 0x101010u), AsTheClientReads(file, 0x1F01));

        // A record: the colour, then the name and zeros to its end.
        var record = file.AsSpan(16 + 64, 64);
        Assert.Equal(0xC0C0C0u, BinaryPrimitives.ReadUInt32LittleEndian(record));
        Assert.Equal("Duelist of the Roses"u8.ToArray(), record[4..24].ToArray());
        Assert.All(record[24..].ToArray(), b => Assert.Equal(0, b));
        // One with no name: the client's own colour, and zeros.
        var empty = file.AsSpan(16 + 2 * 64, 64);
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(empty));
        Assert.All(empty[4..].ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_catalog_without_titles_of_the_servers_own_gives_an_empty_file()
    {
        var file = TitleNames.FileFrom("""{"ok": true, "titles": [{"number": 0, "name": "Novice Duelist"}, {"number": 61696, "name": "DUELIST"}]}"""u8)!;
        Assert.Equal("CHAOSTTL\0\0\0\0\0\0\0\0"u8.ToArray(), file);
        Assert.Equal((null, 0x101010u), AsTheClientReads(file, 0x1000));
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
    public void Only_plain_ascii_without_markup_is_named(string name, bool named)
    {
        Assert.Equal(named, TitleNames.Fits(name));
        var file = TitleNames.Build([new(0x1000, 0xFFFFFFFF, name)]);
        Assert.Equal(named ? name : null, AsTheClientReads(file, 0x1000).Name);
    }

    [Fact]
    public void A_name_of_59_bytes_fits_and_one_of_60_does_not()
    {
        string longest = new('x', 59);
        Assert.True(TitleNames.Fits(longest));
        Assert.False(TitleNames.Fits(longest + "x"));
        Assert.False(TitleNames.Fits(null));
        var file = TitleNames.Build([new(0x1000, 0xFFFFFFFF, longest), new(0x1001, 0xFFFFFFFF, longest + "x")]);
        // The name's zero is the record's last byte.
        Assert.Equal(0, file[16 + 63]);
        Assert.Equal(longest, AsTheClientReads(file, 0x1000).Name);
        Assert.Null(AsTheClientReads(file, 0x1001).Name);
    }

    [Fact]
    public void The_first_of_a_number_given_twice_counts_and_numbers_outside_the_range_are_left_out()
    {
        var file = TitleNames.FileFrom("""
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
        Assert.Equal(("First", 0xFFu), AsTheClientReads(file, 0x1000));
        Assert.Equal((null, 0x101010u), AsTheClientReads(file, 0x1001));
        Assert.Equal((null, 0x101010u), AsTheClientReads(file, 0x1002));
        Assert.Equal(("No colour", 0x101010u), AsTheClientReads(file, 0x1003));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16 + 3 * 64)));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16 + 4 * 64)));
    }

    // The highest the client's range takes, 0x1fff, comes to 256 KB.
    [Fact]
    public void The_whole_range_fits_in_the_file()
    {
        var file = TitleNames.Build([new(0x1FFF, 0xFF, "Last")]);
        Assert.Equal(16 + 0x1000 * 64, file.Length);
        Assert.Equal(("Last", 0xFFu), AsTheClientReads(file, 0x1FFF));
    }

    [Theory]
    [InlineData("<html><body>Sign in to the Wi-Fi</body></html>")]
    [InlineData("""{"ok": false, "error": "rate_limited"}""")]
    [InlineData("""{"ok": true}""")]
    [InlineData("""{"ok": true, "titles": {"number": 4096}}""")]
    [InlineData("""[{"number": 4096, "name": "Star Chip Holder"}]""")]
    [InlineData("""{"ok": true, "titles": [{"number": 4096, "name": "Star""")]
    public void A_reply_that_is_no_catalog_gives_no_file(string reply) =>
        Assert.Null(TitleNames.FileFrom(Encoding.UTF8.GetBytes(reply)));

    [Fact]
    public async Task The_servers_titles_are_written_into_bc()
    {
        Serve(Catalog);
        Assert.Equal(TitlesOutcome.Written, await Names().UpdateAsync(Bc));
        Assert.Equal(TitleNames.FileFrom(Encoding.UTF8.GetBytes(Catalog)), File.ReadAllBytes(TitlesFile));
        Assert.Equal(["GET /api/titles"], server.Asked);
        // Nothing beside it is left over from the write.
        Assert.Equal([TitlesFile], Directory.GetFileSystemEntries(Bc));
    }

    [Fact]
    public async Task A_new_list_replaces_the_old_file()
    {
        Directory.CreateDirectory(Bc);
        File.WriteAllBytes(TitlesFile, TitleNames.Build([new(0x1005, 0xFF, "Old")]));
        Serve(Catalog);
        Assert.Equal(TitlesOutcome.Written, await Names().UpdateAsync(Bc));
        Assert.Equal(TitleNames.FileFrom(Encoding.UTF8.GetBytes(Catalog)), File.ReadAllBytes(TitlesFile));
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
    public async Task A_server_in_trouble_leaves_the_last_file_there(HttpStatusCode status)
    {
        PutLastGood();
        server.Answer = () => new HttpResponseMessage(status);
        Assert.Equal(TitlesOutcome.Unreachable, await Names().UpdateAsync(Bc));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }

    [Fact]
    public async Task Without_the_network_the_last_file_stays()
    {
        PutLastGood();
        server.Wait = _ => throw new HttpRequestException("No such host is known.");
        Assert.Equal(TitlesOutcome.Unreachable, await Names().UpdateAsync(Bc));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }

    [Fact]
    public async Task A_reply_that_is_no_catalog_leaves_the_last_file_there()
    {
        PutLastGood();
        Serve("<html><body>Sign in to the Wi-Fi</body></html>");
        Assert.Equal(TitlesOutcome.Refused, await Names().UpdateAsync(Bc));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }

    [Fact]
    public async Task A_reply_over_1_MB_is_refused()
    {
        PutLastGood();
        Serve("{\"ok\": true, \"titles\": [" + string.Concat(Enumerable.Repeat("{}, ", 300_000)) + "{}]}");
        Assert.Equal(TitlesOutcome.Refused, await Names().UpdateAsync(Bc));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }

    // Only a 404 says the server has no titles.
    [Fact]
    public async Task A_server_without_titles_takes_the_file_away()
    {
        PutLastGood();
        Assert.Equal(TitlesOutcome.None, await Names().UpdateAsync(Bc));
        Assert.False(File.Exists(TitlesFile));
        // Nor does it mind there being no bc at all.
        Directory.Delete(Bc);
        Assert.Equal(TitlesOutcome.None, await Names().UpdateAsync(Bc));
        Assert.False(Directory.Exists(Bc));
    }

    [Fact]
    public async Task A_server_that_never_answers_is_given_five_seconds()
    {
        PutLastGood();
        Serve(Catalog);
        using var stuck = new SemaphoreSlim(0);
        server.Wait = async cancel =>
        {
            stuck.Release();
            await Task.Delay(Timeout.Infinite, cancel);
        };
        var update = Names().UpdateAsync(Bc);
        Assert.True(await stuck.WaitAsync(TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(4.9));
        await Task.Delay(50);
        Assert.False(update.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(TitlesOutcome.Unreachable, await update);
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }

    [Fact]
    public async Task Called_off_by_the_caller_it_throws()
    {
        server.Wait = cancel => Task.Delay(Timeout.Infinite, cancel);
        using var cancel = new CancellationTokenSource();
        var update = Names().UpdateAsync(Bc, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
    }

    [Fact]
    public void A_file_that_cannot_be_replaced_stays_as_it_was()
    {
        PutLastGood();
        var reply = new TitlesReply(TitlesOutcome.Written, TitleNames.FileFrom(Encoding.UTF8.GetBytes(Catalog)));
        using (new FileStream(TitlesFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(TitlesOutcome.Unwritable, TitleNames.Write(Bc, reply));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
        Assert.False(File.Exists(TitlesFile + ".new"));
    }

    [Fact]
    public void A_reply_without_a_file_writes_nothing()
    {
        PutLastGood();
        Assert.Equal(TitlesOutcome.Refused, TitleNames.Write(Bc, new TitlesReply(TitlesOutcome.Written, null)));
        Assert.Equal(TitlesOutcome.Unreachable, TitleNames.Write(Bc, new TitlesReply(TitlesOutcome.Unreachable, null)));
        Assert.Equal(LastGood, File.ReadAllBytes(TitlesFile));
    }
}
