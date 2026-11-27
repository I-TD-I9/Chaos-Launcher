using System.Globalization;
using Chaos.Display;

namespace Chaos.Tests;

public class DdrawIniTests
{
    static readonly Resolution Twice = Resolution.Parse("1600x1200");
    static readonly WindowPlacement TwiceOn1440p = new(480, 111, 1600, 1200);

    static Dictionary<string, string> Keys(string ini)
    {
        var keys = new Dictionary<string, string>();
        foreach (var line in ini.Split("\r\n"))
        {
            if (line.Length == 0 || line[0] is ';' or '[')
                continue;
            int equals = line.IndexOf('=');
            Assert.True(equals > 0, line);
            Assert.True(keys.TryAdd(line[..equals], line[(equals + 1)..]), $"{line[..equals]} twice");
        }
        return keys;
    }

    // Shared by every resolution.
    static readonly Dictionary<string, string> Common = new()
    {
        ["renderer"] = "auto", ["d3d9_filter"] = "2", ["shader"] = "Bicubic", ["maintas"] = "true",
        ["boxing"] = "false", ["adjmouse"] = "true", ["maxfps"] = "-1", ["vsync"] = "false", ["devmode"] = "false",
        ["savesettings"] = "0", ["resizable"] = "true", ["anti_aliased_fonts_min_size"] = "13", ["center_window"] = "1",
        ["nonexclusive"] = "true", ["singlecpu"] = "true", ["noactivateapp"] = "false", ["maxgameticks"] = "0",
        ["minfps"] = "0", ["resolutions"] = "0",
    };

    [Fact]
    public void FullScreenIsBorderless()
    {
        var keys = Keys(DdrawIni.Build(Resolution.FullScreen, null, null));
        Assert.Equal(("true", "true", "false", "0", "0"),
                     (keys["fullscreen"], keys["windowed"], keys["border"], keys["width"], keys["height"]));
        Assert.False(keys.ContainsKey("posX"));
        Assert.All(Common, pair => Assert.Equal(pair.Value, keys[pair.Key]));
        Assert.Equal(Common.Count + 5, keys.Count);
    }

    [Fact]
    public void WindowHasBorderSizeAndPlace()
    {
        var keys = Keys(DdrawIni.Build(Twice, TwiceOn1440p, null));
        Assert.Equal(("false", "true", "true", "1600", "1200", "480", "111"),
                     (keys["fullscreen"], keys["windowed"], keys["border"], keys["width"], keys["height"], keys["posX"], keys["posY"]));
        Assert.All(Common, pair => Assert.Equal(pair.Value, keys[pair.Key]));
        Assert.Equal(Common.Count + 7, keys.Count);
    }

    [Theory]
    [InlineData("1024x768")]
    [InlineData("1400x1050")]
    [InlineData("3200x2400")]
    public void EachWindowGetsItsSize(string name)
    {
        var resolution = Resolution.Parse(name);
        var keys = Keys(DdrawIni.Build(resolution, new WindowPlacement(10, 40, resolution.Width, resolution.Height), null));
        Assert.Equal(name, $"{keys["width"]}x{keys["height"]}");
    }

    [Fact]
    public void OneSectionInCrlf()
    {
        string ini = DdrawIni.Build(Resolution.FullScreen, null, null);
        Assert.EndsWith("\r\n", ini);
        Assert.DoesNotContain("\n", ini.Replace("\r\n", ""));
        Assert.DoesNotContain("\r", ini.Replace("\r\n", ""));
        Assert.Single(ini.Split("\r\n"), line => line.StartsWith('['));
        Assert.Contains("\r\n[ddraw]\r\n", ini);
        Assert.True(ini.All(char.IsAscii));
    }

    [Fact]
    public void ScreenshotFolderEndsInBackslash()
    {
        Assert.Equal(@"C:\Users\Duelist\AppData\Local\ChaosLauncher\Screenshots\",
                     Keys(DdrawIni.Build(Resolution.FullScreen, null, @"C:\Users\Duelist\AppData\Local\ChaosLauncher\Screenshots"))["screenshotdir"]);
        Assert.Equal(@"C:\CHAOSL~1\SCREEN~1\",
                     Keys(DdrawIni.Build(Twice, TwiceOn1440p, @"C:\CHAOSL~1\SCREEN~1\"))["screenshotdir"]);
        // No usable folder: cnc-ddraw keeps its default.
        Assert.False(Keys(DdrawIni.Build(Resolution.FullScreen, null, null)).ContainsKey("screenshotdir"));
    }

    [Fact]
    public void WritesNegativePosition()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            // Like Swedish, which has its own minus sign.
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NegativeSign = "\u2212";
            CultureInfo.CurrentCulture = culture;
            var keys = Keys(DdrawIni.Build(Twice, new WindowPlacement(-1600, -12, 1600, 1200), null));
            Assert.Equal(("-1600", "-12"), (keys["posX"], keys["posY"]));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void RejectsDefaultAndUnplacedWindow()
    {
        Assert.Throws<ArgumentException>(() => DdrawIni.Build(Resolution.Default, TwiceOn1440p, null));
        Assert.Throws<ArgumentException>(() => DdrawIni.Build(Twice, null, null));
        // Placement for a different size.
        Assert.Throws<ArgumentException>(() => DdrawIni.Build(Resolution.Parse("1280x960"), TwiceOn1440p, null));
    }
}
