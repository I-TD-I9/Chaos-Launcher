using System.Runtime.InteropServices;
using System.Text;

namespace Chaos.Game;

static class AnsiPath
{
    static AnsiPath() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding CodePage() =>
        Encoding.GetEncoding((int)GetACP(), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    public static bool Fits(string path)
    {
        try
        {
            var ansi = CodePage();
            byte[] bytes = ansi.GetBytes(path);
            return ansi.GetString(bytes) == path && bytes.Count(b => b == '\\') == path.Count(c => c == '\\');
        }
        catch (Exception e) when (e is EncoderFallbackException or DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static string? ShortName(string path)
    {
        var buffer = new char[260];
        uint length = GetShortPathName(path, buffer, (uint)buffer.Length);
        return length is > 0 and < 260 ? new string(buffer, 0, (int)length) : null;
    }

    public static string? Folder(string folder, Func<string, bool> fits, Func<string, string?> shortName)
    {
        Directory.CreateDirectory(folder);
        return Usable(folder, fits, shortName);
    }

    public static string? Usable(string path, Func<string, bool> fits, Func<string, string?> shortName) =>
        fits(path) ? path : shortName(path) is { } name && fits(name) ? name : null;

    [DllImport("kernel32.dll")]
    static extern uint GetACP();

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathName(string longPath, [Out] char[] shortPath, uint length);
}
