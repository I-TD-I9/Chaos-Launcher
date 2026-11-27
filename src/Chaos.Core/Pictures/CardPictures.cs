using System.Net;
using System.Security.Cryptography;
using Chaos.Titles;

namespace Chaos.Pictures;

/// <summary>Result of a card picture update.</summary>
public enum PicturesOutcome
{
    None,
    UpToDate,
    Unfinished,
    Unreachable,
    Refused,
    Unwritable,
}

/// <summary>Picture update progress.</summary>
public readonly record struct PicturesProgress(int Done, int Total);

public sealed class CardPictures(HttpClient http, Uri api, TimeProvider? clock = null)
{
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(90);

    const int MaxParallel = 4;

    const int Retries = 2;

    // 6000 entries is under 1 MB.
    const int MaxManifestSize = 2 * 1024 * 1024;

    // HttpClient.Timeout only covers the headers.
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<PicturesOutcome> UpdateAsync(string folder, IProgress<PicturesProgress>? progress = null,
                                                   CancellationToken cancel = default)
    {
        using var deadline = new CancellationTokenSource(TimeLimit, clock);
        using var time = CancellationTokenSource.CreateLinkedTokenSource(cancel, deadline.Token);
        HttpStatusCode status;
        byte[]? body;
        try
        {
            (status, body) = await ManifestAsync(time.Token);
        }
        catch (Exception e) when (IsNetworkError(e, cancel))
        {
            return PicturesOutcome.Unreachable;
        }
        try
        {
            // 404: the server has no pictures.
            if (status == HttpStatusCode.NotFound)
                return await Task.Run(() => Clear(folder));
            if (status != HttpStatusCode.OK)
                return PicturesOutcome.Unreachable;
            if (body is null || ArtManifest.Read(body).Manifest is not { } manifest)
                return PicturesOutcome.Refused;

            progress?.Report(new PicturesProgress(0, 0));
            string staging = folder + ".partial";
            List<ArtFile> missing;
            try
            {
                missing = await Task.Run(() => FindMissing(folder, staging, manifest), time.Token);
                var fetched = await FetchAllAsync([.. missing.DistinctBy(file => file.StoredName)], staging, progress, time.Token);
                if (fetched != PicturesOutcome.UpToDate)
                    return fetched;
            }
            catch (Exception) when (deadline.IsCancellationRequested && !cancel.IsCancellationRequested)
            {
                return PicturesOutcome.Unfinished;
            }
            // Can't be cancelled once started.
            await Task.Run(() => Place(folder, staging, manifest, missing));
            return PicturesOutcome.UpToDate;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return PicturesOutcome.Unwritable;
        }
    }

