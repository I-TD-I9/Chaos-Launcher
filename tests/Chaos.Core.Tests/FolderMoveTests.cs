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

    static string ProfileIn(string folder) => File.ReadAllText(Path.Combine(folder, "Saves", "profile.dat"));

    [Fact]
    public void The_old_folder_is_renamed_with_everything_in_it()
    {
        Profile(Old, "duelist");
        Assert.Equal(New, FolderMove.Adopt(Old, New));
        Assert.False(Directory.Exists(Old));
        Assert.Equal("duelist", ProfileIn(New));
    }

    [Fact]
    public void With_both_folders_there_the_new_one_is_used_and_the_old_one_left_alone()
    {
        Profile(Old, "old");
        Profile(New, "new");
        Assert.Equal(New, FolderMove.Adopt(Old, New));
        Assert.Equal("old", ProfileIn(Old));
        Assert.Equal("new", ProfileIn(New));
    }

    [Fact]
    public void An_empty_folder_under_the_new_name_does_not_keep_the_old_one_behind()
    {
        Profile(Old, "duelist");
        Directory.CreateDirectory(New);
        Assert.Equal(New, FolderMove.Adopt(Old, New));
        Assert.False(Directory.Exists(Old));
        Assert.Equal("duelist", ProfileIn(New));
    }

    [Fact]
    public void An_empty_new_folder_with_no_old_one_stays()
    {
        Directory.CreateDirectory(New);
        Assert.Equal(New, FolderMove.Adopt(Old, New));
        Assert.True(Directory.Exists(New));
    }

    [Fact]
    public void With_only_the_new_folder_there_is_nothing_to_do()
    {
        Profile(New, "new");
        Assert.Equal(New, FolderMove.Adopt(Old, New));
        Assert.False(Directory.Exists(Old));
        Assert.Equal("new", ProfileIn(New));
    }

    [Fact]
    public void With_neither_folder_nothing_is_made()
    {
        Assert.Equal(New, FolderMove.Adopt(Old, New));
        Assert.False(Directory.Exists(Old));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public void A_move_that_fails_leaves_the_old_folder_in_use()
    {
        Profile(Old, "duelist");
        // A file open without sharing, as the game holds its files, keeps the folder from being renamed.
        using var held = new FileStream(Path.Combine(Old, "Saves", "profile.dat"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(Old, FolderMove.Adopt(Old, New));
        Assert.False(Directory.Exists(New));
        Assert.True(Directory.Exists(Old));
    }

    [Fact]
    public void A_failed_move_is_tried_again_at_the_next_start()
    {
        Profile(Old, "duelist");
        var held = new FileStream(Path.Combine(Old, "Saves", "profile.dat"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(Old, FolderMove.Adopt(Old, New));
        held.Dispose();
        Assert.Equal(New, FolderMove.Adopt(Old, New));
        Assert.Equal("duelist", ProfileIn(New));
    }

    [Fact]
    public void The_folder_beside_the_game_is_moved_to_its_new_name_before_it_is_used()
    {
        string game = Path.Combine(root, "Games", "Yu-Gi-Oh! ONLINE");
        string oldSaves = Path.Combine(root, "Games", "Battle City saves");
        string beside = Path.Combine(root, "Games", ClientKey.Fallback);
        Directory.CreateDirectory(game);
        Profile(oldSaves, "duelist");
        string? given = ClientKey.Prepare(Path.Combine(root, "Saves"), game, key, path => path == beside, _ => null);
        Assert.Equal(beside, given);
        Assert.False(Directory.Exists(oldSaves));
        Assert.Equal("duelist", ProfileIn(beside));
    }

    [Fact]
    public void The_fallback_folders_name_is_one_the_client_can_open() =>
        Assert.True(AnsiPath.Fits(Path.Combine(@"C:\Games", ClientKey.Fallback)));
}
