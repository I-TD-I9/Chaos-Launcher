using Chaos.Game;
using Microsoft.Win32;

namespace Chaos.Tests;

public sealed class ClientKeyTests : IDisposable
{
    readonly string root = @"SOFTWARE\ChaosLauncherTests-" + Guid.NewGuid().ToString("N");
    readonly string folder = Path.Combine(Path.GetTempPath(), "bc-saves-" + Guid.NewGuid().ToString("N"));

    string key => root + @"\system";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    string? Value(string name)
    {
        using var system = Registry.CurrentUser.OpenSubKey(key);
        return system?.GetValue(name) as string;
    }

    string Saves => Path.Combine(folder, "Saves");
    string Game => Path.Combine(folder, "Games", "Yu-Gi-Oh! ONLINE");

    [Fact]
    public void CreatesFolderAndSetsKey()
    {
        Assert.Equal(Saves, ClientKey.Prepare(Saves, Game, key));
        Assert.True(Directory.Exists(Saves));
        Assert.Equal(Saves, Value("CommonDir"));
    }

    [Fact]
    public void ReleaseMatchesNoEdition()
    {
        ClientKey.Prepare(Saves, Game, key);
        Assert.Equal("koe", Value("Release"));
        foreach (string edition in new[] { "matel", "v-jump", "us-jump", "kmj-release", "korea" })
            Assert.False(edition.StartsWith(ClientKey.Release, StringComparison.Ordinal), edition);
    }

    [Fact]
    public void OverwritesOldFolder()
    {
        using (var system = Registry.CurrentUser.CreateSubKey(key))
            system.SetValue("CommonDir", @"C:\Program Files (x86)\KONAMI\Yu-Gi-Oh! ONLINE");
        ClientKey.Prepare(Saves, Game, key);
        Assert.Equal(Saves, Value("CommonDir"));
    }

    [Fact]
    public void UsesShortName()
    {
        string? given = ClientKey.Prepare(Saves, Game, key, path => path == @"C:\SAVES~1", _ => @"C:\SAVES~1");
        Assert.Equal(@"C:\SAVES~1", given);
        Assert.Equal(@"C:\SAVES~1", Value("CommonDir"));
    }

    [Fact]
    public void SavesBesideGameWithoutShortName()
    {
        string beside = Path.Combine(folder, "Games", ClientKey.Fallback);
        string? given = ClientKey.Prepare(Saves, Game, key, path => path == beside, _ => null);
        Assert.Equal(beside, given);
        Assert.True(Directory.Exists(beside));
        Assert.Equal(beside, Value("CommonDir"));
    }

    [Fact]
    public void ClearsFolderWhenNoneFits()
    {
        using (var system = Registry.CurrentUser.CreateSubKey(key))
            system.SetValue("CommonDir", Saves);
        Assert.Null(ClientKey.Prepare(Saves, Game, key, _ => false, _ => null));
        Assert.Null(Value("CommonDir"));
        Assert.Equal("koe", Value("Release"));
    }

    [Fact]
    public void SavesInLauncherFolder() =>
        Assert.Equal(Path.Combine(LauncherSettings.Folder, "Saves"), ClientKey.SaveFolder);
}
