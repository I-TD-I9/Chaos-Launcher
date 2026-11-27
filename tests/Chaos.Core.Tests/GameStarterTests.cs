using Chaos.Client;
using Chaos.Display;
using Chaos.Game;

namespace Chaos.Tests;

public class GameStarterTests
{
    [Fact]
    public void PrepareRejectsUnreadyFolder()
    {
        string empty = Path.Combine(Path.GetTempPath(), "bc-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                GameStarter.Prepare(GameFolder.Inspect(empty), ServerEndpoints.Public, empty));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    const string Client = @"C:\Launcher\online_pc.exe";
    const string Working = @"C:\Launcher\Game";

    [Fact]
    public void StartsInGivenFolder()
    {
        var info = GameStarter.StartInfo(Client, Working, Resolution.Default, @"C:\Launcher");
        Assert.Equal((Client, Working, "-win"), (info.FileName, info.WorkingDirectory, info.Arguments));
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void KeepsLauncherWorkingFolder()
    {
        Assert.Equal(Path.Combine(LauncherSettings.Folder, "Game"), WorkingFolder.Path);
        Assert.Equal("bc", WorkingFolder.Overrides);
    }

    [Theory]
    [InlineData("default", "-win")]
    [InlineData("1024x768", "-32")]
    [InlineData("1600x1200", "-32")]
    [InlineData("full", "-32")]
    public void ResolutionPicksSwitch(string resolution, string arguments) =>
        Assert.Equal(arguments, GameStarter.StartInfo(Client, Working, Resolution.Parse(resolution), @"C:\Launcher").Arguments);

    [Fact]
    public void StartsByAnsiPath()
    {
        const string Long = @"C:\Users\Дмитрий\AppData\Local\ChaosLauncher";
        var info = GameStarter.StartInfo(Long + @"\online_pc.exe", Working, Resolution.FullScreen, @"C:\Users\8D0B~1\AppData\Local\CHAOSL~1");
        Assert.Equal(@"C:\Users\8D0B~1\AppData\Local\CHAOSL~1\online_pc.exe", info.FileName);
        Assert.Equal(@"C:\Users\8D0B~1\AppData\Local\CHAOSL~1\ddraw.ini", info.Environment[CncDdraw.ConfigFileVariable]);
    }

    [Theory]
    [InlineData("1600x1200")]
    [InlineData("full")]
    public void PassesDdrawConfigPath(string resolution) =>
        Assert.Equal(@"C:\Launcher\ddraw.ini",
                     GameStarter.StartInfo(Client, Working, Resolution.Parse(resolution), @"C:\Launcher").Environment[CncDdraw.ConfigFileVariable]);

    [Fact]
    public void IgnoresPlayerDdrawConfig()
    {
        string? before = Environment.GetEnvironmentVariable(CncDdraw.ConfigFileVariable);
        try
        {
            Environment.SetEnvironmentVariable(CncDdraw.ConfigFileVariable, @"D:\Other game\ddraw.ini");
            Assert.False(GameStarter.StartInfo(Client, Working, Resolution.Default, @"C:\Launcher").Environment
                                    .ContainsKey(CncDdraw.ConfigFileVariable));
            Assert.Equal(@"C:\Launcher\ddraw.ini", GameStarter.StartInfo(Client, Working, Resolution.FullScreen, @"C:\Launcher")
                                                               .Environment[CncDdraw.ConfigFileVariable]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CncDdraw.ConfigFileVariable, before);
        }
    }

    [Fact]
    public void StartsWindowedWithoutHome()
    {
        var info = GameStarter.StartInfo(Client, Working, Resolution.FullScreen, null);
        Assert.Equal((Client, "-win"), (info.FileName, info.Arguments));
        Assert.False(info.Environment.ContainsKey(CncDdraw.ConfigFileVariable));
    }

    [RetailClientFact]
    public void PrepareWritesCopyOnce()
    {
        string retailFolder = Path.Combine(Path.GetTempPath(), "bc-game-" + Guid.NewGuid().ToString("N"));
        string copies = Path.Combine(Path.GetTempPath(), "bc-copies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(retailFolder);
        try
        {
            File.Copy(RetailClient.Path!, Path.Combine(retailFolder, SupportedClient.FileName));
            GameFolderTests.WriteData(retailFolder);
            var game = GameFolder.Inspect(retailFolder);
            Assert.Equal(GameStatus.Ready, game.Status);

            string client = GameStarter.Prepare(game, ServerEndpoints.Public, copies);
            Assert.Equal(Path.Combine(copies, GameStarter.ClientName), client);
            Assert.Equal(SupportedClient.PublicSha256, SupportedClient.Sha256Of(client));

            // Only rewritten when it differs.
            var written = File.GetLastWriteTimeUtc(client);
            File.SetLastWriteTimeUtc(client, written.AddHours(-1));
            GameStarter.Prepare(game, ServerEndpoints.Public, copies);
            Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(client));
            File.WriteAllBytes(client, [1, 2, 3]);
            GameStarter.Prepare(game, ServerEndpoints.Public, copies);
            Assert.Equal(SupportedClient.PublicSha256, SupportedClient.Sha256Of(client));
            Assert.False(File.Exists(client + ".new"));

            File.WriteAllBytes(client, [1, 2, 3]);
            using (new FileStream(client, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var refused = Record.Exception(() => GameStarter.Prepare(game, ServerEndpoints.Public, copies));
                Assert.True(refused is IOException or UnauthorizedAccessException, refused?.ToString());
            }
            Assert.False(File.Exists(client + ".new"));
        }
        finally
        {
            Directory.Delete(retailFolder, recursive: true);
            if (Directory.Exists(copies))
                Directory.Delete(copies, recursive: true);
        }
    }
}
