using Microsoft.Win32;

namespace Chaos.Game;

public static class ClientKey
{
    public const string Key = @"SOFTWARE\KONAMI\Yu-Gi-Oh! ONLINE\system";

    public const string Release = "koe";

    public static string SaveFolder => Path.Combine(LauncherSettings.Folder, "Saves");

    public const string Fallback = "Chaos Launcher saves";

    // Name used by older launchers.
    const string OldFallback = "Battle City saves";

    public static string? Prepare(string saveFolder, string? gameFolder, string key = Key) =>
        Prepare(saveFolder, gameFolder, key, AnsiPath.Fits, AnsiPath.ShortName);

    internal static string? Prepare(string saveFolder, string? gameFolder, string key,
                                    Func<string, bool> fits, Func<string, string?> shortName)
    {
        string? usable = AnsiPath.Folder(saveFolder, fits, shortName);
        if (usable is null && gameFolder is not null
            && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(gameFolder)) is { Length: > 0 } parent)
            usable = AnsiPath.Folder(FolderMove.Migrate(Path.Combine(parent, OldFallback), Path.Combine(parent, Fallback)),
                                     fits, shortName);
        using var system = Registry.CurrentUser.CreateSubKey(key, writable: true);
        if (usable is null)
            system.DeleteValue("CommonDir", throwOnMissingValue: false);
        else
            system.SetValue("CommonDir", usable, RegistryValueKind.String);
        system.SetValue("Release", Release, RegistryValueKind.String);
        return usable;
    }
}
