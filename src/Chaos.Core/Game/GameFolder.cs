using System.Text;
using Chaos.Client;

namespace Chaos.Game;

public enum GameStatus
{
    NotFound,
    NoClient,
    Unsupported,
    DataMissing,
    Ready,
}

/// <summary>A possible game folder and what was found in it.</summary>
public sealed record GameFolder(string Path, GameStatus Status, string? Client)
{
    public const string PatchList = "patch.txt";

    public static GameFolder Inspect(string path) => Inspect(path, SupportedClient.IsSupported);

    internal static GameFolder Inspect(string path, Func<string, bool> supported)
    {
        if (!Directory.Exists(path))
            return new(path, GameStatus.NotFound, null);
        string client = System.IO.Path.Combine(path, SupportedClient.FileName);
        if (!File.Exists(client))
            return new(path, GameStatus.NoClient, null);
        string? retail = supported(client) ? client : null;
        string kept = client + ".orig";
        if (retail is null && File.Exists(kept) && supported(kept))
            retail = kept;
        if (retail is null)
            return new(path, GameStatus.Unsupported, null);
        if (!HasData(path))
            return new(path, GameStatus.DataMissing, null);
        return new(path, GameStatus.Ready, retail);
    }

    internal static bool HasData(string folder)
    {
        string list = System.IO.Path.Combine(folder, PatchList);
        return File.Exists(list)
            && Archives(File.ReadAllBytes(list)).Any(name => IsArchive(System.IO.Path.Combine(folder, name + ".dat")));
    }

    static ReadOnlySpan<byte> ArchiveTag => "KCEJYUGI"u8;

    static bool IsArchive(string path)
    {
        if (!File.Exists(path))
            return false;
        using var file = File.OpenRead(path);
        Span<byte> tag = stackalloc byte[ArchiveTag.Length];
        return file.ReadAtLeast(tag, tag.Length, throwOnEndOfStream: false) == tag.Length && tag.SequenceEqual(ArchiveTag);
    }

    internal static List<string> Archives(ReadOnlySpan<byte> text)
    {
        var names = new List<string>();
        foreach (var raw in Encoding.Latin1.GetString(text).Split('\n'))
        {
            string line = raw;
            int end = line.IndexOf('\r');
            if (end >= 0)
                line = line[..end];
            if (line.Length == 0 || line[0] == '#')
                continue;
            int dat = line.IndexOf(".dat", StringComparison.Ordinal);
            names.Add(dat >= 0 ? line[..dat] : line);
        }
        return names;
    }
}
