using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Chaos.Client;
using Chaos.Display;
using Chaos.Game;
using Chaos.Pictures;

namespace Chaos.Launcher;

public enum ServerState
{
    Checking,
    Online,

    /// <summary>The web API answers but the gate cannot be reached: a network blocking the game's ports.</summary>
    Blocked,

    Offline,
}

/// <summary>Everything the launcher's window shows and does.</summary>
public sealed class MainViewModel : ObservableObject
{
    readonly LauncherSettings settings = LauncherSettings.Load(LauncherSettings.DefaultPath);
    readonly ServerEndpoints server;
    readonly HttpClient http;
    readonly CardPictures pictures;
    // The window's thread, where a game that exits on its own is reported back.
    readonly SynchronizationContext ui = SynchronizationContext.Current ?? new SynchronizationContext();

    readonly bool elevated = Elevation.ByRequest();

    public MainViewModel()
    {
        server = settings.Server();
        // Every reply the launcher reads whole is a few kilobytes, and nothing bigger is held in
        // memory: the card pictures are read as they come (CardPictures).
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1 << 20 };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChaosLauncher", Releases.Current.ToString()));
        pictures = new CardPictures(http, server.Api);
    }

    /// <summary>Finds the game, then asks after the server and the launcher's own updates.</summary>
    public async Task StartAsync()
    {
        if (elevated)
            PlayError = AsAdministrator;
        Game = await Task.Run(() => GameLocator.Find(settings.GameFolder));
        await Task.WhenAll(CheckServerAsync(), CheckForUpdateAsync());
    }

    // --- The game --------------------------------------------------------------------------

    GameFolder? game;
    bool starting;
    bool running;
    string? playError;

    public GameFolder? Game
    {
        get => game;
        private set
        {
            if (Set(ref game, value))
                Raise(nameof(GameFolderText), nameof(GameReady), nameof(GameStatusText), nameof(CanPlay));
        }
    }

    public string GameFolderText => game?.Path ?? "Not found";

    public bool GameReady => game?.Status == GameStatus.Ready;

    public string GameStatusText => game?.Status switch
    {
        GameStatus.Ready => "Ready to play.",
        GameStatus.Unsupported => $"This copy of the game is another version. Battle City needs the February 2006 client, {SupportedClient.Version}.",
        GameStatus.DataMissing => "The game's data is missing from this folder: it needs patch.txt and merge.dat beside online_pc.dat. Unpack the whole folder again.",
        GameStatus.NoClient => "This folder has no online_pc.dat. Choose the Yu-Gi-Oh! ONLINE folder.",
        GameStatus.NotFound => "This folder no longer exists. Choose the Yu-Gi-Oh! ONLINE folder.",
        _ => "The game was not found. Choose the Yu-Gi-Oh! ONLINE folder.",
    };

    public bool Starting
    {
        get => starting;
        private set
        {
            if (Set(ref starting, value))
                Raise(nameof(CanPlay), nameof(PlayText));
        }
    }

    public bool Running
    {
        get => running;
        private set
        {
            if (Set(ref running, value))
                Raise(nameof(CanPlay), nameof(PlayText));
        }
    }

    public bool CanPlay => GameReady && !starting && !running && !elevated && !outdated;

    const string AsAdministrator =
        "The launcher is running as administrator. Close it and start it again normally: the game must not run with administrator rights.";

    public string PlayText => running ? "GAME RUNNING" : starting ? "STARTING" : "PLAY";

    public string? PlayError
    {
        get => playError;
        private set => Set(ref playError, value);
    }

    /// <summary>The player chose a folder: look at it, and remember it if the game is there.</summary>
    public void ChooseFolder(string path)
    {
        try
        {
            Game = GameFolder.Inspect(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Game = new GameFolder(path, GameStatus.NotFound, null);
            PlayError = $"The launcher could not read that folder: {e.Message}";
            return;
        }
        PlayError = elevated ? AsAdministrator : null;
        if (Game.Status != GameStatus.Ready)
            return;
        settings.GameFolder = Game.Path;
        SaveSettings();
    }

    void SaveSettings()
    {
        try
        {
            settings.Save(LauncherSettings.DefaultPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing breaks: the launcher just won't remember the folder or the resolution next
            // time.
        }
    }

    /// <summary>
    /// Makes the patched copy and starts it. <paramref name="window"/> is the launcher's own
    /// window: a bigger game window goes on its monitor.
    /// </summary>
    public async Task PlayAsync(IntPtr window)
    {
        if (game is not { Status: GameStatus.Ready } current || !CanPlay)
            return;
        PlayError = null;
        PicturesText = null;
        Starting = true;
        try
        {
            string client;
            try
            {
                client = await Task.Run(() => GameStarter.Prepare(current, server, LauncherSettings.Folder));
            }
            catch (PatchException e)
            {
                PlayError = $"The launcher could not prepare the game: {e.Message}.";
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Most often the copy is still running from an earlier Play and cannot be replaced.
                PlayError = $"The launcher could not read the game or write its copy of it. If the game is open, close it and try again. ({e.Message})";
                return;
            }

            // A folder of the launcher's that the game can't be given keeps it in its own window.
            string? home = CncDdraw.Folder(LauncherSettings.Folder);
            var resolution = home is null ? Resolution.Default : settings.Resolution;
            try
            {
                WindowPlacement? placement = null;
                if (resolution.IsWindow)
                {
                    // The window that fits this monitor, which may not be the one chosen on another.
                    var area = Measure(window);
                    resolution = area.Effective(resolution);
                    placement = resolution.IsWindow ? area.Place(resolution) : null;
                }
                await Task.Run(() => CncDdraw.Prepare(resolution, LauncherSettings.Folder, placement));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // As with the copy, most often the game is still open and holds the DLL.
                PlayError = $"The launcher could not set the game up {Described(resolution)}. If the game is open, close it and try again, or choose another resolution. ({e.Message})";
                return;
            }

            string start = await PreparePicturesAsync(current);

            try
            {
                ClientKey.Prepare(ClientKey.SaveFolder, current.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                PlayError = $"The launcher could not make the folder the game keeps its saves in. ({e.Message})";
                return;
            }

            try
            {
                var process = GameStarter.Start(client, start, resolution, home);
                Running = true;
                process.Exited += (_, _) => ui.Post(_ => Running = false, null);
                process.EnableRaisingEvents = true;
                if (process.HasExited)
                    Running = false;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                PlayError = $"Windows could not start the game: {e.Message}";
            }
        }
        finally
        {
            Starting = false;
        }
    }

    static string Described(Resolution resolution) =>
        resolution.IsFullScreen ? "in full screen"
        : resolution.IsWindow ? $"in a {resolution.Width} x {resolution.Height} window"
        : "in its own window";

    // --- The card pictures -------------------------------------------------------------------

    string? picturesText;

    /// <summary>A line on how the card pictures stand, or null when the server hands out none.</summary>
    public string? PicturesText
    {
        get => picturesText;
        private set => Set(ref picturesText, value);
    }

    const string PicturesChecking = "Checking the card pictures…";
    const string PicturesUpToDate = "Card pictures up to date.";
    const string PicturesNotUpdated = "The card pictures could not be updated just now, so the game has the ones it had before.";
    const string PicturesLater =
        "Not all the card pictures came in time, so the game has the ones it had before. The rest come at the next Play.";
    const string PicturesUnwritten =
        "The card pictures could not all be written to the launcher's folder, so some may be old or missing.";
    const string PicturesNotSetUp = "The launcher could not set the card pictures up, so the game starts without them.";
    const string NoPicturesGameFolder =
        "No card pictures from the server with this game folder: its path is one the game can't take. A plain one, such as C:\\Games\\Yu-Gi-Oh, works.";
    const string NoPicturesLauncherFolder =
        "No card pictures from the server: the launcher's folder, under your Windows user name, has letters the game can't read.";

    // Writes the working folder's patch.txt and brings its pictures up to date, and returns the
    // folder to start the game in. The pictures never keep the game from starting for long
    // (CardPictures.Budget): whatever goes wrong, it starts with the ones it has, or with none,
    // from the game's own folder if need be.
    async Task<string> PreparePicturesAsync(GameFolder current)
    {
        string start;
        NoPictures? reason;
        try
        {
            (start, reason) = await Task.Run(() => WorkingFolder.Prepare(current, WorkingFolder.Path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PicturesText = PicturesNotSetUp;
            return WorkingFolder.Fallback(current);
        }
        if (reason is { } why)
        {
            PicturesText = why == NoPictures.LauncherFolder ? NoPicturesLauncherFolder : NoPicturesGameFolder;
            return start;
        }

        bool done = false;
        var progress = new Progress<PicturesProgress>(step =>
        {
            if (!done)
                PicturesText = step.Total == 0 ? PicturesChecking : $"Updating card pictures, {step.Done} of {step.Total}";
        });
        PicturesText = PicturesChecking;
        var outcome = await pictures.UpdateAsync(Path.Combine(WorkingFolder.Path, WorkingFolder.Overrides), progress);
        done = true;
        PicturesText = outcome switch
        {
            PicturesOutcome.None => null,
            PicturesOutcome.UpToDate => PicturesUpToDate,
            PicturesOutcome.Unfinished => PicturesLater,
            PicturesOutcome.Unwritable => PicturesUnwritten,
            _ => PicturesNotUpdated,
        };
        return start;
    }

    // --- The settings ------------------------------------------------------------------------

    WorkArea? workArea;
    bool scaled = true;
    bool listing;
    IReadOnlyList<ResolutionChoice> resolutions = [];

    /// <summary>
    /// The resolutions the Settings page's drop-down offers: the default, the bigger windows that
    /// fit the launcher's monitor, and full screen.
    /// </summary>
    public IReadOnlyList<ResolutionChoice> Resolutions
    {
        get => resolutions;
        private set
        {
            if (Set(ref resolutions, value))
                Raise(nameof(SharpOffered));
        }
    }

    /// <summary>
    /// The resolution the drop-down shows: what Play would use here. A window chosen for a
    /// bigger monitor than this one shows as the largest that fits, and stays chosen as it was
    /// until the player picks another. Picking one keeps it at once; the game takes it up at the
    /// next Play.
    /// </summary>
    public ResolutionChoice? ChosenResolution
    {
        get
        {
            if (listing)
                return null;
            var shown = workArea is { } area ? area.Effective(settings.Resolution, scaled) : settings.Resolution;
            return resolutions.FirstOrDefault(choice => choice.Resolution == shown);
        }
        set
        {
            // The drop-down sets none while its list is replaced.
            if (value is null)
                return;
            if (settings.Resolution != value.Resolution)
            {
                settings.Resolution = value.Resolution;
                SaveSettings();
            }
            Raise(nameof(ChosenResolution));
        }
    }

    /// <summary>Whether a window as sharp as can be is on offer, for the note on the star that marks it.</summary>
    public bool SharpOffered => resolutions.Any(choice => choice.Sharp);

    /// <summary>
    /// Whether the resolution can be changed at all: not when the launcher's folder is one the
    /// game can't be given (CncDdraw.Folder), and the game keeps its own window.
    /// </summary>
    public bool ResolutionChangeable => scaled;

    public bool ResolutionFixed => !scaled;

    /// <summary>Lists what fits the monitor <paramref name="window"/> is on, as the Settings page comes up.</summary>
    public void ShowResolutions(IntPtr window) => ShowResolutions(Measure(window));

    /// <summary>Lists what fits <paramref name="area"/>.</summary>
    public void ShowResolutions(WorkArea area)
    {
        workArea = area;
        scaled = CncDdraw.Folder(LauncherSettings.Folder) is not null;
        Raise(nameof(ResolutionChangeable), nameof(ResolutionFixed));
        // The drop-down lets go of its choice while its list is replaced, and is given the one to
        // show once the new list is in.
        listing = true;
        Resolutions = [.. area.Choices(scaled).Select(resolution => new ResolutionChoice(resolution))];
        listing = false;
        Raise(nameof(ChosenResolution));
    }

    // The monitor the launcher is on; when Windows can't say, none, where no bigger window fits.
    static WorkArea Measure(IntPtr window)
    {
        try
        {
            return WorkArea.Of(window);
        }
        catch (Win32Exception)
        {
            return default;
        }
    }

    // --- The server ------------------------------------------------------------------------

    // Each check opens a connection to the gate, and the server limits how many new connections
    // one address may open in a minute, so a check waits for the one before and comes no sooner
    // than this after it.
    static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    ServerState serverState;
    bool checking;
    DateTime lastCheck = DateTime.MinValue;

    public ServerState ServerState
    {
        get => serverState;
        private set
        {
            if (Set(ref serverState, value))
                Raise(nameof(ServerText));
        }
    }

    public string ServerText => serverState switch
    {
        ServerState.Online => "Server online",
        ServerState.Blocked => "Server unreachable from this network",
        ServerState.Offline => "Server offline",
        _ => "Checking the server",
    };

    public bool CanCheck => !checking;

    public async Task CheckServerAsync()
    {
        if (checking || DateTime.UtcNow - lastCheck < CheckInterval)
            return;
        checking = true;
        lastCheck = DateTime.UtcNow;
        Raise(nameof(CanCheck));
        ServerState = ServerState.Checking;
        try
        {
            var fetching = ServerInfo.FetchAsync(http, server.Api);
            var probing = ServerProbe.GateOpensAsync(server, TimeSpan.FromSeconds(6));
            var fetched = await fetching;
            bool gate = await probing;
            if (fetched is not null)
                Outdated = fetched.TooOld(Releases.Current);
            ServerState = gate ? ServerState.Online : fetched is not null ? ServerState.Blocked : ServerState.Offline;
        }
        catch (Exception)
        {
            // A status check never takes the launcher down: whatever went wrong, the server could
            // not be reached.
            ServerState = ServerState.Offline;
        }
        finally
        {
            checking = false;
            Raise(nameof(CanCheck));
        }
    }

    // --- The account page ------------------------------------------------------------------

    // Accounts are made, and their passwords set, on the website with Discord: the launcher only
    // takes the player there.
    public static void OpenCreateAccount() => Open(ServerEndpoints.CreateAccountPage);

    public static void OpenResetPassword() => Open(ServerEndpoints.ResetPasswordPage);


    // --- The launcher's own updates ----------------------------------------------------------

    LauncherRelease? update;
    bool outdated;

    public LauncherRelease? Update
    {
        get => update;
        private set
        {
            if (Set(ref update, value))
                Raise(nameof(ShowUpdate), nameof(UpdateText));
        }
    }

    /// <summary>
    /// The server takes no launcher this old any more (its GET /api/info says which it does), so
    /// the game isn't started from it.
    /// </summary>
    public bool Outdated
    {
        get => outdated;
        private set
        {
            if (Set(ref outdated, value))
                Raise(nameof(CanPlay), nameof(ShowUpdate), nameof(UpdateText));
        }
    }

    public bool ShowUpdate => update is not null || outdated;

    public string UpdateText => outdated ? "This launcher is out of date."
                              : update is null ? "" : $"Version {update.Version} of the launcher is out.";

    /// <summary>Where to download the new launcher: the newer release found, else the latest one.</summary>
    public Uri UpdatePage => update?.Page ?? Releases.LatestPage;

    public async Task CheckForUpdateAsync()
    {
        try
        {
            var latest = await Releases.LatestAsync(http);
            if (latest is not null && latest.Version > Releases.Current)
                Update = latest;
        }
        catch (Exception)
        {
            // Like the server's status this is only news: a failure leaves the banner hidden.
        }
    }

    // --- The launcher itself -----------------------------------------------------------------

    public string VersionText => $"v{Releases.Current}";

    /// <summary>Opens a page in the player's browser.</summary>
    public static void Open(Uri page) =>
        Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
}
