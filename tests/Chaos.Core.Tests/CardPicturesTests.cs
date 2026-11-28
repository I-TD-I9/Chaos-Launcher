using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chaos.Game;
using Chaos.Pictures;
using Chaos.Titles;

namespace Chaos.Tests;

public sealed class CardPicturesTests : IDisposable
{
    static readonly Uri Api = new("https://api.example.test/");
    readonly string root = Path.Combine(Path.GetTempPath(), "bc-pictures-" + Guid.NewGuid().ToString("N"));
    readonly FakeServer server = new();
    readonly ManualClock clock = new();
    readonly List<TimeSpan> pauses = [];

    string Bc => Path.Combine(root, "bc");
    string Staging => Bc + ".partial";
    string Outside => Path.Combine(root, "outside");

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];

        public Dictionary<string, Func<CancellationToken, Task>> Waits { get; } = [];

        readonly List<string> asked = [];

        public List<string> Asked
        {
            get
            {
                lock (asked)
                    return [.. asked];
            }
        }

        public void Forget()
        {
            lock (asked)
                asked.Clear();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            string path = request.RequestUri!.AbsolutePath;
            lock (asked)
                asked.Add(path);
            if (Waits.TryGetValue(path, out var wait))
                await wait(cancel);
            return Routes.TryGetValue(path, out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    static string Stored(string path, byte[] content) => Sha(content) + Path.GetExtension(path);

    static HttpResponseMessage Ok(byte[] content, string type = "application/octet-stream")
    {
        var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        reply.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return reply;
    }

    // A pack of card pictures.
    static readonly Dictionary<string, byte[]> Pack = new()
    {
        ["data/card/list_card.txt"] = Encoding.ASCII.GetBytes("// slot 0\r\ncard_ura.bmp\r\n\r\nbc1441.jpg\r\n"),
        ["data/mini/list_card.txt"] = Encoding.ASCII.GetBytes("// slot 0\r\ncard_ura.bmp\r\n\r\nbc1441.jpg\r\n"),
        ["data/card/bc1441.jpg"] = [0xFF, 0xD8, 0xFF, .. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)), 0xFF, 0xD9],
        ["data/mini/bc1441.jpg"] = [0xFF, 0xD8, 0xFF, .. Enumerable.Range(0, 300).Select(i => (byte)(i * 11)), 0xFF, 0xD9],
    };

    static readonly string CardPicture = "/art/files/" + Stored("data/card/bc1441.jpg", Pack["data/card/bc1441.jpg"]);
    static readonly string MiniPicture = "/art/files/" + Stored("data/mini/bc1441.jpg", Pack["data/mini/bc1441.jpg"]);

    // Serves the pack and returns its manifest.
    string Serve(Dictionary<string, byte[]> pack)
    {
        string manifest = JsonSerializer.Serialize(new
        {
            format = 1,
            client = "Ver.060110.00",
            files = pack.OrderBy(file => file.Key, StringComparer.Ordinal)
                        .Select(file => new { path = file.Key, sha256 = Sha(file.Value), size = file.Value.Length }),
        });
        server.Routes["/art/manifest.json"] = () => Ok(Encoding.UTF8.GetBytes(manifest), "application/json");
        foreach (var (path, content) in pack)
            server.Routes["/art/files/" + Stored(path, content)] = () => Ok(content);
        return manifest;
    }

    CardPictures Pictures() => new(new HttpClient(server), Api, clock);

    async Task<PicturesOutcome> Update(List<PicturesProgress>? steps = null)
    {
        var update = Pictures().UpdateAsync(Bc, steps is null ? null : new Steps(steps));
        while (!update.IsCompleted)
        {
            if (clock.NextDue(TimeSpan.FromSeconds(5)) is { } pause)
            {
                pauses.Add(pause);
                clock.Advance(pause);
            }
            else
            {
                await Task.WhenAny(update, Task.Delay(5));
            }
        }
        return await update;
    }

    // Waits in real time for other threads.
    static async Task WaitUntil(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "timed out");
            await Task.Delay(5);
        }
    }

    sealed class Steps(List<PicturesProgress> steps) : IProgress<PicturesProgress>
    {
        public void Report(PicturesProgress value)
        {
            lock (steps)
                steps.Add(value);
        }
    }

    // Every file under bc, with its bytes.
    Dictionary<string, byte[]> ReadBc() =>
        !Directory.Exists(Bc) ? new Dictionary<string, byte[]>()
        : Directory.GetFiles(Bc, "*", SearchOption.AllDirectories)
                   .ToDictionary(file => Path.GetRelativePath(Bc, file).Replace('\\', '/'), File.ReadAllBytes);

    static void AssertSame(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (path, content) in expected)
            Assert.Equal(content, actual[path]);
    }

    void Put(string path, byte[] content)
    {
        string file = Path.Combine(Bc, path.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, content);
    }

    // mklink /J or /H, which need no admin rights.
    internal static void Link(string kind, string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink {kind} \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string error = mklink.StandardError.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0, error);
    }

    [Fact]
    public async Task FetchesMissingPictures()
    {
        Serve(Pack);
        var steps = new List<PicturesProgress>();
        Assert.Equal(PicturesOutcome.UpToDate, await Update(steps));
        AssertSame(Pack, ReadBc());
        Assert.False(Directory.Exists(Staging));
        // Check, then fetch; both lists share one file.
        Assert.Equal(new PicturesProgress(0, 0), steps[0]);
        Assert.Equal(new PicturesProgress(0, 3), steps[1]);
        Assert.Equal([1, 2, 3], steps.Skip(2).Select(step => step.Done).Order());
        Assert.Equal(4, server.Asked.Count);
        Assert.Equal("/art/manifest.json", server.Asked[0]);
        Assert.Empty(pauses);
    }

    [Fact]
    public async Task KeepsExistingPictures()
    {
        Serve(Pack);
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        string picture = Path.Combine(Bc, "data", "card", "bc1441.jpg");
        var written = File.GetLastWriteTimeUtc(picture).AddHours(-1);
        File.SetLastWriteTimeUtc(picture, written);
        server.Forget();

        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        Assert.Equal(["/art/manifest.json"], server.Asked);
        Assert.Equal(written, File.GetLastWriteTimeUtc(picture));
        AssertSame(Pack, ReadBc());
    }

    [Fact]
    public async Task RefetchesChangedPicture()
    {
        Serve(Pack);
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        // Same size, then a different size.
        var changed = (byte[])Pack["data/mini/bc1441.jpg"].Clone();
        changed[10] ^= 0xFF;
        Put("data/mini/bc1441.jpg", changed);
        server.Forget();
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        Assert.Equal(["/art/manifest.json", MiniPicture], server.Asked);
        Put("data/card/bc1441.jpg", [1, 2, 3]);
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        AssertSame(Pack, ReadBc());
    }

    [Fact]
    public async Task FetchesBothExtensions()
    {
        byte[] same = Encoding.ASCII.GetBytes("the same bytes\r\n");
        var pack = new Dictionary<string, byte[]> { ["data/card/aaa.jpg"] = same, ["data/card/list_card.txt"] = same };
        Put("data/card/old.jpg", [1, 2, 3]);
        Serve(pack);
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        AssertSame(pack, ReadBc());
        Assert.Contains("/art/files/" + Sha(same) + ".jpg", server.Asked);
        Assert.Contains("/art/files/" + Sha(same) + ".txt", server.Asked);
    }

    [Fact]
    public async Task RemovesUnlistedFiles()
    {
        Put("data/card/bc9999.jpg", [1]);
        Put("data/card/old/x.jpg", [2]);
        Put("Voice/BGM01/b_water.wav", [3]);
        Put("readme.txt", [4]);
        Serve(Pack);
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        AssertSame(Pack, ReadBc());
        Assert.False(Directory.Exists(Path.Combine(Bc, "Voice")));
        Assert.False(Directory.Exists(Path.Combine(Bc, "data", "card", "old")));
    }

    // Copy before pruning, so a failed copy removes nothing.
    [Fact]
    public async Task LockedPictureKeepsOldPictures()
    {
        Put("data/card/bc9999.jpg", [1]);
        Put("data/mini/bc1441.jpg", [2]);
        Serve(Pack);
        // Like a program holding the file open.
        using (new FileStream(Path.Combine(Bc, "data", "mini", "bc1441.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(PicturesOutcome.Unwritable, await Update());
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(Bc, "data", "card", "bc9999.jpg")));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(Bc, "data", "mini", "bc1441.jpg")));

        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        AssertSame(Pack, ReadBc());
    }

    [Theory]
    [InlineData("altered")]
    [InlineData("longer")]
    [InlineData("shorter")]
    public async Task RejectsBadFile(string how)
    {
        Put("data/card/list_card.txt", Encoding.ASCII.GetBytes("an older list\r\n"));
        Put("data/card/bc0001.jpg", [9, 9, 9]);
        var before = ReadBc();
        Serve(Pack);
        byte[] good = Pack["data/card/bc1441.jpg"];
        byte[] bad = how switch
        {
            "altered" => [.. good[..^1], 0x00],
            "longer" => [.. good, 0x00],
            _ => good[..^1],
        };
        server.Routes[CardPicture] = () => Ok(bad);
        Assert.Equal(PicturesOutcome.Refused, await Update());
        AssertSame(before, ReadBc());
        // No retry: it would get the same file.
        Assert.Single(server.Asked, CardPicture);
    }

    [Fact]
    public async Task RejectsMissingFile()
    {
        Serve(Pack);
        server.Routes.Remove(MiniPicture);
        Assert.Equal(PicturesOutcome.Refused, await Update());
        Assert.Empty(ReadBc());
        Assert.Single(server.Asked, MiniPicture);
    }

    [Fact]
    public async Task OfflineKeepsPictures()
    {
        Put("data/card/list_card.txt", Encoding.ASCII.GetBytes("an older list\r\n"));
        var before = ReadBc();
        server.Routes["/art/manifest.json"] = () => throw new HttpRequestException("no route to host");
        Assert.Equal(PicturesOutcome.Unreachable, await Update());
        AssertSame(before, ReadBc());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(null)]
    public async Task RetriesFailedFile(HttpStatusCode? status)
    {
        Serve(Pack);
        var answer = server.Routes[MiniPicture];
        int tries = 0;
        server.Routes[MiniPicture] = () => ++tries > 2 ? answer()
            : status is { } refusal ? new HttpResponseMessage(refusal) : throw new HttpRequestException("connection reset");
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        AssertSame(Pack, ReadBc());
        Assert.Equal(3, server.Asked.Count(path => path == MiniPicture));
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], pauses);
    }

    [Fact]
    public async Task ResumesAfterNetworkFailure()
    {
        Put("data/card/list_card.txt", Encoding.ASCII.GetBytes("an older list\r\n"));
        var before = ReadBc();
        Serve(Pack);
        var answer = server.Routes[MiniPicture];
        server.Routes[MiniPicture] = () => throw new HttpRequestException("connection reset");
        Assert.Equal(PicturesOutcome.Unreachable, await Update());
        AssertSame(before, ReadBc());
        // Retried twice, then gave up.
        Assert.Equal(3, server.Asked.Count(path => path == MiniPicture));

        // Finished files stay staged and aren't refetched.
        var kept = Directory.Exists(Staging) ? Directory.GetFiles(Staging, "*.*").Select(Path.GetFileName).ToList() : [];
        server.Routes[MiniPicture] = answer;
        server.Forget();
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        AssertSame(Pack, ReadBc());
        Assert.Contains(MiniPicture, server.Asked);
        Assert.All(kept, name => Assert.DoesNotContain("/art/files/" + name, server.Asked));
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public async Task TimeoutKeepsOldPictures()
    {
        Put("data/card/list_card.txt", Encoding.ASCII.GetBytes("an older list\r\n"));
        var before = ReadBc();
        Serve(Pack);
        using var blocked = new SemaphoreSlim(0);
        server.Waits[CardPicture] = async cancel =>
        {
            blocked.Release();
            await Task.Delay(Timeout.Infinite, cancel);
        };
        var update = Pictures().UpdateAsync(Bc);

        Assert.True(await blocked.WaitAsync(TimeSpan.FromSeconds(10)));
        string[] others = [Stored("data/card/list_card.txt", Pack["data/card/list_card.txt"]), Stored("data/mini/bc1441.jpg", Pack["data/mini/bc1441.jpg"])];
        await WaitUntil(() => others.All(name => File.Exists(Path.Combine(Staging, name))));
        clock.Advance(TimeSpan.FromSeconds(60));
        await WaitUntil(() => clock.NextDue(TimeSpan.FromSeconds(5)) is not null);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.NextDue(TimeSpan.FromSeconds(5)));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await blocked.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(update.IsCompleted);
        clock.Advance(CardPictures.TimeLimit - TimeSpan.FromSeconds(61));

        Assert.Equal(PicturesOutcome.Unfinished, await update);
        AssertSame(before, ReadBc());
        Assert.All(others, name => Assert.True(File.Exists(Path.Combine(Staging, name)), name));

        server.Waits.Remove(CardPicture);
        server.Forget();
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        AssertSame(Pack, ReadBc());
        Assert.Equal(["/art/manifest.json", CardPicture], server.Asked);
    }

    [Fact]
    public async Task WaitsAMinuteForServer()
    {
        Put("data/card/list_card.txt", Encoding.ASCII.GetBytes("an older list\r\n"));
        var before = ReadBc();
        Serve(Pack);
        using var blocked = new SemaphoreSlim(0);
        server.Waits["/art/manifest.json"] = async cancel =>
        {
            blocked.Release();
            await Task.Delay(Timeout.Infinite, cancel);
        };
        var update = Pictures().UpdateAsync(Bc);
        Assert.True(await blocked.WaitAsync(TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50);
        Assert.False(update.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PicturesOutcome.Unreachable, await update);
        AssertSame(before, ReadBc());
        Assert.Equal(["/art/manifest.json"], server.Asked);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ServerErrorKeepsPictures(HttpStatusCode status)
    {
        Put("data/card/list_card.txt", Encoding.ASCII.GetBytes("an older list\r\n"));
        var before = ReadBc();
        server.Routes["/art/manifest.json"] = () => new HttpResponseMessage(status) { Content = new StringContent("<html>busy</html>") };
        Assert.Equal(PicturesOutcome.Unreachable, await Update());
        AssertSame(before, ReadBc());
    }

    [Fact]
    public async Task NotFoundEmptiesBc()
    {
        Put("data/card/list_card.txt", Encoding.ASCII.GetBytes("an older list\r\n"));
        Put("data/mini/bc1441.jpg", [1, 2, 3]);
        Directory.CreateDirectory(Staging);
        File.WriteAllBytes(Path.Combine(Staging, "left.jpg"), [1]);
        // The manifest is a 404: no pictures.
        Assert.Equal(PicturesOutcome.None, await Update());
        Assert.Empty(ReadBc());
        Assert.False(Directory.Exists(Staging));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KeepsTitleNames(bool pictures)
    {
        Put(TitleNames.FileName, [1, 2, 3]);
        Put("data/card/bc9999.jpg", [1]);
        if (pictures)
            Serve(Pack);
        Assert.Equal(pictures ? PicturesOutcome.UpToDate : PicturesOutcome.None, await Update());
        var expected = pictures ? new Dictionary<string, byte[]>(Pack) : [];
        expected[TitleNames.FileName] = [1, 2, 3];
        AssertSame(expected, ReadBc());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KeepsCardNames(bool pictures)
    {
        foreach (var entry in CardNames.Entries)
            Put(entry, [1, 2, 3]);
        Put("data/card/bc9999.jpg", [1]);
        if (pictures)
            Serve(Pack);
        Assert.Equal(pictures ? PicturesOutcome.UpToDate : PicturesOutcome.None, await Update());
        var expected = pictures ? new Dictionary<string, byte[]>(Pack) : [];
        foreach (var entry in CardNames.Entries)
            expected[entry.Replace('\\', '/')] = [1, 2, 3];
        AssertSame(expected, ReadBc());
    }

    // Only a 404 says the server has no pictures.
    [Theory]
    [InlineData("<html><body>Sign in to the Wi-Fi</body></html>")]
    [InlineData("""{"format": 1, "client": "Ver.060110.00", "files": [{"path": "data/card/list_card.txt", "sha256": "00""")]
    [InlineData("""{"ok": true}""")]
    [InlineData("""{"format": 2, "files": []}""")]
    public async Task BadReplyKeepsPictures(string reply)
    {
        Put("data/card/list_card.txt", Encoding.ASCII.GetBytes("an older list\r\n"));
        var before = ReadBc();
        server.Routes["/art/manifest.json"] = () => Ok(Encoding.UTF8.GetBytes(reply), "text/html");
        Assert.Equal(PicturesOutcome.Refused, await Update());
        AssertSame(before, ReadBc());
    }

    [Fact]
    public async Task InvalidManifestKeepsPictures()
    {
        Put("data/card/list_card.txt", Encoding.ASCII.GetBytes("an older list\r\n"));
        var before = ReadBc();
        string manifest = JsonSerializer.Serialize(new
        {
            format = 1,
            files = new[] { new { path = "../../online_pc.exe", sha256 = Sha([1]), size = 1 } },
        });
        server.Routes["/art/manifest.json"] = () => Ok(Encoding.UTF8.GetBytes(manifest), "application/json");
        Assert.Equal(PicturesOutcome.Refused, await Update());
        AssertSame(before, ReadBc());
        Assert.Equal(["/art/manifest.json"], server.Asked);
    }

    // The launcher's HttpClient buffers 1 MB at most.
    [Fact]
    public async Task FetchesLargePicture()
    {
        var big = new Dictionary<string, byte[]> { ["data/card/bc2000.jpg"] = new byte[3 * 1024 * 1024 / 2] };
        Random.Shared.NextBytes(big["data/card/bc2000.jpg"]);
        Serve(big);
        var http = new HttpClient(server) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1 << 20 };
        Assert.Equal(PicturesOutcome.UpToDate, await new CardPictures(http, Api).UpdateAsync(Bc));
        AssertSame(big, ReadBc());
    }

    [Fact]
    public async Task RejectsLargeManifest()
    {
        server.Routes["/art/manifest.json"] = () => Ok(new byte[2 * 1024 * 1024 + 1], "application/json");
        Assert.Equal(PicturesOutcome.Refused, await Update());
    }

    [Fact]
    public async Task EmptyManifestEmptiesBc()
    {
        Put("data/card/list_card.txt", Encoding.ASCII.GetBytes("an older list\r\n"));
        Serve(new());
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        Assert.Empty(ReadBc());
    }

    // The launcher makes no links.
    [Fact]
    public async Task RemovesLinkButNotTarget()
    {
        Directory.CreateDirectory(Outside);
        File.WriteAllBytes(Path.Combine(Outside, "keep.jpg"), [7]);
        // Even with the listed file inside.
        File.WriteAllBytes(Path.Combine(Outside, "bc1441.jpg"), Pack["data/card/bc1441.jpg"]);
        Directory.CreateDirectory(Path.Combine(Bc, "data"));
        Link("/J", Path.Combine(Bc, "data", "card"), Outside);
        Serve(Pack);
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        Assert.False(new DirectoryInfo(Path.Combine(Bc, "data", "card")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        AssertSame(Pack, ReadBc());
        Assert.Equal(["bc1441.jpg", "keep.jpg"], Directory.GetFiles(Outside).Select(Path.GetFileName).Order());
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(Outside, "keep.jpg")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovesBcLink(bool pictures)
    {
        Directory.CreateDirectory(Path.Combine(Outside, "data", "card"));
        File.WriteAllBytes(Path.Combine(Outside, "data", "card", "keep.jpg"), [7]);
        Directory.CreateDirectory(root);
        Link("/J", Bc, Outside);
        if (pictures)
            Serve(Pack);
        Assert.Equal(pictures ? PicturesOutcome.UpToDate : PicturesOutcome.None, await Update());
        Assert.False(Directory.Exists(Bc) && new DirectoryInfo(Bc).Attributes.HasFlag(FileAttributes.ReparsePoint));
        AssertSame(pictures ? Pack : new Dictionary<string, byte[]>(), ReadBc());
        Assert.Equal([Path.Combine(Outside, "data", "card", "keep.jpg")], Directory.GetFiles(Outside, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void PruneDoesNotFollowLinks()
    {
        Directory.CreateDirectory(Outside);
        File.WriteAllBytes(Path.Combine(Outside, "keep.jpg"), [7]);
        Directory.CreateDirectory(Path.Combine(Bc, "data"));
        Link("/J", Path.Combine(Bc, "data", "card"), Outside);
        CardPictures.Prune(new DirectoryInfo(Bc), []);
        Assert.Empty(Directory.GetFileSystemEntries(Bc));
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(Outside, "keep.jpg")));
    }

    [Fact]
    public async Task ReplacesHardLink()
    {
        string document = Path.Combine(Outside, "document.txt");
        Directory.CreateDirectory(Outside);
        File.WriteAllText(document, "the player's own document");
        Directory.CreateDirectory(Path.Combine(Bc, "data", "card"));
        Link("/H", Path.Combine(Bc, "data", "card", "list_card.txt"), document);
        Serve(Pack);
        Assert.Equal(PicturesOutcome.UpToDate, await Update());
        AssertSame(Pack, ReadBc());
        Assert.Equal("the player's own document", File.ReadAllText(document));
    }
}
