using System.Reflection;
using System.Text.Json;

namespace Chaos;

/// <summary>
/// A file attached to a release on GitHub.
/// </summary>
/// <param name="Name">The file's name, ChaosLauncher.exe or ChaosLauncher.exe.sha256.</param>
/// <param name="Url">Where GitHub serves it: always under this project's own release of that version.</param>
/// <param name="Size">Its size in bytes, as GitHub gives it.</param>
/// <param name="Sha256">
/// Its SHA-256 in lower-case hex, from the asset's "digest" when GitHub gives one (it does for
/// files uploaded since mid-2025), or null.
/// </param>
public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Sha256);

/// <summary>A published version of the launcher.</summary>
/// <param name="Exe">The launcher itself, when the release carries it where it should be.</param>
/// <param name="Checksum">The file holding the launcher's SHA-256, when the release carries it where it should be.</param>
public sealed record LauncherRelease(Version Version, Uri Page, ReleaseAsset? Exe = null, ReleaseAsset? Checksum = null)
{
    /// <summary>
    /// Whether the launcher can update itself to this release: it carries both the launcher and
    /// its checksum. Without them the player is sent to <see cref="Page"/>.
    /// </summary>
    public bool CanSelfUpdate => Exe is not null && Checksum is not null;
}

/// <summary>The launcher's own releases on GitHub, so a player hears when a newer one is out.</summary>
public static class Releases
{
    public static Uri ProjectPage { get; } = new("https://github.com/I-TD-I9/Chaos-Launcher");

    /// <summary>The page of the newest release, wherever GitHub has it.</summary>
    public static Uri LatestPage { get; } = new("https://github.com/I-TD-I9/Chaos-Launcher/releases/latest");

    static Uri LatestApi { get; } = new("https://api.github.com/repos/I-TD-I9/Chaos-Launcher/releases/latest");

    /// <summary>This launcher's version, as major.minor.patch.</summary>
    public static Version Current { get; } = Trim(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>The newest release, or null when there is none or GitHub cannot be reached.</summary>
    public static async Task<LauncherRelease?> LatestAsync(HttpClient http, CancellationToken cancel = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode)
                return null;
            // As bytes, whatever charset the reply claims (ServerInfo.FetchAsync says why).
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancel));
            return Parse(json.RootElement);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or JsonException)
                                  && !cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The name the release workflow publishes the launcher under.</summary>
    public const string ExeName = "ChaosLauncher.exe";

    /// <summary>The name of the file beside it holding its SHA-256, as "&lt;hash&gt;  ChaosLauncher.exe".</summary>
    public const string ChecksumName = ExeName + ".sha256";

    /// <summary>
    /// The largest launcher believed: a published one is about 70 MB. An asset claiming more is
    /// no asset.
    /// </summary>
    public const long LargestExe = 200L * 1024 * 1024;

    // The checksum file is one line of under a hundred bytes.
    internal const long LargestChecksum = 4096;

    /// <summary>
    /// The release a GitHub "latest release" reply describes, if it names one of this project's
    /// pages, with the launcher and its checksum among its assets when each is where the release
    /// workflow puts it.
    /// </summary>
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
                if (found.Name == ExeName && found.Size <= LargestExe)
                    exe ??= found;
                else if (found.Name == ChecksumName && found.Size <= LargestChecksum)
                    checksum ??= found;
            }
        return new LauncherRelease(version, page, exe, checksum);
    }

    /// <summary>
    /// An asset of the release of <paramref name="version"/>, when its browser_download_url is
    /// exactly where GitHub serves this project's own files of that release,
    /// https://github.com/I-TD-I9/Chaos-Launcher/releases/download/v&lt;version&gt;/&lt;name&gt;, and
    /// its size and digest make sense. Nothing else is downloaded: not another repository's file,
    /// not another release's, not one over plain HTTP.
    /// </summary>
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
            // GitHub writes "sha256:<hex>". A digest of another kind can't be checked and is let
            // be; one that claims to be SHA-256 and isn't is a reply not to trust.
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

    /// <summary>A SHA-256 written as 64 hex digits, in lower case, or null when it isn't one.</summary>
    internal static string? Hex(string text) =>
        text.Length == 64 && text.All(Uri.IsHexDigit) ? text.ToLowerInvariant() : null;

    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
