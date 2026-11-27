using System.Text.Json;

namespace Chaos.Tests;

public class ReleasesTests
{
    static LauncherRelease? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Releases.Parse(document.RootElement);
    }

    [Fact]
    public void ParsesRelease()
    {
        var release = Parse("""
            {"tag_name": "v1.2.3",
             "html_url": "https://github.com/I-TD-I9/Chaos-Launcher/releases/tag/v1.2.3"}
            """);
        Assert.Equal(new Version(1, 2, 3), release!.Version);
        Assert.Equal("https://github.com/I-TD-I9/Chaos-Launcher/releases/tag/v1.2.3", release.Page.AbsoluteUri);
    }

    [Fact]
    public void TwoPartTagIsPatchZero() =>
        Assert.Equal(new Version(2, 0, 0),
                     Parse("""{"tag_name": "2.0", "html_url": "https://github.com/I-TD-I9/Chaos-Launcher/releases/tag/2.0"}""")!.Version);

    [Theory]
    [InlineData("""{"tag_name": "v1.0.0", "html_url": "https://example.com/I-TD-I9/Chaos-Launcher/releases"}""")]
    [InlineData("""{"tag_name": "v1.0.0", "html_url": "https://github.com/someone-else/Chaos-Launcher/releases"}""")]
    [InlineData("""{"tag_name": "latest", "html_url": "https://github.com/I-TD-I9/Chaos-Launcher/releases"}""")]
    [InlineData("""{"message": "Not Found"}""")]
    public void RejectsBadRelease(string json) => Assert.Null(Parse(json));

    [Fact]
    public void CurrentHasThreeParts() => Assert.Equal(-1, Releases.Current.Revision);

    const string Download = "https://github.com/I-TD-I9/Chaos-Launcher/releases/download/v1.2.3/";
    const string Digest = "sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef";

    static string Asset(string name, string url, long size = 1000, string? digest = null) =>
        $$"""{"name": "{{name}}", "browser_download_url": "{{url}}", "size": {{size}}{{(digest is null ? "" : $", \"digest\": \"{digest}\"")}}}""";

    static LauncherRelease? WithAssets(params string[] assets) => Parse($$"""
        {"tag_name": "v1.2.3",
         "html_url": "https://github.com/I-TD-I9/Chaos-Launcher/releases/tag/v1.2.3",
         "assets": [{{string.Join(", ", assets)}}]}
        """);

    [Fact]
    public void FindsExeAndChecksum()
    {
        var release = WithAssets(Asset("THIRD-PARTY-NOTICES.md", Download + "THIRD-PARTY-NOTICES.md"),
                                 Asset("ChaosLauncher.exe", Download + "ChaosLauncher.exe", 70_000_000, Digest),
                                 Asset("ChaosLauncher.exe.sha256", Download + "ChaosLauncher.exe.sha256", 86))!;
        Assert.True(release.CanSelfUpdate);
        Assert.Equal(new ReleaseAsset("ChaosLauncher.exe", new Uri(Download + "ChaosLauncher.exe"), 70_000_000,
                                      "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"), release.Exe);
        Assert.Equal(86, release.Checksum!.Size);
        Assert.Null(release.Checksum.Sha256);
    }

    [Theory]
    [InlineData("https://github.com/someone-else/Chaos-Launcher/releases/download/v1.2.3/ChaosLauncher.exe")]
    [InlineData("https://github.com/I-TD-I9/Chaos-Launcher/releases/download/v1.2.2/ChaosLauncher.exe")]
    [InlineData("http://github.com/I-TD-I9/Chaos-Launcher/releases/download/v1.2.3/ChaosLauncher.exe")]
    [InlineData("https://objects.githubusercontent.com/I-TD-I9/Chaos-Launcher/releases/download/v1.2.3/ChaosLauncher.exe")]
    [InlineData("https://github.com/I-TD-I9/Chaos-Launcher/releases/download/v1.2.3/../v1.2.2/ChaosLauncher.exe")]
    [InlineData("https://github.com/I-TD-I9/Chaos-Launcher/releases/download/v1.2.3/ChaosLauncher.exe?x=1")]
    public void RejectsForeignUrl(string url)
    {
        var release = WithAssets(Asset("ChaosLauncher.exe", url),
                                 Asset("ChaosLauncher.exe.sha256", Download + "ChaosLauncher.exe.sha256", 86))!;
        Assert.Null(release.Exe);
        Assert.False(release.CanSelfUpdate);
    }

    [Fact]
    public void RejectsOtherName()
    {
        var release = WithAssets(Asset("Setup.exe", Download + "Setup.exe"),
                                 Asset("ChaosLauncher.exe.sha256", Download + "ChaosLauncher.exe.sha256", 86))!;
        Assert.Null(release.Exe);
        // Right name, wrong URL.
        Assert.Null(WithAssets(Asset("ChaosLauncher.exe", Download + "Setup.exe"))!.Exe);
    }

    [Fact]
    public void NeedsChecksum()
    {
        var release = WithAssets(Asset("ChaosLauncher.exe", Download + "ChaosLauncher.exe"))!;
        Assert.NotNull(release.Exe);
        Assert.Null(release.Checksum);
        Assert.False(release.CanSelfUpdate);
        Assert.False(Parse("""{"tag_name": "v1.2.3", "html_url": "https://github.com/I-TD-I9/Chaos-Launcher/releases/tag/v1.2.3"}""")!.CanSelfUpdate);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(300_000_000, null)]
    [InlineData(1000, "sha256:abc")]
    public void RejectsBadSizeOrDigest(long size, string? digest) =>
        Assert.Null(WithAssets(Asset("ChaosLauncher.exe", Download + "ChaosLauncher.exe", size, digest))!.Exe);

    [Fact]
    public void IgnoresOtherDigest() =>
        Assert.Null(WithAssets(Asset("ChaosLauncher.exe", Download + "ChaosLauncher.exe", 1000, "sha512:abcd"))!.Exe!.Sha256);

    [Fact]
    public void NoAssetsWithoutV()
    {
        var release = Parse($$"""
            {"tag_name": "1.2.3",
             "html_url": "https://github.com/I-TD-I9/Chaos-Launcher/releases/tag/1.2.3",
             "assets": [{{Asset("ChaosLauncher.exe", "https://github.com/I-TD-I9/Chaos-Launcher/releases/download/1.2.3/ChaosLauncher.exe")}}]}
            """)!;
        Assert.Equal(new Version(1, 2, 3), release.Version);
        Assert.Null(release.Exe);
    }
}
