using System.Net;
using System.Text.Json;

namespace Chaos;

/// <summary>
/// What the server's GET /api/info says that the launcher uses: the oldest launcher it still takes,
/// when it names one.
/// </summary>
public sealed record ServerInfo(Version? OldestLauncher)
{
    /// <summary>Whether a launcher of this version is older than the server takes.</summary>
    public bool TooOld(Version launcher) => OldestLauncher is { } oldest && launcher < oldest;

    /// <summary>The server's info, or null when it cannot be reached or does not answer with one.</summary>
    /// <remarks>
    /// The reply is read as bytes and parsed as the UTF-8 that JSON is, whatever charset it claims:
    /// a filtering proxy's own page may name one .NET cannot decode at all.
    /// </remarks>
    public static async Task<ServerInfo?> FetchAsync(HttpClient http, Uri api, CancellationToken cancel = default)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(api, "api/info"), cancel);
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancel));
            return FromInfo(json.RootElement);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or JsonException)
                                  && !cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The info in a GET /api/info reply, or null when it is not one.</summary>
    internal static ServerInfo? FromInfo(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
            ? new ServerInfo(Oldest(root))
            : null;

    // No minimum, a null one or one that is no version all let any launcher in: a server from
    // before the setting sends none.
    static Version? Oldest(JsonElement root)
    {
        if (!root.TryGetProperty("launcher", out var launcher) || launcher.ValueKind != JsonValueKind.Object
            || !launcher.TryGetProperty("min", out var min) || min.ValueKind != JsonValueKind.String
            || !Version.TryParse(min.GetString(), out var version))
            return null;
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
