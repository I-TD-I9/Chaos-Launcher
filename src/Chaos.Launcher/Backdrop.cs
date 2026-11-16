using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Chaos.Launcher;

/// <summary>
/// The window's see-through backdrop: Windows 11's acrylic behind the whole window, where Windows
/// gives it. Anywhere else the window lays its own solid navy under the same layers.
/// </summary>
/// <remarks>
/// Windows draws a system backdrop only in a window's frame, so the window keeps the frame of an
/// ordinary captioned window (WS_CAPTION) and its WindowChrome turns that frame into glass over
/// the whole of it. A window without a caption gets no backdrop at all, whatever it asks for.
/// </remarks>
static class Backdrop
{
    // Windows 11 22H2, the first to take a system backdrop for a window of its own.
    const int FirstBuild = 22621;

    const int DwmwaUseImmersiveDarkMode = 20;
    const int DwmwaWindowCornerPreference = 33;
    const int DwmwaSystemBackdropType = 38;
    const int DwmwcpRound = 2;
    const int DwmsbtTransientWindow = 3;

    const int GwlStyle = -16;
    const int WsSysMenu = 0x00080000;
    const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    /// <summary>
    /// Takes the system menu off the window's style, and with it the minimize and close buttons
    /// Windows would draw at the top right of the glass: the window has its own there.
    /// </summary>
    public static void HideCaptionButtons(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        int style = GetWindowLong(handle, GwlStyle);
        if ((style & WsSysMenu) == 0)
            return;
        SetWindowLong(handle, GwlStyle, style & ~WsSysMenu);
        // Windows holds on to parts of the old style until the frame is told it has changed.
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    /// <summary>
    /// Asks Windows for the dark frame, round corners and the acrylic backdrop, and says whether
    /// the acrylic is on: only on 22H2 or later with transparency effects on. The window's
    /// background is then left transparent so it shows.
    /// </summary>
    public static bool TryAcrylic(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || Environment.OSVersion.Version.Build < FirstBuild || !TransparencyOn())
            return false;
        // The dark frame and round corners are only for looks: the acrylic doesn't hang on them.
        Set(handle, DwmwaUseImmersiveDarkMode, 1);
        Set(handle, DwmwaWindowCornerPreference, DwmwcpRound);
        // The backdrop comes last, once it is sure the window can be made transparent for it.
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is not { } target)
            return false;
        if (!Set(handle, DwmwaSystemBackdropType, DwmsbtTransientWindow))
            return false;
        target.BackgroundColor = Colors.Transparent;
        return true;
    }

    // Settings > Personalization > Colors > Transparency effects. Missing means on, as in Windows.
    static bool TransparencyOn()
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

    static bool Set(IntPtr window, int attribute, int value) =>
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
