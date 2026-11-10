using System.Text.Json;
using System.Text.Json.Serialization;
using Chaos.Client;

namespace Chaos;

/// <summary>What the launcher remembers between runs, kept as settings.json in its folder.</summary>
public sealed class LauncherSettings
{
    const string FolderName = "ChaosLauncher";

    // The launcher's folder before it was called Chaos Launcher.
    const string OldFolderName = "BattleCity";

    /// <summary>The launcher's folder: its settings, and the patched client with cnc-ddraw beside it.</summary>
    public static string Folder { get; private set; } = Path.Combine(LocalData, FolderName);

    public static string DefaultPath => Path.Combine(Folder, "settings.json");

    static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Brings the folder an earlier launcher made under its old name along, once, at start-up and
    /// before anything reads <see cref="Folder"/>: the players' profiles and decks are in it.
    /// When the move cannot be made, this run uses the old folder, and the next start tries again.
    /// </summary>
    public static void MoveOldFolder() =>
        Folder = FolderMove.Adopt(Path.Combine(LocalData, OldFolderName), Path.Combine(LocalData, FolderName));

    // Lenient on reading, since the file is also edited by hand to try another server.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The game folder the player chose, when the launcher could not find it alone.</summary>
    public string? GameFolder { get; set; }

    /// <summary>How the game is shown from the next Play on: in its own window unless the player chose otherwise.</summary>
    public Display.Resolution Resolution { get; set; }

    // For trying the launcher against another server, such as one on this machine. Nothing in the
    // launcher's window sets these.
    public string? GateHost { get; set; }
    public int? GatePort { get; set; }
    public string? Api { get; set; }

    /// <summary>The settings at <paramref name="path"/>; the defaults if there are none or they cannot be read.</summary>
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

    /// <summary>The Battle City server, with whichever overrides set here are usable.</summary>
    public ServerEndpoints Server()
    {
        var server = ServerEndpoints.Public;
        if (GateHost is { } host && ClientPatcher.IsHostName(host) && host.Length < ClientPatcher.GateHostSlot)
            server = server with { GateHost = host };
        if (GatePort is > 0 and <= 0xFFFF)
            server = server with { GatePort = GatePort.Value };
        // Plain http is only for a server on this machine: anywhere else the reply could be tampered with.
        if (Api is { } api && Uri.TryCreate(api, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            server = server with { Api = uri };
        return server;
    }
}
