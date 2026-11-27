namespace Chaos.Game;

/// <summary>Why the client runs without the card pictures.</summary>
public enum NoPictures
{
    GameFolder,
    LauncherFolder,
}

/// <summary>The folder the client starts in.</summary>
public static class WorkingFolder
{
    public static string Path => System.IO.Path.Combine(LauncherSettings.Folder, "Game");

    public const string Overrides = "bc";

    internal const int MaxLineLength = 0x100 - 3;

    public static (string Start, NoPictures? Reason) Prepare(GameFolder game, string folder) =>
        Prepare(game.Path, folder, AnsiPath.Fits, AnsiPath.ShortName);

    public static string Fallback(GameFolder game) => Fallback(game.Path, AnsiPath.Fits, AnsiPath.ShortName);

    internal static (string Start, NoPictures? Reason) Prepare(string gameFolder, string folder, Func<string, bool> fits,
                                                             Func<string, string?> shortName)
    {
        // Launcher's folder first.
        if (AnsiPath.Folder(folder, fits, shortName) is not { } start)
            return (Fallback(gameFolder, fits, shortName), NoPictures.LauncherFolder);
        var lines = Lines(gameFolder, File.ReadAllBytes(System.IO.Path.Combine(gameFolder, GameFolder.PatchList)), fits, shortName);
        if (lines is null)
            return (Fallback(gameFolder, fits, shortName), NoPictures.GameFolder);
        LauncherFiles.Replace(System.IO.Path.Combine(folder, GameFolder.PatchList),
                              AnsiPath.CodePage().GetBytes(string.Concat(lines.Select(line => line + "\r\n"))));
        return (start, null);
    }

    internal static string Fallback(string gameFolder, Func<string, bool> fits, Func<string, string?> shortName) =>
        AnsiPath.Usable(gameFolder, fits, shortName) ?? gameFolder;

    internal static List<string>? Lines(string gameFolder, ReadOnlySpan<byte> patchList,
                                        Func<string, bool> fits, Func<string, string?> shortName)
    {
        string game = System.IO.Path.TrimEndingDirectorySeparator(gameFolder);
        var archives = GameFolder.Archives(patchList);
        foreach (var folder in Candidates(game, fits, shortName))
        {
            var lines = new List<string> { Overrides };
            // The client cuts each line at its first .dat.
            foreach (var archive in archives)
                if (Archive(game, folder, archive + ".dat", fits, shortName) is { } line)
                    lines.Add(line);
            if (lines.Count == archives.Count + 1)
                return [.. lines, folder];
        }
        return null;
    }

    static string? Archive(string game, string folder, string name, Func<string, bool> fits, Func<string, string?> shortName)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(System.IO.Path.Combine(game, name));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        string parent = System.IO.Path.GetDirectoryName(full) ?? "";
        IEnumerable<string> candidates = string.Equals(parent, game, StringComparison.OrdinalIgnoreCase) ? [folder] : Candidates(parent, fits, shortName);
        foreach (var at in candidates)
        {
            string line = System.IO.Path.Join(at, System.IO.Path.GetFileName(full));
            if (CanUse(line, fits) && line.IndexOf(".dat", StringComparison.Ordinal) == line.Length - 4)
                return line;
        }
        return null;
    }

    // The folder itself, then its short name.
    static IEnumerable<string> Candidates(string folder, Func<string, bool> fits, Func<string, string?> shortName)
    {
        if (CanUse(folder, fits) && !folder.Contains(".dat", StringComparison.Ordinal))
            yield return folder;
        if (shortName(folder) is { } name && name != folder && CanUse(name, fits) && !name.Contains(".dat", StringComparison.Ordinal))
            yield return name;
    }

    static bool CanUse(string line, Func<string, bool> fits) =>
        line.Length <= MaxLineLength && !line.Contains(' ') && fits(line);
}
