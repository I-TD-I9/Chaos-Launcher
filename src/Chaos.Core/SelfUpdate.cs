using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Chaos;

/// <summary>Result of a launcher update.</summary>
public enum UpdateOutcome
{
    Restarting,
    NotOffered,
    GameRunning,
    Unwritable,
    Unreachable,
    Refused,
    NotStarted,
}

/// <summary>Download progress in bytes.</summary>
public readonly record struct UpdateProgress(long Done, long Total);

/// <summary>Updates the launcher to a newer release.</summary>
public sealed class SelfUpdate
{
    public const string UpdatedArgument = "--updated";

    public static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    // Give up after this long without data.
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    readonly HttpClient http;
    readonly Func<bool> gameRunning;
    readonly Func<ProcessStartInfo, Process?> start;

    public string ExePath { get; }

    string Download => ExePath + ".download";
    string New => ExePath + ".new";
    string Old => ExePath + ".old";

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

    public static SelfUpdate? Create(HttpClient http) =>
        Environment.ProcessPath is { } path && IsLauncher(path) ? new SelfUpdate(http, path) : null;

    internal static bool IsLauncher(string path) =>
        string.Equals(Path.GetFileName(path), Releases.ExeName, StringComparison.OrdinalIgnoreCase);

    public static bool Offers(LauncherRelease? latest, Version current, bool gameRunning) =>
        latest is not null && latest.Version > current && !gameRunning;

    public static bool IsGameRunning()
    {
        var found = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Game.GameStarter.ClientName));
        foreach (var process in found)
            process.Dispose();
        return found.Length > 0;
    }

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

    public async Task<UpdateOutcome> UpdateAsync(LauncherRelease release, IProgress<UpdateProgress>? progress = null,
                                                 CancellationToken cancel = default)
    {
        if (await DownloadAsync(release, progress, cancel) is { } failed)
            return failed;
        // The game may have started during the download.
        if (gameRunning())
        {
            LauncherFiles.TryDelete(New);
            return UpdateOutcome.GameRunning;
        }
        if (!Swap())
            return UpdateOutcome.Unwritable;
        return Restart() ? UpdateOutcome.Restarting : UpdateOutcome.NotStarted;
    }

    internal async Task<UpdateOutcome?> DownloadAsync(LauncherRelease release, IProgress<UpdateProgress>? progress,
                                                     CancellationToken cancel)
    {
        if (release.Exe is not { } exe || release.Checksum is not { } checksum
            || exe.Size > Releases.MaxExeSize || checksum.Size > Releases.MaxChecksumSize)
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
        catch (Exception e) when (IsNetworkError(e, cancel))
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

    // Downloads to .download and checks size and hash.
    async Task<bool> FetchAsync(ReleaseAsset asset, string expected, IProgress<UpdateProgress>? progress,
                                CancellationToken cancel)
    {
        string? hash;
        await using (var output = new FileStream(Download, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            hash = await GetAsync(asset, output, progress, cancel);
            output.Flush(flushToDisk: true);
        }
        return hash is not null && hash == expected && (asset.Sha256 is null || asset.Sha256 == hash);
    }

    // HTTPS only, capped at the asset's size.
    async Task<string?> GetAsync(ReleaseAsset asset, Stream output, IProgress<UpdateProgress>? progress,
                                 CancellationToken cancel)
    {
        if (asset.Url.Scheme != Uri.UriSchemeHttps)
            return null;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallTimeout);
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        // GitHub redirects the download.
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub returned {(int)response.StatusCode}", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(stall.Token);
        var buffer = new byte[81920];
        long size = 0;
        progress?.Report(new UpdateProgress(0, asset.Size));
        while (true)
        {
            stall.CancelAfter(StallTimeout);
            int read;
            try
            {
                read = await input.ReadAsync(buffer, stall.Token);
            }
            catch (IOException e) when (e is not HttpIOException)
            {
                // Connection dropped mid-read.
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
        // Antivirus may lock a new exe briefly while scanning.
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

    // Put the old exe back.
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
                // Leave the new exe, it's already checked.
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

    public static int? UpdatedFrom(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
            if (args[i] == UpdatedArgument
                && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0)
                return pid;
        return null;
    }

    public async Task<string> FinishAsync(int oldPid, CancellationToken cancel = default)
    {
        await WaitForExitAsync(oldPid, ExitTimeout, cancel);
        // The old exe can stay locked briefly after it exits.
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

    static async Task WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken cancel)
    {
        if (pid == Environment.ProcessId)
            return;
        try
        {
            using var process = Process.GetProcessById(pid);
            using var time = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            time.CancelAfter(timeout);
            await process.WaitForExitAsync(time.Token);
        }
        catch (ArgumentException)
        {
            // Exited.
        }
        catch (InvalidOperationException)
        {
            // Exited while we looked.
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            // Timed out, clean up what we can.
        }
    }

    static bool IsNetworkError(Exception e, CancellationToken cancel) =>
        e is HttpRequestException or HttpIOException or OperationCanceledException && !cancel.IsCancellationRequested;
}
