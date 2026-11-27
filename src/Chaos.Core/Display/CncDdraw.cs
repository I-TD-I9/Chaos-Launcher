using Chaos.Client;
using Chaos.Game;

namespace Chaos.Display;

public static class CncDdraw
{
    public const string Version = "7.1.0.0";
    public const string DllName = "ddraw.dll";
    public const string IniName = "ddraw.ini";
    public const long Length = 411_648;
    public const string Sha256 = "85e0f7d530dfda134793a57cb3e76b0287dcc96892ee57162dd68f47283b03a9";

    public const string LicenseName = "ddraw-LICENSE.txt";

    public const string ConfigFileVariable = "CNC_DDRAW_CONFIG_FILE";

    const string Resource = "Chaos.Display.ddraw.dll";
    const string LicenseResource = "Chaos.Display.cnc-ddraw-LICENSE.txt";

    public const string Screenshots = "Screenshots";

    public static byte[] Dll() => ReadResource(Resource);

    public static byte[] License() => ReadResource(LicenseResource);

    static byte[] ReadResource(string name)
    {
        using var stream = typeof(CncDdraw).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"missing resource {name}");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static string? Folder(string folder) => AnsiPath.Folder(folder, AnsiPath.Fits, AnsiPath.ShortName);

    public static void Prepare(Resolution resolution, string folder, WindowPlacement? window)
    {
        string dll = Path.Combine(folder, DllName), ini = Path.Combine(folder, IniName);
        string license = Path.Combine(folder, LicenseName);
        if (resolution.IsDefault)
        {
            // Fails while the game has the DLL loaded.
            foreach (var file in new[] { dll, ini, license })
                if (File.Exists(file))
                    File.Delete(file);
            return;
        }
        Directory.CreateDirectory(folder);
        string? shots = AnsiPath.Folder(Path.Combine(folder, Screenshots), AnsiPath.Fits, AnsiPath.ShortName);
        byte[] settings = AnsiPath.CodePage().GetBytes(DdrawIni.Build(resolution, window, shots));
        LauncherFiles.Replace(dll, Dll());
        if (SupportedClient.Sha256Of(dll) != Sha256)
            throw new IOException($"{dll} is not cnc-ddraw {Version}");
        LauncherFiles.Replace(license, License());
        LauncherFiles.Replace(ini, settings);
    }
}
