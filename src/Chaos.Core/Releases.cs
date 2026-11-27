using System.Reflection;
using System.Text.Json;

namespace Chaos;

/// <summary>A GitHub release asset.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Sha256);

/// <summary>A launcher release.</summary>
public sealed record LauncherRelease(Version Version, Uri Page, ReleaseAsset? Exe = null, ReleaseAsset? Checksum = null)
{
    public bool CanSelfUpdate => Exe is not null && Checksum is not null;
}

public static class Releases
{
    public static Uri ProjectPage { get; } = new("https://github.com/I-TD-I9/Chaos-Launcher");

    public static Uri LatestPage { get; } = new("https://github.com/I-TD-I9/Chaos-Launcher/releases/latest");

    static Uri LatestApi { get; } = new("https://api.github.com/repos/I-TD-I9/Chaos-Launcher/releases/latest");

    public static Version Current { get; } = Trim(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static async Task<LauncherRelease?> LatestAsync(HttpClient http, CancellationToken cancel = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode)
                return null;
            // Read as bytes, ignoring the charset.
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancel));
            return Parse(json.RootElement);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or JsonException)
                                  && !cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    public const string ExeName = "ChaosLauncher.exe";

    public const string ChecksumName = ExeName + ".sha256";

    public const long MaxExeSize = 200L * 1024 * 1024;

    // The checksum file is one short line.
    internal const long MaxChecksumSize = 4096;

    internal static LauncherRelease? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("html_url", out var url) || url.ValueKind != JsonValueKind.String)
            return null;
        if (!Version.TryParse(tag.GetString()!.TrimStart('v', 'V'), out var version)
            || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var page)
            || !page.AbsoluteUri.StartsWith(ProjectPage.AbsoluteUri + "/", StringComparison.Ordinal))
            return null;
        version = Trim(version);
        ReleaseAsset? exe = null, checksum = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                if (Asset(asset, version) is not { } found)
                    continue;
                if (found.Name == ExeName && found.Size <= MaxExeSize)
                    exe ??= found;
                else if (found.Name == ChecksumName && found.Size <= MaxChecksumSize)
                    checksum ??= found;
            }
        return new LauncherRelease(version, page, exe, checksum);
    }

    internal static ReleaseAsset? Asset(JsonElement asset, Version version)
    {
        if (asset.ValueKind != JsonValueKind.Object
            || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String
            || !asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out long bytes) || bytes <= 0)
            return null;
        string file = name.GetString()!;
        if (file is not (ExeName or ChecksumName))
            return null;
        string expected = $"{ProjectPage.AbsoluteUri}/releases/download/v{version}/{file}";
        if (url.GetString() != expected)
            return null;
        string? sha256 = null;
        if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
        {
            // GitHub writes "sha256:<hex>".
            string given = digest.GetString()!;
            if (given.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha256 = Hex(given["sha256:".Length..]);
                if (sha256 is null)
                    return null;
            }
        }
        return new ReleaseAsset(file, new Uri(expected), bytes, sha256);
    }

    internal static string? Hex(string text) =>
        text.Length == 64 && text.All(Uri.IsHexDigit) ? text.ToLowerInvariant() : null;

    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
