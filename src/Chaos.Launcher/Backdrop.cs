using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Chaos.Launcher;

static class Backdrop
{
    // Windows 11 22H2, the first with system backdrops.
    const int MinBuild = 22621;

    const int DwmwaUseImmersiveDarkMode = 20;
    const int DwmwaWindowCornerPreference = 33;
    const int DwmwaSystemBackdropType = 38;
    const int DwmwcpRound = 2;
    const int DwmsbtTransientWindow = 3;

    const int GwlStyle = -16;
    const int WsSysMenu = 0x00080000;
    const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    public static void HideCaptionButtons(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        int style = GetWindowLong(handle, GwlStyle);
        if ((style & WsSysMenu) == 0)
            return;
        SetWindowLong(handle, GwlStyle, style & ~WsSysMenu);
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    public static bool TryAcrylic(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || Environment.OSVersion.Version.Build < MinBuild || !TransparencyEnabled())
            return false;
        // Cosmetic, so failures are ignored.
        SetAttribute(handle, DwmwaUseImmersiveDarkMode, 1);
        SetAttribute(handle, DwmwaWindowCornerPreference, DwmwcpRound);
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is not { } target)
            return false;
        if (!SetAttribute(handle, DwmwaSystemBackdropType, DwmsbtTransientWindow))
            return false;
        target.BackgroundColor = Colors.Transparent;
        return true;
    }

    // Settings > Personalization > Colors > Transparency effects.
    static bool TransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    static bool SetAttribute(IntPtr window, int attribute, int value) =>
        DwmSetWindowAttribute(window, attribute, ref value, sizeof(int)) == 0;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
