using Chaos.Game;
using Microsoft.Win32;

namespace Chaos.Tests;

public sealed class FolderMoveTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "chaos-move-" + Guid.NewGuid().ToString("N"));
    readonly string key = @"SOFTWARE\ChaosLauncherTests-" + Guid.NewGuid().ToString("N");

    string Old => Path.Combine(root, "BattleCity");
    string New => Path.Combine(root, "ChaosLauncher");

    public FolderMoveTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
        Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
    }

    static void Profile(string folder, string text)
    {
        Directory.CreateDirectory(Path.Combine(folder, "Saves"));
        File.WriteAllText(Path.Combine(folder, "Saves", "profile.dat"), text);
    }

    static string ReadProfile(string folder) => File.ReadAllText(Path.Combine(folder, "Saves", "profile.dat"));

    [Fact]
    public void RenamesOldFolder()
    {
        Profile(Old, "duelist");
        Assert.Equal(New, FolderMove.Migrate(Old, New));
        Assert.False(Directory.Exists(Old));
        Assert.Equal("duelist", ReadProfile(New));
    }

    [Fact]
    public void PrefersNewFolder()
    {
        Profile(Old, "old");
        Profile(New, "new");
        Assert.Equal(New, FolderMove.Migrate(Old, New));
        Assert.Equal("old", ReadProfile(Old));
        Assert.Equal("new", ReadProfile(New));
    }

    [Fact]
    public void ReplacesEmptyNewFolder()
    {
        Profile(Old, "duelist");
        Directory.CreateDirectory(New);
        Assert.Equal(New, FolderMove.Migrate(Old, New));
        Assert.False(Directory.Exists(Old));
        Assert.Equal("duelist", ReadProfile(New));
    }

    [Fact]
    public void KeepsEmptyNewFolder()
    {
        Directory.CreateDirectory(New);
        Assert.Equal(New, FolderMove.Migrate(Old, New));
        Assert.True(Directory.Exists(New));
    }

    [Fact]
    public void NothingToMove()
    {
        Profile(New, "new");
        Assert.Equal(New, FolderMove.Migrate(Old, New));
        Assert.False(Directory.Exists(Old));
        Assert.Equal("new", ReadProfile(New));
    }

    [Fact]
    public void CreatesNothingWithoutFolders()
    {
        Assert.Equal(New, FolderMove.Migrate(Old, New));
        Assert.False(Directory.Exists(Old));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public void FailedMoveKeepsOldFolder()
    {
        Profile(Old, "duelist");
        using var held = new FileStream(Path.Combine(Old, "Saves", "profile.dat"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(Old, FolderMove.Migrate(Old, New));
        Assert.False(Directory.Exists(New));
        Assert.True(Directory.Exists(Old));
    }

    [Fact]
    public void RetriesFailedMove()
    {
        Profile(Old, "duelist");
        var held = new FileStream(Path.Combine(Old, "Saves", "profile.dat"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(Old, FolderMove.Migrate(Old, New));
        held.Dispose();
        Assert.Equal(New, FolderMove.Migrate(Old, New));
        Assert.Equal("duelist", ReadProfile(New));
    }

    [Fact]
    public void MovesFolderBesideGame()
    {
        string game = Path.Combine(root, "Games", "Yu-Gi-Oh! ONLINE");
        string oldSaves = Path.Combine(root, "Games", "Battle City saves");
        string beside = Path.Combine(root, "Games", ClientKey.Fallback);
        Directory.CreateDirectory(game);
        Profile(oldSaves, "duelist");
        string? given = ClientKey.Prepare(Path.Combine(root, "Saves"), game, key, path => path == beside, _ => null);
        Assert.Equal(beside, given);
        Assert.False(Directory.Exists(oldSaves));
        Assert.Equal("duelist", ReadProfile(beside));
    }

    [Fact]
    public void FallbackNameIsAnsi() =>
        Assert.True(AnsiPath.Fits(Path.Combine(@"C:\Games", ClientKey.Fallback)));
}
