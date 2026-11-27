using System.Diagnostics;
using Chaos.Client;
using Chaos.Display;

namespace Chaos.Game;

/// <summary>Patches the client and starts it.</summary>
public static class GameStarter
{
    public const string ClientName = "online_pc.exe";

    public static string Prepare(GameFolder game, ServerEndpoints server, string folder)
    {
        if (game.Status != GameStatus.Ready || game.Client is null)
            throw new InvalidOperationException($"no supported client in {game.Path}");
        byte[] retail = File.ReadAllBytes(game.Client);
        // Check again, the file may have changed since.
        if (SupportedClient.Sha256Of(retail) != SupportedClient.Sha256)
            throw new PatchException($"{game.Client} has changed since it was checked");
        byte[] patched = ClientPatcher.Patch(retail, server);
        if (server.IsPublic && SupportedClient.Sha256Of(patched) != SupportedClient.PublicSha256)
            throw new PatchException("the patched client is not the expected file");

        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, ClientName);
        // Fails while the old copy is running.
        LauncherFiles.Replace(target, patched);
        return target;
    }

    public static Process Start(string client, string workingFolder, Resolution resolution, string? home) =>
        Process.Start(StartInfo(client, workingFolder, resolution, home)) ?? throw new InvalidOperationException("no process was started");

    internal static ProcessStartInfo StartInfo(string client, string workingFolder, Resolution resolution, string? home)
    {
        bool scaled = !resolution.IsDefault && home is not null;
        var info = new ProcessStartInfo(home is null ? client : Path.Join(home, Path.GetFileName(client)))
        {
            WorkingDirectory = workingFolder,
            Arguments = scaled ? "-32" : "-win",
            UseShellExecute = false,
        };
        info.Environment.Remove(CncDdraw.ConfigFileVariable);
        if (scaled)
            info.Environment[CncDdraw.ConfigFileVariable] = Path.Join(home, CncDdraw.IniName);
        return info;
    }
}
