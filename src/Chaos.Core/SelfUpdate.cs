using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Chaos;

/// <summary>How an update of the launcher went. Anything but Restarting leaves the launcher as it was.</summary>
public enum UpdateOutcome
{
    /// <summary>The new launcher is in place and has been started: the caller shuts down now.</summary>
    Restarting,

    /// <summary>The release carries no launcher to update to: the player is sent to its page instead.</summary>
    NotOffered,

    /// <summary>The game is running, and the update waits until it has closed.</summary>
    GameRunning,

    /// <summary>
    /// Windows won't let the launcher write in its own folder (Program Files, say), or the swap
    /// failed: nothing was replaced, and the player is sent to the release page instead.
    /// </summary>
    Unwritable,

    /// <summary>GitHub could not be reached, or did not answer in time.</summary>
    Unreachable,

    /// <summary>What came was not the file the release describes: its size or its SHA-256 was wrong.</summary>
    Refused,

    /// <summary>The new launcher would not start, so the old one was put back.</summary>
    NotStarted,
}

/// <summary>How far a download has got, in bytes.</summary>
public readonly record struct UpdateProgress(long Done, long Total);

/// <summary>
/// The launcher updating itself to a newer release: it downloads the new ChaosLauncher.exe beside
/// itself, checks it, swaps it in for itself and starts it, and the new one, started with
/// <c>--updated &lt;pid&gt;</c>, tidies up after the old one once it has gone.
/// </summary>
/// <remarks>
/// <para>
/// Windows lets the file of a running program be renamed, though not deleted or written over, so
/// the swap is two renames in the launcher's own folder: the running exe to .old, the checked
/// download, .new, to the exe's name. The download goes to .download in that same folder, so
/// every move stays on one volume, and only becomes .new once its size and SHA-256 have checked
/// out: against the asset's size, the release's .sha256 file and, when GitHub gives one, the
/// asset's digest. These catch a broken or cut-short download; they do not prove who made the
/// file, which, as with a download by hand, rests on HTTPS to this project's own releases.
/// </para>
/// <para>
/// The settings folder is never touched. A file HttpClient writes carries no mark of the web, so
/// Windows won't warn about the new exe as downloaded from the internet.
/// </para>
/// </remarks>
public sealed class SelfUpdate
{
    /// <summary>The argument the new launcher is started with, followed by the old one's process id.</summary>
    public const string UpdatedArgument = "--updated";

    /// <summary>How long a new launcher waits for the old one to end before it tidies up anyway.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // A download that gets no bytes for this long has stalled.
    static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    readonly HttpClient http;
    readonly Func<bool> gameRunning;
    readonly Func<ProcessStartInfo, Process?> start;

    /// <summary>The launcher to update, ChaosLauncher.exe in the folder it runs from.</summary>
    public string ExePath { get; }

    string Download => ExePath + ".download";
    string New => ExePath + ".new";
    string Old => ExePath + ".old";

    /// <summary>Updates the launcher at <paramref name="exePath"/>.</summary>
    public SelfUpdate(HttpClient http, string exePath) : this(http, exePath, IsGameRunning, Process.Start)
    {
    }

    internal SelfUpdate(HttpClient http, string exePath, Func<bool> gameRunning, Func<ProcessStartInfo, Process?> start)
    {
        this.http = http;
        ExePath = Path.GetFullPath(exePath);
        this.gameRunning = gameRunning;
        this.start = start;
    }

    /// <summary>
    /// The update of the launcher that is running, or null when it isn't running as
    /// ChaosLauncher.exe: when Windows can't say where its exe is, or when it was started through
    /// dotnet.exe, as a build is, which must never be swapped out.
    /// </summary>
    public static SelfUpdate? ForThisLauncher(HttpClient http) =>
        Environment.ProcessPath is { } path && IsLauncher(path) ? new SelfUpdate(http, path) : null;