    async Task<(HttpStatusCode Status, byte[]? Body)> ManifestAsync(CancellationToken cancel)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, clock);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
        using var response = await http.GetAsync(new Uri(api, "art/manifest.json"), HttpCompletionOption.ResponseHeadersRead, limit.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, null);
        // Read as bytes, ignoring the charset.
        await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, limit.Token)) > 0)
        {
            if (body.Length + read > MaxManifestSize)
                return (HttpStatusCode.OK, null);
            body.Write(buffer, 0, read);
        }
        return (HttpStatusCode.OK, body.ToArray());
    }

    static List<ArtFile> FindMissing(string folder, string staging, ArtManifest manifest)
    {
        RemoveLinks(folder);
        RemoveLinks(staging);
        return [.. manifest.Files.Where(file => !Matches(Local(folder, file), file))];
    }

    async Task<PicturesOutcome> FetchAllAsync(List<ArtFile> files, string staging, IProgress<PicturesProgress>? progress,
                                              CancellationToken cancel)
    {
        if (files.Count == 0)
            return PicturesOutcome.UpToDate;
        Directory.CreateDirectory(staging);
        progress?.Report(new PicturesProgress(0, files.Count));
        int done = 0;
        var outcome = PicturesOutcome.UpToDate;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        try
        {
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = stop.Token },
                async (file, token) =>
                {
                    string staged = Path.Combine(staging, file.StoredName);
                    var fetched = Matches(staged, file) ? PicturesOutcome.UpToDate : await FetchAsync(file, staged, token);
                    if (fetched != PicturesOutcome.UpToDate)
                    {
                        outcome = fetched;
                        await stop.CancelAsync();
                        return;
                    }
                    progress?.Report(new PicturesProgress(Interlocked.Increment(ref done), files.Count));
                });
        }
        catch (Exception) when (outcome != PicturesOutcome.UpToDate)
        {
            // Cancelled because another file failed.
        }
        return outcome;
    }

    async Task<PicturesOutcome> FetchAsync(ArtFile file, string staged, CancellationToken cancel)
    {
        for (int retry = 0; ; retry++)
        {
            PicturesOutcome fetched;
            try
            {
                fetched = await TryFetchAsync(file, staged, cancel);
            }
            catch (Exception e) when (IsNetworkError(e, cancel))
            {
                fetched = PicturesOutcome.Unreachable;
            }
            if (fetched != PicturesOutcome.Unreachable || retry == Retries)
                return fetched;
            await Task.Delay(TimeSpan.FromSeconds(retry + 1), clock, cancel);
        }
    }

    // Downloads from art/files/ and checks size and hash.
    async Task<PicturesOutcome> TryFetchAsync(ArtFile file, string staged, CancellationToken cancel)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, clock);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
        using var response = await http.GetAsync(new Uri(api, "art/files/" + file.StoredName), HttpCompletionOption.ResponseHeadersRead, limit.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return PicturesOutcome.Refused;
        if (response.StatusCode != HttpStatusCode.OK)
            return PicturesOutcome.Unreachable;
        string partial = staged + ".part";
        bool whole;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        await using (var input = await response.Content.ReadAsStreamAsync(limit.Token))
        await using (var output = File.Create(partial))
        {
            var buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, limit.Token)) > 0 && size + read <= file.Size)
            {
                size += read;
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), limit.Token);
            }
            whole = read == 0 && size == file.Size && Convert.ToHexStringLower(hash.GetHashAndReset()) == file.Sha256;
        }
        if (!whole)
        {
            File.Delete(partial);
            return PicturesOutcome.Refused;
        }
        File.Move(partial, staged, overwrite: true);
        return PicturesOutcome.UpToDate;
    }

    static void Place(string folder, string staging, ArtManifest manifest, List<ArtFile> missing)
    {
        Directory.CreateDirectory(folder);
        foreach (var file in missing)
        {
            string target = Local(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Delete(target);
            File.Copy(Path.Combine(staging, file.StoredName), target);
        }
        var listed = manifest.Files.Select(file => Path.GetFullPath(Local(folder, file))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        listed.UnionWith(KeptFiles(folder));
        Prune(new DirectoryInfo(folder), listed);
        if (Directory.Exists(staging))
        {
            Prune(new DirectoryInfo(staging), []);
            Directory.Delete(staging);
        }
    }

    static PicturesOutcome Clear(string folder)
    {
        string staging = folder + ".partial";
        RemoveLinks(folder);
        RemoveLinks(staging);
        if (Directory.Exists(folder))
            Prune(new DirectoryInfo(folder), KeptFiles(folder));
        if (Directory.Exists(staging))
        {
            Prune(new DirectoryInfo(staging), []);
            Directory.Delete(staging);
        }
        return PicturesOutcome.None;
    }

    static HashSet<string> KeptFiles(string folder) =>
        new([Path.GetFullPath(Path.Combine(folder, TitleNames.FileName))], StringComparer.OrdinalIgnoreCase);

    internal static void Prune(DirectoryInfo folder, HashSet<string> listed)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            bool link = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo sub && !link)
            {
                Prune(sub, listed);
                if (!sub.EnumerateFileSystemInfos().Any())
                    sub.Delete();
            }
            else if (link || !listed.Contains(entry.FullName))
            {
                entry.Delete();
            }
        }
    }

    static void RemoveLinks(string path)
    {
        var folder = new DirectoryInfo(path);
        if (!folder.Exists)
            return;
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            folder.Delete();
            return;
        }
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                entry.Delete();
            else if (entry is DirectoryInfo)
                RemoveLinks(entry.FullName);
        }
    }

    static string Local(string folder, ArtFile file) => Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));

    // Same size and hash as listed.
    static bool Matches(string path, ArtFile file)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            using var stream = info.OpenRead();
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == file.Sha256;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool IsNetworkError(Exception e, CancellationToken cancel) =>
        (e is HttpRequestException or IOException or OperationCanceledException) && !cancel.IsCancellationRequested;
}
