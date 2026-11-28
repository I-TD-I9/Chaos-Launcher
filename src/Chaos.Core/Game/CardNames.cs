using System.Buffers.Binary;
using System.Text;

namespace Chaos.Game;

/// <summary>Result of writing the card names.</summary>
public enum CardNamesOutcome
{
    Written,
    None,
    Unwritable,
}

/// <summary>The client's card names, with the one it lacks.</summary>
public static class CardNames
{
    internal const string NumberEntry = @"data\bin\CARD_ID.bin";

    internal const int RecordLength = 64;
    internal const int Records = 2351;
    internal const int Slot = 1024;
    internal const ushort Number = 1258;

    // Konami's own name for the card.
    internal static readonly IReadOnlyList<Language> Languages =
    [
        new("ENG", "Mask of Will and Testimony"),
        new("GER", "Maske des Willens und Zeugnisses"),
        new("FRA", "Masque de Volont\u00e9 et de Jugement"),
        new("ITA", "Maschera della Volont\u00e0 e della Prova"),
        new("SPA", "M\u00e1scara de Voluntad y Testimonio"),
    ];

    // Read from bc ahead of merge.dat.
    public static IEnumerable<string> Entries => Languages.Select(language => language.Entry);

    internal sealed record Language(string Code, string Name)
    {
        public string Entry => $@"data\bin\CARD_Name{Code}.bin";

        public byte[] Bytes => Encoding.Latin1.GetBytes(Name);
    }

    public static CardNamesOutcome Write(string folder, string gameFolder)
    {
        var outcome = CardNamesOutcome.None;
        try
        {
            if (Linked(folder))
                return CardNamesOutcome.Unwritable;
            var archives = Archives(gameFolder);
            var numbers = archives is null ? null : GameArchive.Read(archives, NumberEntry);
            foreach (var language in Languages)
            {
                string path = Path.Combine(folder, language.Entry);
                try
                {
                    var names = archives is null ? null : Build(language, GameArchive.Read(archives, language.Entry), numbers);
                    if (names is null)
                    {
                        // The client reads its own then.
                        if (File.Exists(path))
                            File.Delete(path);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    LauncherFiles.Replace(path, names);
                    if (outcome == CardNamesOutcome.None)
                        outcome = CardNamesOutcome.Written;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    outcome = CardNamesOutcome.Unwritable;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return CardNamesOutcome.Unwritable;
        }
        return outcome;
    }

    // Fills the empty record.
    internal static byte[]? Build(Language language, byte[]? names, byte[]? numbers)
    {
        const int at = Slot * RecordLength;
        if (names is not { Length: Records * RecordLength } || numbers is not { Length: Records * 2 }
            || BinaryPrimitives.ReadUInt16LittleEndian(numbers.AsSpan(Slot * 2)) != Number
            || names.AsSpan(at, RecordLength).ContainsAnyExcept((byte)0)
            || names.AsSpan(0, 4).SequenceEqual("YPK1"u8))
            return null;
        var filled = (byte[])names.Clone();
        language.Bytes.CopyTo(filled.AsSpan(at));
        return filled;
    }

    // Null if the client may read archives not seen here.
    static List<string>? Archives(string gameFolder)
    {
        var kept = GameArchive.InFolder(gameFolder);
        try
        {
            string list = Path.Combine(gameFolder, GameFolder.PatchList);
            return !File.Exists(list) || GameFolder.Archives(File.ReadAllBytes(list)).Count == kept.Count ? kept : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static bool Linked(string folder) =>
        new[] { folder, Path.Combine(folder, "data"), Path.Combine(folder, "data", "bin") }
            .Select(path => new DirectoryInfo(path))
            .Any(dir => dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint));
}
