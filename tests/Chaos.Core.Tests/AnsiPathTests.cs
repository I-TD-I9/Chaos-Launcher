using Chaos.Game;

namespace Chaos.Tests;

public sealed class AnsiPathTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "bc-ansi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void PlainPathsFit() =>
        Assert.True(AnsiPath.Fits(@"C:\Users\Duelist\AppData\Local\ChaosLauncher\Saves"));

    [Fact]
    public void UnspellablePathDoesNotFit() =>
        // Mixed scripts no single ANSI code page can hold.
        Assert.False(AnsiPath.Fits(@"C:\Users\Иван 田中 Ελένη\Saves"));

    [Fact]
    public void CodePageThrowsOnUnspellable() =>
        Assert.ThrowsAny<ArgumentException>(() => AnsiPath.CodePage().GetBytes("Иван 田中 Ελένη"));

    [Fact]
    public void CreatesFolderThatFits()
    {
        string saves = Path.Combine(folder, "Saves");
        Assert.Equal(saves, AnsiPath.Folder(saves, _ => true, _ => null));
        Assert.True(Directory.Exists(saves));
    }

    [Fact]
    public void ShortNameForFolderThatDoesNotFit()
    {
        string saves = Path.Combine(folder, "Saves");
        Assert.Equal(@"C:\SAVES~1", AnsiPath.Folder(saves, path => path == @"C:\SAVES~1", _ => @"C:\SAVES~1"));
        Assert.Null(AnsiPath.Folder(saves, _ => false, _ => @"C:\SAVES~1"));
        Assert.Null(AnsiPath.Folder(saves, _ => false, _ => null));
    }

    [Fact]
    public void NoShortNameForMissingPath() =>
        Assert.Null(AnsiPath.ShortName(Path.Combine(folder, "nowhere")));
}