    internal static bool IsLauncher(string path) =>
        string.Equals(Path.GetFileName(path), Releases.ExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether to offer <paramref name="latest"/> to a launcher of version
    /// <paramref name="current"/>: only a newer release, and never while the game runs.
    /// </summary>
    public static bool Offers(LauncherRelease? latest, Version current, bool gameRunning) =>
        latest is not null && latest.Version > current && !gameRunning;

    /// <summary>
    /// Whether the game is running: any process of the client's name, which the launcher starts
    /// it under (GameStarter.ClientName).
    /// </summary>
    public static bool IsGameRunning()
    {
        var found = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Game.GameStarter.ClientName));
        foreach (var process in found)
            process.Dispose();
        return found.Length > 0;
    }

    /// <summary>
    /// Whether the launcher may write in its own folder, found out by making a file there and
    /// deleting it. When it may not, there is no download, and the caller offers the release page.
    /// </summary>
    public bool CanWrite() => CanWrite(Path.GetDirectoryName(ExePath)!);

    internal static bool CanWrite(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, $".chaos-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return !File.Exists(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Updates the launcher to <paramref name="release"/>: downloads and checks the new exe,
    /// swaps it in for this one and starts it. On <see cref="UpdateOutcome.Restarting"/> the
    /// caller shuts down at once; on anything else nothing was replaced, and the caller says what
    /// went wrong and offers the release page. <paramref name="progress"/> hears the bytes of
    /// the exe as they come. Calling it off deletes what was downloaded and throws an
    /// OperationCanceledException.
    /// </summary>
    public async Task<UpdateOutcome> UpdateAsync(LauncherRelease release, IProgress<UpdateProgress>? progress = null,
                                                 CancellationToken cancel = default)
    {
        if (await DownloadAsync(release, progress, cancel) is { } failed)
            return failed;
        // The game may have been started while the file came.
        if (gameRunning())
        {
            LauncherFiles.TryDelete(New);
            return UpdateOutcome.GameRunning;
        }
        if (!Swap())
            return UpdateOutcome.Unwritable;
        return Restart() ? UpdateOutcome.Restarting : UpdateOutcome.NotStarted;
    }

    /// <summary>
    /// The new exe, downloaded and checked, as .new beside this one: null once it is there, or
    /// why it isn't, with no .download or .new left behind.
    /// </summary>
    internal async Task<UpdateOutcome?> DownloadAsync(LauncherRelease release, IProgress<UpdateProgress>? progress,
                                                     CancellationToken cancel)
    {
        if (release.Exe is not { } exe || release.Checksum is not { } checksum
            || exe.Size > Releases.LargestExe || checksum.Size > Releases.LargestChecksum)
            return UpdateOutcome.NotOffered;
        if (gameRunning())
            return UpdateOutcome.GameRunning;
        if (!CanWrite())
            return UpdateOutcome.Unwritable;
        LauncherFiles.TryDelete(Download);
        LauncherFiles.TryDelete(New);
        try
        {
            if (await ChecksumAsync(checksum, cancel) is not { } expected)
                return UpdateOutcome.Refused;
            if (!await FetchAsync(exe, expected, progress, cancel))
            {
                LauncherFiles.TryDelete(Download);
                return UpdateOutcome.Refused;
            }
            File.Move(Download, New, overwrite: true);
            return null;
        }
        catch (Exception e) when (Unanswered(e, cancel))
        {
            LauncherFiles.TryDelete(Download);
            return UpdateOutcome.Unreachable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LauncherFiles.TryDelete(Download);
            LauncherFiles.TryDelete(New);
            return UpdateOutcome.Unwritable;
        }
        catch (OperationCanceledException)
        {
            LauncherFiles.TryDelete(Download);
            throw;
        }
    }

    // The SHA-256 the release's .sha256 file gives, its first word, or null when the file is not
    // what the release says or holds no hash.
    async Task<string?> ChecksumAsync(ReleaseAsset asset, CancellationToken cancel)
    {
        using var body = new MemoryStream();
        var hash = await GetAsync(asset, body, null, cancel);
        if (hash is null || (asset.Sha256 is { } digest && digest != hash))
            return null;
        string text = Encoding.ASCII.GetString(body.ToArray());
        string first = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Releases.Hex(first);
    }

    // The exe into .download, its size and hash checked as it comes: whether it is the one the
    // release describes.
    async Task<bool> FetchAsync(ReleaseAsset asset, string expected, IProgress<UpdateProgress>? progress,
                                         CancellationToken cancel)
    {
        string? hash;
        await using (var output = new FileStream(Download, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            hash = await GetAsync(asset, output, progress, cancel);
            // On disk before the renames, so a power cut after the swap can't leave an empty exe.
            output.Flush(flushToDisk: true);
        }
        return hash is not null && hash == expected && (asset.Sha256 is null || asset.Sha256 == hash);
    }

    // The asset into output, over HTTPS only, held to its size: its SHA-256, or null when the
    // reply was refused or the size was wrong. A reply that is no answer at all throws.
    async Task<string?> GetAsync(ReleaseAsset asset, Stream output, IProgress<UpdateProgress>? progress,
                                 CancellationToken cancel)
    {
        if (asset.Url.Scheme != Uri.UriSchemeHttps)
            return null;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(Stall);
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        // GitHub sends the file from elsewhere; HttpClient follows no HTTPS-to-HTTP redirect, and
        // this holds to that whatever handler it was given.
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(stall.Token);
        var buffer = new byte[81920];
        long size = 0;
        progress?.Report(new UpdateProgress(0, asset.Size));
        while (true)
        {
            stall.CancelAfter(Stall);
            int read;
            try
            {
                read = await input.ReadAsync(buffer, stall.Token);
            }
            catch (IOException e) when (e is not HttpIOException)
            {
                // The connection broke under the read: that is GitHub out of reach, not a file
                // the launcher can't write.
                throw new HttpIOException(HttpRequestError.ResponseEnded, e.Message, e);
            }
            if (read == 0)
                break;
            if (size + read > asset.Size)
                return null;
            size += read;
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            progress?.Report(new UpdateProgress(size, asset.Size));
        }
        return size == asset.Size ? Convert.ToHexStringLower(hash.GetHashAndReset()) : null;
    }

    /// <summary>
    /// Swaps the checked .new in for the running exe: the exe to .old, then .new to the exe's
    /// name, and when that second step fails, .old back. False when nothing was replaced, and
    /// then .new is deleted where it can be.
    /// </summary>
    internal bool Swap()
    {
        if (!File.Exists(New))
            return false;
        try
        {
            File.Delete(Old);
            File.Move(ExePath, Old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LauncherFiles.TryDelete(New);
            return false;
        }
        // Antivirus often holds a freshly written exe for a moment while it scans it.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(New, ExePath);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                    break;
                Thread.Sleep(200);
            }
        }
        PutBack();
        return false;
    }

    // The old exe back under its name, whatever is there now going first. The exe's name must
    // always hold a launcher that works: .new is only deleted once .old is back, and when .old
    // won't move, the checked .new goes under the exe's name instead, or both are left as they are.
    void PutBack()
    {
        if (File.Exists(ExePath))
        {
            try
            {
                File.Move(ExePath, New, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The new exe stays in place, and it is a checked one.
                return;
            }
        }
        if (TryMove(Old, ExePath))
            LauncherFiles.TryDelete(New);
        else
            TryMove(New, ExePath);
    }

    static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the swapped-in exe with <c>--updated &lt;this process's id&gt;</c>. When it won't
    /// start, the old exe goes back, and the launcher carries on as it was.
    /// </summary>
    internal bool Restart()
    {
        try
        {
            using var process = start(StartInfo(ExePath, Environment.ProcessId));
            if (process is not null)
                return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
        PutBack();
        return false;
    }

    internal static ProcessStartInfo StartInfo(string exe, int pid)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        info.ArgumentList.Add(UpdatedArgument);
        info.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        return info;
    }

    /// <summary>
    /// The process id the launcher was started after an update with (<c>--updated &lt;pid&gt;</c>
    /// among <paramref name="args"/>), or null when it was started any other way.
    /// </summary>
    public static int? UpdatedFrom(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
            if (args[i] == UpdatedArgument
                && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0)
                return pid;
        return null;
    }

    /// <summary>
    /// What a launcher started with <c>--updated &lt;pid&gt;</c> does first: waits, no longer
    /// than <see cref="Patience"/>, for the old one to end, deletes the .old it left and any
    /// stale .download or .new, and gives the line to show, "Updated to &lt;version&gt;.".
    /// Never throws: a file it can't delete yet is left for the next update.
    /// </summary>
    public async Task<string> FinishAsync(int oldPid, CancellationToken cancel = default)
    {
        await WaitForExitAsync(oldPid, Patience, cancel);
        // Windows may hold the old exe's file a moment after its process has gone.
        for (int attempt = 0; attempt < 5 && File.Exists(Old); attempt++)
        {
            LauncherFiles.TryDelete(Old);
            if (File.Exists(Old))
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancel);
        }
        LauncherFiles.TryDelete(Download);
        LauncherFiles.TryDelete(New);
        return $"Updated to {Releases.Current}.";
    }

    static async Task WaitForExitAsync(int pid, TimeSpan patience, CancellationToken cancel)
    {
        if (pid == Environment.ProcessId)
            return;
        try
        {
            using var process = Process.GetProcessById(pid);
            using var time = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            time.CancelAfter(patience);
            await process.WaitForExitAsync(time.Token);
        }
        catch (ArgumentException)
        {
            // Gone already.
        }
        catch (InvalidOperationException)
        {
            // Gone while it was looked at.
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            // Still there after all this time: tidy up what can be tidied.
        }
    }

    // A request that got no answer, or none in time, or was cut off, rather than one the caller
    // called off. A failure to write the file is an IOException too, but never an HttpIOException.
    static bool Unanswered(Exception e, CancellationToken cancel) =>
        e is HttpRequestException or HttpIOException or OperationCanceledException && !cancel.IsCancellationRequested;
}
