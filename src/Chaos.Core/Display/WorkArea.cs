using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Chaos.Display;

/// <summary>Window frame size on each side of the client area.</summary>
public readonly record struct WindowFrame(int Left, int Top, int Right, int Bottom)
{
    public int Width => Left + Right;
    public int Height => Top + Bottom;
}

/// <summary>The game window's client area, in physical pixels.</summary>
public readonly record struct WindowPlacement(int X, int Y, int Width, int Height);

public readonly record struct WorkArea(int Left, int Top, int Right, int Bottom, WindowFrame Frame)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public static WorkArea FromWindow(IntPtr window)
    {
        IntPtr monitor = window != IntPtr.Zero
            ? MonitorFromWindow(window, MonitorDefaultToNearest)
            : MonitorFromPoint(default, MonitorDefaultToPrimary);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            throw new Win32Exception("could not find the monitor");
        if (GetDpiForMonitor(monitor, EffectiveDpi, out uint dpi, out _) != 0 || dpi == 0)
            dpi = 96;
        var frame = new Rect();
        if (!AdjustWindowRectExForDpi(ref frame, OverlappedWindow, false, 0, dpi))
        {
            // Windows 10 frame at 100%, scaled for this DPI.
            int side = (int)(8 * dpi / 96), top = (int)(31 * dpi / 96);
            frame = new Rect { Left = -side, Top = -top, Right = side, Bottom = side };
        }
        return new WorkArea(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom,
                            new WindowFrame(-frame.Left, -frame.Top, frame.Right, frame.Bottom));
    }

    public bool Fits(Resolution resolution) =>
        resolution.IsWindow && resolution.Width + Frame.Width <= Width && resolution.Height + Frame.Height <= Height;

    public IReadOnlyList<Resolution> Choices(bool scaled = true) =>
        scaled ? [Resolution.Default, .. Resolution.Windows.Where(Fits), Resolution.FullScreen] : [Resolution.Default];

    public Resolution Resolve(Resolution chosen, bool scaled = true) =>
        !scaled ? Resolution.Default
        : !chosen.IsWindow || Fits(chosen) ? chosen
        : Resolution.Windows.Where(Fits).DefaultIfEmpty(Resolution.Default).Last();

    public WindowPlacement Place(Resolution window)
    {
        int x = Left + Math.Max((Width - Frame.Width - window.Width) / 2, 0) + Frame.Left;
        int y = Top + Math.Max((Height - Frame.Height - window.Height) / 2, 0) + Frame.Top;
        return new WindowPlacement(x, y, window.Width, window.Height);
    }

    const uint MonitorDefaultToPrimary = 1, MonitorDefaultToNearest = 2;
    const int EffectiveDpi = 0;
    const uint OverlappedWindow = 0x00CF0000;

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    static extern bool AdjustWindowRectExForDpi(ref Rect rect, uint style, bool menu, uint exStyle, uint dpi);
}
