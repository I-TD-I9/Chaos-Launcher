using System.Text;
using Chaos.Client;
using Chaos.Display;

namespace Chaos.Tests;

public sealed class CncDdrawTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "bc-ddraw-" + Guid.NewGuid().ToString("N"));
    static readonly Resolution Twice = Resolution.Parse("1600x1200");
    static readonly WindowPlacement TwiceOn1440p = new(480, 111, 1600, 1200);

    string Dll => Path.Combine(folder, CncDdraw.DllName);
    string Ini => Path.Combine(folder, CncDdraw.IniName);
    string License => Path.Combine(folder, CncDdraw.LicenseName);

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void CarriesPinnedRelease()
    {
        var dll = CncDdraw.Dll();
        Assert.Equal(CncDdraw.Length, dll.Length);
        Assert.Equal("85e0f7d530dfda134793a57cb3e76b0287dcc96892ee57162dd68f47283b03a9", SupportedClient.Sha256Of(dll));
        Assert.Equal(CncDdraw.Sha256, SupportedClient.Sha256Of(dll));
        // 32-bit, like the game (PeImage only reads PE32).
        _ = new PeImage(dll);
    }

    [Fact]
    public void CarriesLicence()
    {
        string license = Encoding.UTF8.GetString(CncDdraw.License()).ReplaceLineEndings("\n");
        Assert.Equal("a705144535bbfccc05e8310c4f8ab5f0b6f0ec2c9371b76ca5a522cf90e031f5",
                     SupportedClient.Sha256Of(Encoding.UTF8.GetBytes(license)));
        Assert.StartsWith("MIT License\n\nCopyright (c) 2022 github.com/FunkyFr3sh\n", license);
        // Same text as the notices file.
        Assert.Contains(license, File.ReadAllText(RepositoryFile("THIRD-PARTY-NOTICES.md")).ReplaceLineEndings("\n"));
    }

    static string RepositoryFile(string name)
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
            if (File.Exists(Path.Combine(at.FullName, "ChaosLauncher.slnx")))
                return Path.Combine(at.FullName, name);
        throw new FileNotFoundException("no repository above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void WindowWritesWrapper()
    {
        CncDdraw.Prepare(Twice, folder, TwiceOn1440p);
        Assert.Equal(CncDdraw.Sha256, SupportedClient.Sha256Of(Dll));
        Assert.Equal(CncDdraw.License(), File.ReadAllBytes(License));
        Assert.Equal(DdrawIni.Build(Twice, TwiceOn1440p, Path.Combine(folder, CncDdraw.Screenshots)),
                     File.ReadAllText(Ini, Encoding.Latin1));
        Assert.True(Directory.Exists(Path.Combine(folder, CncDdraw.Screenshots)));
        Assert.Empty(Directory.GetFiles(folder, "*.new"));
    }

    [Fact]
    public void FullScreenWritesWrapper()
    {
        CncDdraw.Prepare(Resolution.FullScreen, folder, null);
        Assert.Equal(CncDdraw.Sha256, SupportedClient.Sha256Of(Dll));
        Assert.Equal(DdrawIni.Build(Resolution.FullScreen, null, Path.Combine(folder, CncDdraw.Screenshots)),
                     File.ReadAllText(Ini, Encoding.Latin1));
    }

    [Fact]
    public void WritesOnlyWhenChanged()
    {
        CncDdraw.Prepare(Resolution.FullScreen, folder, null);
        var written = File.GetLastWriteTimeUtc(Dll).AddHours(-1);
        File.SetLastWriteTimeUtc(Dll, written);
        CncDdraw.Prepare(Twice, folder, TwiceOn1440p);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Dll));
        Assert.Contains("width=1600\r\n", File.ReadAllText(Ini));

        File.WriteAllBytes(Dll, [1, 2, 3]);
        CncDdraw.Prepare(Resolution.FullScreen, folder, null);
        Assert.Equal(CncDdraw.Sha256, SupportedClient.Sha256Of(Dll));
        Assert.Contains("width=0\r\n", File.ReadAllText(Ini));
    }

    [Fact]
    public void DefaultRemovesWrapper()
    {
        CncDdraw.Prepare(Twice, folder, TwiceOn1440p);
        CncDdraw.Prepare(Resolution.Default, folder, null);
        Assert.False(File.Exists(Dll));
        Assert.False(File.Exists(Ini));
        Assert.False(File.Exists(License));
        // Also fine with nothing to remove.
        CncDdraw.Prepare(Resolution.Default, folder, null);
        CncDdraw.Prepare(Resolution.Default, Path.Combine(folder, "nowhere"), null);
    }

    [Fact]
    public void FailedSettingsLeaveNoWrapper()
    {
        Assert.Throws<ArgumentException>(() => CncDdraw.Prepare(Twice, folder, null));
        Assert.False(File.Exists(Dll));
        Assert.False(File.Exists(Ini));
    }

    [Fact]
    public void ReportsWrapperInUse()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Dll, [1, 2, 3]);
        using (new FileStream(Dll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var replacing = Record.Exception(() => CncDdraw.Prepare(Resolution.FullScreen, folder, null));
            Assert.True(replacing is IOException or UnauthorizedAccessException, replacing?.ToString());
            var removing = Record.Exception(() => CncDdraw.Prepare(Resolution.Default, folder, null));
            Assert.True(removing is IOException or UnauthorizedAccessException, removing?.ToString());
        }
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Dll));
        Assert.Empty(Directory.GetFiles(folder, "*.new"));
        Assert.False(File.Exists(Ini));
    }
}
