using System.Net;
using System.Text.Json;

namespace Chaos;

/// <summary>The parts of GET /api/info the launcher uses.</summary>
public sealed record ServerInfo(Version? OldestLauncher)
{
    public bool TooOld(Version launcher) => OldestLauncher is { } oldest && launcher < oldest;

    public static async Task<ServerInfo?> FetchAsync(HttpClient http, Uri api, CancellationToken cancel = default)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(api, "api/info"), cancel);
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancel));
            return Parse(json.RootElement);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or JsonException)
                                  && !cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static ServerInfo? Parse(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
            ? new ServerInfo(MinVersion(root))
            : null;

    // No valid minimum means any launcher is allowed.
    static Version? MinVersion(JsonElement root)
    {
        if (!root.TryGetProperty("launcher", out var launcher) || launcher.ValueKind != JsonValueKind.Object
            || !launcher.TryGetProperty("min", out var min) || min.ValueKind != JsonValueKind.String
            || !Version.TryParse(min.GetString(), out var version))
            return null;
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
