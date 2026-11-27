using System.Security;
using Microsoft.Win32;

namespace Chaos.Game;

/// <summary>Finds the player's game folder.</summary>
public static class GameLocator
{
    // Installer keys and default folder.
    const string SystemKey = @"SOFTWARE\KONAMI\Yu-Gi-Oh! ONLINE\system";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{67FB570C-900D-4515-ACB3-1004074D63D3}";
    const string DefaultFolder = @"KONAMI\Yu-Gi-Oh! ONLINE";

    public static IEnumerable<string> Candidates(string? chosen)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string?[] folders =
        [
            chosen,
            Registry32(SystemKey, "CommonDir"),
            Registry32(UninstallKey, "InstallLocation"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), DefaultFolder),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), DefaultFolder),
        ];
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (seen.Add(full))
                yield return full;
        }
    }

    public static GameFolder? Find(string? chosen) => Find(Candidates(chosen), GameFolder.Inspect);

    internal static GameFolder? Find(IEnumerable<string> candidates, Func<string, GameFolder> inspect)
    {
        GameFolder? incomplete = null;
        GameFolder? other = null;
        foreach (var folder in candidates)
        {
            GameFolder found;
            try
            {
                found = inspect(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (found.Status == GameStatus.Ready)
                return found;
            if (found.Status == GameStatus.DataMissing)
                incomplete ??= found;
            else if (found.Status == GameStatus.Unsupported)
                other ??= found;
        }
        return incomplete ?? other;
    }

    static string? Registry32(string key, string value)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var sub = root.OpenSubKey(key);
            return sub?.GetValue(value) as string;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}
