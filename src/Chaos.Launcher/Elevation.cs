using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Chaos.Launcher;

static class Elevation
{
    const int TokenElevationType = 18;
    const int TokenElevationTypeFull = 2;

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return GetTokenInformation(identity.Token, TokenElevationType, out int type, sizeof(int), out _)
               && type == TokenElevationTypeFull;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information,
                                           int length, out int returned);
}
