using System.Text.Json;
using System.Text.Json.Serialization;
using Chaos.Client;

namespace Chaos;

/// <summary>Launcher settings, saved as settings.json.</summary>
public sealed class LauncherSettings
{
    const string FolderName = "ChaosLauncher";

    // The folder's name before Chaos Launcher.
    const string OldFolderName = "BattleCity";

    public static string Folder { get; private set; } = Path.Combine(LocalData, FolderName);

    public static string DefaultPath => Path.Combine(Folder, "settings.json");

    static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static void MoveOldFolder() =>
        Folder = FolderMove.Migrate(Path.Combine(LocalData, OldFolderName), Path.Combine(LocalData, FolderName));

    // Lenient, since the file may be edited by hand.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string? GameFolder { get; set; }

    public Display.Resolution Resolution { get; set; }

    // For testing against another server, e.g. a local one.
    public string? GateHost { get; set; }
    public int? GatePort { get; set; }
    public string? Api { get; set; }

    public static LauncherSettings Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string partial = path + ".new";
        File.WriteAllText(partial, JsonSerializer.Serialize(this, Json));
        File.Move(partial, path, overwrite: true);
    }

    public ServerEndpoints Server()
    {
        var server = ServerEndpoints.Public;
        if (GateHost is { } host && ClientPatcher.IsHostName(host) && host.Length < ClientPatcher.GateHostSlot)
            server = server with { GateHost = host };
        if (GatePort is > 0 and <= 0xFFFF)
            server = server with { GatePort = GatePort.Value };
        // Plain http only for localhost.
        if (Api is { } api && Uri.TryCreate(api, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            server = server with { Api = uri };
        return server;
    }
}
