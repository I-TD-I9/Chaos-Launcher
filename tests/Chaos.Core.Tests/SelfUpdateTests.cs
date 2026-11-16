using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Chaos.Tests;

public sealed class SelfUpdateTests : IDisposable
{
    const string Base = "https://github.com/I-TD-I9/Chaos-Launcher/releases/download/v9.9.9/";

    readonly string folder = Directory.CreateTempSubdirectory("bc-update-").FullName;
    readonly FakeServer server = new();
    readonly HttpClient http;
    bool gameRunning;
    readonly List<ProcessStartInfo> started = [];
    bool starts = true;

    static readonly byte[] OldExe = "MZ the launcher as it is"u8.ToArray();
    static readonly byte[] NewExe = [.. "MZ the launcher to come "u8, .. Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31))];

    public SelfUpdateTests()
    {
        http = new HttpClient(server);
        File.WriteAllBytes(Exe, OldExe);
        Serve(NewExe, $"{Sha(NewExe)}  ChaosLauncher.exe\n");
    }

    public void Dispose()
    {
        http.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    string Exe => Path.Combine(folder, "ChaosLauncher.exe");

    SelfUpdate Update() => new(http, Exe, () => gameRunning, info =>
    {
        started.Add(info);
        return starts ? new Process() : null;
    });

    sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(Routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var answer)
                ? answer()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    void Serve(byte[] exe, string checksum)
    {
        server.Routes[Base + "ChaosLauncher.exe"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(exe) };
        server.Routes[Base + "ChaosLauncher.exe.sha256"] = () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes(checksum)) };
    }

    static LauncherRelease Release(long? exeSize = null, string? exeDigest = null, bool checksum = true, string? checksumDigest = null) =>
        new(new Version(9, 9, 9), new Uri("https://github.com/I-TD-I9/Chaos-Launcher/releases/tag/v9.9.9"),
            new ReleaseAsset("ChaosLauncher.exe", new Uri(Base + "ChaosLauncher.exe"), exeSize ?? NewExe.Length, exeDigest),
            checksum
                ? new ReleaseAsset("ChaosLauncher.exe.sha256", new Uri(Base + "ChaosLauncher.exe.sha256"),
                                   $"{Sha(NewExe)}  ChaosLauncher.exe\n".Length, checksumDigest)
                : null);

    // The launcher's folder holds the launcher and nothing else.
    void AssertUntouched()
    {
        Assert.Equal(OldExe, File.ReadAllBytes(Exe));
        Assert.Equal([Exe], Directory.GetFiles(folder));
    }

    [Fact]
    public async Task A_checked_download_is_swapped_in_and_started()
    {
        var progress = new List<UpdateProgress>();
        var outcome = await Update().UpdateAsync(Release(exeDigest: Sha(NewExe)), new Recorder(progress));
        Assert.Equal(UpdateOutcome.Restarting, outcome);
        Assert.Equal(NewExe, File.ReadAllBytes(Exe));
        Assert.Equal(OldExe, File.ReadAllBytes(Exe + ".old"));
        Assert.False(File.Exists(Exe + ".download"));
        Assert.False(File.Exists(Exe + ".new"));
        var start = Assert.Single(started);
        Assert.Equal(Exe, start.FileName);
        Assert.Equal(["--updated", Environment.ProcessId.ToString()], start.ArgumentList.ToArray());
        Assert.Equal(new UpdateProgress(NewExe.Length, NewExe.Length), progress[^1]);
        Assert.All(progress, p => Assert.Equal(NewExe.Length, p.Total));
    }

    sealed class Recorder(List<UpdateProgress> seen) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => seen.Add(value);
    }

    [Fact]
    public async Task A_download_of_the_wrong_size_leaves_nothing_behind()
    {
        Assert.Equal(UpdateOutcome.Refused, await Update().UpdateAsync(Release(exeSize: NewExe.Length - 1)));
        AssertUntouched();
        Assert.Equal(UpdateOutcome.Refused, await Update().UpdateAsync(Release(exeSize: NewExe.Length + 1)));
        AssertUntouched();
    }

    [Fact]
    public async Task A_download_cut_short_leaves_nothing_behind()
    {
        // No length said, and the body ends early.
        server.Routes[Base + "ChaosLauncher.exe"] = () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(NewExe[..1000])) };
        Assert.Equal(UpdateOutcome.Refused, await Update().UpdateAsync(Release()));
        AssertUntouched();
        Assert.Empty(started);
    }

    [Fact]
    public async Task A_hash_that_does_not_match_the_checksum_file_leaves_nothing_behind()
    {
        Serve(NewExe, $"{Sha(OldExe)}  ChaosLauncher.exe\n");
        Assert.Equal(UpdateOutcome.Refused, await Update().UpdateAsync(Release()));
        AssertUntouched();
    }

    [Fact]
    public async Task A_checksum_file_without_a_hash_is_refused()
    {
        Serve(NewExe, "not a hash, and long enough to stand in for one: 0123456789abcdef0123\n");
        Assert.Equal(UpdateOutcome.Refused, await Update().UpdateAsync(Release()));
        AssertUntouched();
    }

    [Fact]
    public async Task A_hash_that_does_not_match_the_digest_leaves_nothing_behind()
    {
        Assert.Equal(UpdateOutcome.Refused, await Update().UpdateAsync(Release(exeDigest: Sha(OldExe))));
        AssertUntouched();
        Assert.Equal(UpdateOutcome.Refused, await Update().UpdateAsync(Release(checksumDigest: Sha(OldExe))));
        AssertUntouched();
    }

    [Fact]
    public async Task Without_the_checksum_there_is_no_update()
    {
        Assert.Equal(UpdateOutcome.NotOffered, await Update().UpdateAsync(Release(checksum: false)));
        AssertUntouched();
    }

    [Fact]
    public async Task Nothing_is_downloaded_while_the_game_runs()
    {
        gameRunning = true;
        Assert.Equal(UpdateOutcome.GameRunning, await Update().UpdateAsync(Release()));
        AssertUntouched();
    }

    [Fact]
    public async Task GitHub_out_of_reach_leaves_nothing_behind()
    {
        server.Routes.Clear();
        Assert.Equal(UpdateOutcome.Unreachable, await Update().UpdateAsync(Release()));
        AssertUntouched();
    }

    [Fact]
    public async Task A_folder_that_cannot_be_written_gets_no_download()
    {
        var update = new SelfUpdate(http, Path.Combine(folder, "gone", "ChaosLauncher.exe"), () => false, _ => null);
        Assert.False(update.CanWrite());
        Assert.Equal(UpdateOutcome.Unwritable, await update.UpdateAsync(Release()));
        Assert.True(SelfUpdate.CanWrite(folder));
        AssertUntouched();
    }

    [Fact]
    public async Task Stale_files_from_an_earlier_try_are_cleared_first()
    {
        File.WriteAllBytes(Exe + ".download", [1]);
        File.WriteAllBytes(Exe + ".new", [2]);
        Assert.Equal(UpdateOutcome.Refused, await Update().UpdateAsync(Release(exeSize: 5)));
        AssertUntouched();
    }

    [Fact]
    public void The_swap_renames_the_exe_then_the_download()
    {
        File.WriteAllBytes(Exe + ".new", NewExe);
        Assert.True(Update().Swap());
        Assert.Equal(NewExe, File.ReadAllBytes(Exe));
        Assert.Equal(OldExe, File.ReadAllBytes(Exe + ".old"));
        Assert.False(File.Exists(Exe + ".new"));
    }

    [Fact]
    public void A_swap_that_fails_halfway_puts_the_old_exe_back()
    {
        File.WriteAllBytes(Exe + ".new", NewExe);
        // Held open without leave to move it, so the second rename fails after the first.
        using (new FileStream(Exe + ".new", FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(Update().Swap());
        Assert.Equal(OldExe, File.ReadAllBytes(Exe));
        Assert.False(File.Exists(Exe + ".old"));
    }

    [Fact]
    public void When_the_old_exe_cannot_go_back_the_new_one_stays_under_the_name()
    {
        File.WriteAllBytes(Exe + ".new", NewExe);
        starts = false;
        var update = Update();
        Assert.True(update.Swap());
        // Held open without leave to move it, so it can't be put back.
        using (new FileStream(Exe + ".old", FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(update.Restart());
        Assert.Equal(NewExe, File.ReadAllBytes(Exe));
        Assert.Equal(OldExe, File.ReadAllBytes(Exe + ".old"));
    }

    [Theory]
    [InlineData(@"C:\Games\Chaos\ChaosLauncher.exe", true)]
    [InlineData(@"C:\Games\Chaos\chaoslauncher.EXE", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)]
    public void Only_the_launcher_s_own_exe_is_ever_swapped(string path, bool launcher) =>
        Assert.Equal(launcher, SelfUpdate.IsLauncher(path));

    [Fact]
    public async Task A_new_launcher_that_will_not_start_is_swapped_back_out()
    {
        starts = false;
        Assert.Equal(UpdateOutcome.NotStarted, await Update().UpdateAsync(Release()));
        Assert.Single(started);
        AssertUntouched();
    }

    [Fact]
    public async Task After_the_update_the_new_launcher_tidies_up()
    {
        File.WriteAllBytes(Exe + ".old", OldExe);
        File.WriteAllBytes(Exe + ".download", [1]);
        File.WriteAllBytes(Exe + ".new", [2]);
        // A process that has ended already: no process has the id int.MaxValue.
        string said = await Update().FinishAsync(int.MaxValue);
        Assert.Equal($"Updated to {Releases.Current}.", said);
        Assert.Equal([Exe], Directory.GetFiles(folder));
    }

    [Fact]
    public async Task The_new_launcher_waits_for_the_old_one_to_end()
    {
        File.WriteAllBytes(Exe + ".old", OldExe);
        using var old = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        await Update().FinishAsync(old.Id);
        Assert.True(old.HasExited);
        Assert.False(File.Exists(Exe + ".old"));
    }

    [Theory]
    [InlineData(new[] { "--updated", "1234" }, 1234)]
    [InlineData(new[] { "--other", "--updated", "77" }, 77)]
    [InlineData(new[] { "--updated" }, null)]
    [InlineData(new[] { "--updated", "-5" }, null)]
    [InlineData(new[] { "--updated", "soon" }, null)]
    [InlineData(new string[0], null)]
    public void The_old_launcher_is_named_by_its_process_id(string[] args, int? pid) =>
        Assert.Equal(pid, SelfUpdate.UpdatedFrom(args));

    [Fact]
    public void Only_a_newer_release_is_offered_and_never_while_the_game_runs()
    {
        var release = Release();
        Assert.True(SelfUpdate.Offers(release, new Version(9, 9, 8), gameRunning: false));
        Assert.False(SelfUpdate.Offers(release, new Version(9, 9, 9), gameRunning: false));
        Assert.False(SelfUpdate.Offers(release, new Version(10, 0, 0), gameRunning: false));
        Assert.False(SelfUpdate.Offers(release, new Version(9, 9, 8), gameRunning: true));
        Assert.False(SelfUpdate.Offers(null, new Version(9, 9, 8), gameRunning: false));
    }
}
