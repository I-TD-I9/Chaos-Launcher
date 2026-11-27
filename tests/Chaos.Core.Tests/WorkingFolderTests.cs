using System.Text;
using Chaos.Game;

namespace Chaos.Tests;

public sealed class WorkingFolderTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "bc-working-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    const string February = @"C:\Program Files (x86)\KONAMI\Yu-Gi-Oh! ONLINE Ver.060110.00";
    const string FebruaryShort = @"C:\PROGRA~2\KONAMI\YU-GI-~1.00";

    // Close enough to a Western code page for these paths.
    static bool Ascii(string path) => path.All(c => c < 0x80);

    static Func<string, string?> Short(params (string Long, string Short)[] names) =>
        path => names.FirstOrDefault(name => string.Equals(name.Long, path, StringComparison.OrdinalIgnoreCase)).Short;

    static List<string>? Lines(string game, string patchList, Func<string, bool>? fits = null, Func<string, string?>? shortName = null) =>
        WorkingFolder.Lines(game, Encoding.Latin1.GetBytes(patchList), fits ?? Ascii, shortName ?? Short());

    [Fact]
    public void ShortNameForUsualFolder()
    {
        var lines = Lines(February, "merge.dat\r\n", shortName: Short((February, FebruaryShort)))!;
        Assert.Equal(["bc", FebruaryShort + @"\merge.dat", FebruaryShort], lines);
        // A patch.txt known to work with the game.
        Assert.Equal("bc\r\nC:\\PROGRA~2\\KONAMI\\YU-GI-~1.00\\merge.dat\r\nC:\\PROGRA~2\\KONAMI\\YU-GI-~1.00\r\n",
                     string.Concat(lines.Select(line => line + "\r\n")));
    }

    [Fact]
    public void KeepsPlainFolder()
    {
        Assert.Equal(["bc", @"D:\Games\YGO\merge.dat", @"D:\Games\YGO"], Lines(@"D:\Games\YGO", "merge.dat\r\n"));
        Assert.Equal(["bc", @"D:\Games\YGO\merge.dat", @"D:\Games\YGO"], Lines(@"D:\Games\YGO\", "merge.dat\r\n"));
    }

    [Fact]
    public void SpacesNeedShortName()
    {
        Assert.Null(Lines(February, "merge.dat\r\n"));
        Assert.Null(Lines(@"D:\My Games\YGO", "merge.dat\r\n"));
    }

    [Fact]
    public void UnspellableNeedsShortName()
    {
        const string Russian = @"C:\Игры\Yu-Gi-Oh";
        Assert.Equal(["bc", @"C:\8D0B~1\Yu-Gi-Oh\merge.dat", @"C:\8D0B~1\Yu-Gi-Oh"],
                     Lines(Russian, "merge.dat\r\n", shortName: Short((Russian, @"C:\8D0B~1\Yu-Gi-Oh"))));
        Assert.Null(Lines(Russian, "merge.dat\r\n"));
    }

    [Fact]
    public void DatInNameNeedsShortName()
    {
        const string Data = @"D:\my.data\YGO";
        Assert.Null(Lines(Data, "merge.dat\r\n"));
        Assert.Equal(["bc", @"D:\MYDATA~1.DAT\YGO\merge.dat", @"D:\MYDATA~1.DAT\YGO"],
                     Lines(Data, "merge.dat\r\n", shortName: Short((Data, @"D:\MYDATA~1.DAT\YGO"))));
    }

    [Fact]
    public void LineHoldsUpTo253Chars()
    {
        string fits = @"D:\" + new string('g', 253 - @"D:\".Length - @"\merge.dat".Length);
        Assert.Equal(253, Lines(fits, "merge.dat\r\n")![1].Length);
        string tooLong = fits + "g";
        Assert.Null(Lines(tooLong, "merge.dat\r\n"));
        Assert.Equal(["bc", @"D:\GGGGGG~1\merge.dat", @"D:\GGGGGG~1"],
                     Lines(tooLong, "merge.dat\r\n", shortName: Short((tooLong, @"D:\GGGGGG~1"))));
    }

    [Fact]
    public void KeepsLineOrder()
    {
        Assert.Equal(["bc", @"D:\YGO\p06020900.dat", @"D:\YGO\merge.dat", @"D:\YGO"],
                     Lines(@"D:\YGO", "# the archives\r\n\r\np06020900.dat\r\nmerge.dat\r\n"));
        // LF line endings, no final newline.
        Assert.Equal(["bc", @"D:\YGO\p06020900.dat", @"D:\YGO\merge.dat", @"D:\YGO"],
                     Lines(@"D:\YGO", "p06020900.dat\nmerge.dat"));
    }

    [Theory]
    [InlineData("merge.dat.bak\r\n")]
    [InlineData("merge\r\n")]
    [InlineData("merge.dat junk\r\n")]
    public void LineEndsAtFirstDat(string patchList) =>
        Assert.Equal(["bc", @"D:\YGO\merge.dat", @"D:\YGO"], Lines(@"D:\YGO", patchList));

    [Fact]
    public void KeepsArchiveFolder()
    {
        Assert.Equal(["bc", @"E:\Archives\merge.dat", @"D:\YGO"], Lines(@"D:\YGO", @"E:\Archives\merge.dat"));
        Assert.Equal(["bc", @"D:\YGO\old\merge.dat", @"D:\YGO"], Lines(@"D:\YGO", @"old\merge.dat"));
        Assert.Null(Lines(@"D:\YGO", @"E:\Old Archives\merge.dat"));
        Assert.Null(Lines(@"D:\YGO", "my merge.dat"));
    }

    [Fact]
    public void PrepareWritesPatchTxt()
    {
        string game = Path.Combine(folder, "YGO");
        string working = Path.Combine(folder, "Launcher", "Game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "patch.txt"), "merge.dat\r\n");
        Assert.Equal((working, (NoPictures?)null), WorkingFolder.Prepare(game, working, Ascii, Short()));
        Assert.Equal($"bc\r\n{game}\\merge.dat\r\n{game}\r\n", File.ReadAllText(Path.Combine(working, "patch.txt")));
        Assert.Empty(Directory.GetFiles(working, "*.new"));
    }

    [Fact]
    public void StartsInGameFolderWithoutShortPath()
    {
        string game = Path.Combine(folder, "My Games", "YGO");
        string working = Path.Combine(folder, "Game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "patch.txt"), "merge.dat\r\n");
        Assert.Equal((game, NoPictures.GameFolder), WorkingFolder.Prepare(game, working, Ascii, Short()));
        Assert.False(File.Exists(Path.Combine(working, "patch.txt")));
    }

    [Fact]
    public void FallbackUsesShortName()
    {
        const string Russian = @"C:\Игры\Yu-Gi-Oh";
        Assert.Equal(@"C:\8D0B~1\Yu-Gi-Oh", WorkingFolder.Fallback(Russian, Ascii, Short((Russian, @"C:\8D0B~1\Yu-Gi-Oh"))));
        Assert.Equal(Russian, WorkingFolder.Fallback(Russian, Ascii, Short()));
        Assert.Equal(@"D:\My Games\YGO", WorkingFolder.Fallback(@"D:\My Games\YGO", Ascii, Short()));
    }

    [Fact]
    public void ShortNameForUnsupportedFolder()
    {
        string game = Path.Combine(folder, "YGO");
        string working = Path.Combine(folder, "Game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "patch.txt"), "merge.dat\r\n");
        bool fits(string path) => Ascii(path) && path != working;
        Assert.Equal((@"C:\GAME~1", (NoPictures?)null),
                     WorkingFolder.Prepare(game, working, path => fits(path) || path == @"C:\GAME~1", Short((working, @"C:\GAME~1"))));
        File.Delete(Path.Combine(working, "patch.txt"));
        // Otherwise the launcher's folder is the reason.
        Assert.Equal((game, NoPictures.LauncherFolder), WorkingFolder.Prepare(game, working, fits, Short()));
        Assert.Equal((game, NoPictures.LauncherFolder), WorkingFolder.Prepare(game, working, path => fits(path) && path != game, Short()));
        Assert.False(File.Exists(Path.Combine(working, "patch.txt")));
    }

    [RetailClientFact]
    public void RetailFolderFitsPatchTxt()
    {
        string game = Path.GetDirectoryName(RetailClient.Path!)!;
        var lines = WorkingFolder.Lines(game, File.ReadAllBytes(Path.Combine(game, "patch.txt")), AnsiPath.Fits, AnsiPath.ShortName);
        Assert.NotNull(lines);
        Assert.Equal("bc", lines[0]);
        Assert.EndsWith(@"\merge.dat", lines[1]);
        Assert.All(lines, line => Assert.True(line.Length <= 253 && !line.Contains(' ') && AnsiPath.Fits(line), line));
        Assert.True(Directory.Exists(lines[^1]));
        Assert.True(File.Exists(lines[1]));
    }

    [Fact]
    public void ReportsMissingPatchTxt()
    {
        string game = Path.Combine(folder, "YGO");
        Directory.CreateDirectory(game);
        Assert.ThrowsAny<IOException>(() => WorkingFolder.Prepare(game, Path.Combine(folder, "Game"), Ascii, Short()));
    }
}
