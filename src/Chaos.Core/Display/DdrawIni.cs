using System.Globalization;

namespace Chaos.Display;

public static class DdrawIni
{
    // The same for every resolution.
    static readonly string[] Common =
    [
        "renderer=auto",
        "d3d9_filter=2",
        "shader=Bicubic",
        "maintas=true",
        "boxing=false",
        "adjmouse=true",
        "maxfps=-1",
        "vsync=false",
        "devmode=false",
        "savesettings=0",
        "resizable=true",
        "anti_aliased_fonts_min_size=13",
        "center_window=1",
        "nonexclusive=true",
        "singlecpu=true",
        "noactivateapp=false",
        "maxgameticks=0",
        "minfps=0",
        "resolutions=0",
    ];

    public static string Build(Resolution resolution, WindowPlacement? window, string? screenshots)
    {
        var lines = new List<string>
        {
            "; Chaos Launcher writes this file every time it starts the game, so changes made here don't last.",
            "[ddraw]",
        };
        if (resolution.IsFullScreen)
        {
            // Native resolution: 0 x 0 lets cnc-ddraw decide.
            lines.AddRange(["fullscreen=true", "windowed=true", "border=false", "width=0", "height=0"]);
        }
        else if (resolution.IsWindow && window is { } at && (at.Width, at.Height) == (resolution.Width, resolution.Height))
        {
            lines.AddRange(["fullscreen=false", "windowed=true", "border=true",
                            Line("width", at.Width), Line("height", at.Height), Line("posX", at.X), Line("posY", at.Y)]);
        }
        else
        {
            throw new ArgumentException($"no ddraw.ini settings for {resolution}" +
                                        (resolution.IsWindow ? " without a window position" : ""));
        }
        lines.AddRange(Common);
        if (screenshots is not null)
            lines.Add($"screenshotdir={Path.TrimEndingDirectorySeparator(screenshots)}\\");
        return string.Join("\r\n", lines) + "\r\n";
    }

    static string Line(string key, int value) => key + "=" + value.ToString(CultureInfo.InvariantCulture);
}
