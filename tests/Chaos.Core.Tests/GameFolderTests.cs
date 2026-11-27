using System.Text;
using Chaos.Client;
using Chaos.Game;

namespace Chaos.Tests;

public sealed class GameFolderTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "bc-folder-" + Guid.NewGuid().ToString("N"));

    public GameFolderTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    string Client => Path.Combine(folder, SupportedClient.FileName);

    internal static void WriteData(string folder)
    {
        File.WriteAllText(Path.Combine(folder, GameFolder.PatchList), "merge.dat\r\n");
        WriteArchive(Path.Combine(folder, "merge.dat"));
    }

    // KCEJYUGI tag plus a few bytes.
    static void WriteArchive(string path) => File.WriteAllBytes(path, [.. "KCEJYUGI"u8, 0xAF, 0x16, 0, 0]);

    void WriteList(string text) => File.WriteAllBytes(Path.Combine(folder, GameFolder.PatchList), Encoding.Latin1.GetBytes(text));

    [Fact]
    public void MissingFolderIsNotFound() =>
        Assert.Equal(GameStatus.NotFound, GameFolder.Inspect(Path.Combine(folder, "nowhere")).Status);

    [Fact]
    public void ReportsMissingClient() =>
        Assert.Equal(GameStatus.NoClient, GameFolder.Inspect(folder).Status);

    [Fact]
    public void OtherBuildIsUnsupported()
    {
        File.WriteAllBytes(Client, TestImage.Build());
        WriteData(folder);
        var found = GameFolder.Inspect(folder);
        Assert.Equal(GameStatus.Unsupported, found.Status);
        Assert.Null(found.Client);
    }

    [Fact]
    public void SupportedClientIsReady()
    {
        File.WriteAllBytes(Client, [1]);
        WriteData(folder);
        var found = GameFolder.Inspect(folder, _ => true);
        Assert.Equal(GameStatus.Ready, found.Status);
        Assert.Equal(Client, found.Client);
    }

    [Fact]
    public void UsesOriginalBesidePatched()
    {
        File.WriteAllBytes(Client, [1]);
        File.WriteAllBytes(Client + ".orig", [2]);
        WriteData(folder);
        var found = GameFolder.Inspect(folder, path => path.EndsWith(".orig", StringComparison.Ordinal));
        Assert.Equal(GameStatus.Ready, found.Status);
        Assert.Equal(Client + ".orig", found.Client);
    }

    [Fact]
    public void ChecksBuildBeforeData()
    {
        // Like a June install: wrong build, no patch.txt.
        File.WriteAllBytes(Client, TestImage.Build());
        Assert.Equal(GameStatus.Unsupported, GameFolder.Inspect(folder).Status);
    }

    [Fact]
    public void MissingDataWithoutPatchTxt()
    {
        File.WriteAllBytes(Client, [1]);
        WriteArchive(Path.Combine(folder, "merge.dat"));
        var found = GameFolder.Inspect(folder, _ => true);
        Assert.Equal(GameStatus.DataMissing, found.Status);
        Assert.Null(found.Client);
    }

    [Fact]
    public void MissingDataWithoutArchive()
    {
        File.WriteAllBytes(Client, [1]);
        WriteList("merge.dat\r\n");
        Assert.Equal(GameStatus.DataMissing, GameFolder.Inspect(folder, _ => true).Status);
    }

    [Fact]
    public void MissingDataWithEmptyPatchTxt()
    {
        File.WriteAllBytes(Client, [1]);
        WriteArchive(Path.Combine(folder, "merge.dat"));
        WriteList("# merge.dat\r\n\r\n");
        Assert.Equal(GameStatus.DataMissing, GameFolder.Inspect(folder, _ => true).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("KCEJ")]
    [InlineData("PK\u0003\u0004 a zip, not an archive")]
    public void IgnoresNonArchive(string content)
    {
        File.WriteAllBytes(Client, [1]);
        WriteList("merge.dat\r\n");
        File.WriteAllBytes(Path.Combine(folder, "merge.dat"), Encoding.Latin1.GetBytes(content));
        Assert.Equal(GameStatus.DataMissing, GameFolder.Inspect(folder, _ => true).Status);
    }

    [Theory]
    [InlineData("p06020900.dat\r\nmerge.dat\r\n")]
    [InlineData("ï»¿# the archives\r\nmerge.dat\r\n")]
    [InlineData("merge.dat\r\n\u001A")]
    public void OneArchiveIsEnough(string list)
    {
        File.WriteAllBytes(Client, [1]);
        WriteArchive(Path.Combine(folder, "merge.dat"));
        WriteList(list);
        Assert.Equal(GameStatus.Ready, GameFolder.Inspect(folder, _ => true).Status);
    }

    [Fact]
    public void LocatorPrefersReadyFolder()
    {
        GameFolder Folder(string path, GameStatus status) => new(path, status, status == GameStatus.Ready ? path : null);
        var statuses = new Dictionary<string, GameStatus>
        {
            ["gone"] = GameStatus.NotFound,
            ["june"] = GameStatus.Unsupported,
            ["broken"] = GameStatus.DataMissing,
            ["empty"] = GameStatus.NoClient,
            ["february"] = GameStatus.Ready,
            ["second"] = GameStatus.Ready,
        };
        GameFolder Inspect(string path) => Folder(path, statuses[path]);
        Assert.Equal("february", GameLocator.Find(["gone", "june", "broken", "empty", "february", "second"], Inspect)?.Path);
        Assert.Equal("broken", GameLocator.Find(["june", "broken", "empty"], Inspect)?.Path);
        Assert.Equal("june", GameLocator.Find(["gone", "june", "empty"], Inspect)?.Path);
        Assert.Null(GameLocator.Find(["gone", "empty"], Inspect));
        // Unreadable folders are skipped.
        Assert.Equal("february", GameLocator.Find(["locked", "february"],
            path => path == "locked" ? throw new UnauthorizedAccessException() : Inspect(path))?.Path);
    }

    [Theory]
    [InlineData("merge.dat\r\n", new[] { "merge" })]
    [InlineData("merge.dat", new[] { "merge" })]
    [InlineData("DATA.dat\n", new[] { "DATA" })]
    [InlineData("# old patches\r\n\r\np06020900.dat\r\nmerge.dat\r\n", new[] { "p06020900", "merge" })]
    [InlineData("merge\r\n", new[] { "merge" })]
    [InlineData("merge.DAT\r\n", new[] { "merge.DAT" })]
    [InlineData("merge.dat.bak\r\n", new[] { "merge" })]
    [InlineData(" #merge.dat\r\n", new[] { " #merge" })]
    [InlineData("", new string[0])]
    public void ReadsPatchTxtLikeClient(string text, string[] archives) =>
        Assert.Equal(archives, GameFolder.Archives(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void CandidatesStartWithChosenFolder()
    {
        var candidates = GameLocator.Candidates(folder + Path.DirectorySeparatorChar).ToList();
        Assert.Equal(folder, candidates[0]);
        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(candidates, c => c.EndsWith(@"KONAMI\Yu-Gi-Oh! ONLINE", StringComparison.OrdinalIgnoreCase));
    }

    [RetailClientFact]
    public void RecognisesRetailClient() => Assert.True(SupportedClient.IsSupported(RetailClient.Path!));
}
