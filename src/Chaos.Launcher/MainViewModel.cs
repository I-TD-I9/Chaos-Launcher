using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Chaos.Client;
using Chaos.Display;
using Chaos.Game;
using Chaos.Pictures;
using Chaos.Titles;

namespace Chaos.Launcher;

public enum ServerState
{
    Checking,
    Online,

    /// <summary>The web API answers but the gate cannot be reached: a network blocking the game's ports.</summary>
    Blocked,

    Offline,
}

/// <summary>Where the update prompt has got to.</summary>
public enum UpdateStep
{
    /// <summary>It offers the new launcher: Update now, or Not now.</summary>
    Offer,

    /// <summary>The new launcher is coming, the LP meter counting it down.</summary>
    Downloading,

    /// <summary>It didn't work out, and the prompt says why and offers the release page.</summary>
    Failed,
}

/// <summary>Everything the launcher's window shows and does.</summary>
public sealed class MainViewModel : ObservableObject
{
    readonly LauncherSettings settings = LauncherSettings.Load(LauncherSettings.DefaultPath);
    readonly ServerEndpoints server;
    readonly HttpClient http;
    readonly CardPictures pictures;
    readonly TitleNames titles;
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
        titles = new TitleNames(http, server.Api);
    }

    /// <summary>Finds the game, then asks after the server and the launcher's own updates.</summary>
    public async Task StartAsync()
    {
        if (elevated)
            PlayError = AsAdministrator;
        Game = await Task.Run(() => GameLocator.Find(settings.GameFolder));
        await Task.WhenAll(CheckServerAsync(), OfferUpdateAsync(), FinishUpdateAsync());
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
    PicturesProgress? picturesMeter;

    /// <summary>A line on how the card pictures stand, or null when the server hands out none.</summary>
    public string? PicturesText
    {
        get => picturesText;
        private set
        {
            if (Set(ref picturesText, value))
                Raise(nameof(PicturesUpToDateShown), nameof(PicturesLineShown));
        }
    }

    /// <summary>How far the pictures have come while they download, for the LP meter; null otherwise.</summary>
    public PicturesProgress? PicturesMeter
    {
        get => picturesMeter;
        private set
        {
            if (Set(ref picturesMeter, value))
                Raise(nameof(PicturesMeterShown), nameof(PicturesLineShown), nameof(PicturesLeft), nameof(PicturesTotal));
        }
    }

    public bool PicturesMeterShown => picturesMeter is not null;

    /// <summary>Whether the line under PLAY shows, in place of the meter.</summary>
    public bool PicturesLineShown => picturesMeter is null && picturesText is not null;

    public bool PicturesUpToDateShown => picturesText == PicturesUpToDate;

    public long PicturesLeft => picturesMeter is { } step ? step.Total - step.Done : 0;

    public long PicturesTotal => picturesMeter?.Total ?? 0;

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

    // Writes the working folder's patch.txt and brings its pictures and the titles' names up to
    // date, and returns the folder to start the game in. Neither keeps the game from starting for
    // long (CardPictures.Budget, TitleNames.Patience): whatever goes wrong, it starts with the
    // ones it has, or with none, from the game's own folder if need be. The titles are asked for
    // while the pictures come, and written once they are in place.
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
            if (done)
                return;
            PicturesText = step.Total == 0 ? PicturesChecking : $"Updating card pictures, {step.Done} of {step.Total}";
            PicturesMeter = step.Total == 0 ? null : step;
        });
        PicturesText = PicturesChecking;
        string bc = Path.Combine(WorkingFolder.Path, WorkingFolder.Overrides);
        var named = titles.FetchAsync();
        var outcome = await pictures.UpdateAsync(bc, progress);
        done = true;
        PicturesMeter = null;
        PicturesText = outcome switch
        {
            PicturesOutcome.None => null,
            PicturesOutcome.UpToDate => PicturesUpToDate,
            PicturesOutcome.Unfinished => PicturesLater,
            PicturesOutcome.Unwritable => PicturesUnwritten,
            _ => PicturesNotUpdated,
        };
        // A title the game has no name for shows as "Unknown", which says all there is to say, so
        // how this went is not told.
        var reply = await named;
        await Task.Run(() => TitleNames.Write(bc, reply));
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

    bool? signUpOpen;

    /// <summary>Whether the server takes new accounts, as its GET /api/info last said.</summary>
    public bool? SignUpOpen
    {
        get => signUpOpen;
        private set
        {
            if (Set(ref signUpOpen, value))
                Raise(nameof(SignUpStateText));
        }
    }

    public string? SignUpStateText => signUpOpen switch
    {
        true => "Sign-up open",
        false => "Sign-up closed",
        null => null,
    };

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
            {
                Outdated = fetched.TooOld(Releases.Current);
                SignUpOpen = fetched.SignUpOpen;
            }
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
    bool updateChecked;
    bool checkingUpdate;
    bool promptShown;
    UpdateStep updateStep;
    UpdateProgress downloaded;
    string? updateFailure;
    string? updatedNote;
    CancellationTokenSource? updateCancel;

    /// <summary>A newer release than this launcher, once one has been found.</summary>
    public LauncherRelease? Update
    {
        get => update;
        private set
        {
            if (Set(ref update, value))
                Raise(nameof(ShowUpdate), nameof(UpdateText), nameof(ShowUpdatedNote), nameof(VersionStateText),
                      nameof(UpdateButtonText), nameof(PromptTitle), nameof(PromptText), nameof(DownloadText));
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
                Raise(nameof(CanPlay), nameof(ShowUpdate), nameof(UpdateText), nameof(ShowUpdatedNote),
                      nameof(PromptTitle), nameof(PromptText));
        }
    }

    public bool ShowUpdate => update is not null || outdated;

    public string UpdateText => outdated ? "This launcher is out of date."
                              : update is null ? "" : $"Version {update.Version} of the launcher is out.";

    /// <summary>Where to download the new launcher: the newer release found, else the latest one.</summary>
    public Uri UpdatePage => update?.Page ?? Releases.LatestPage;

    /// <summary>"Updated to 0.8.1." once a launcher started after its own update has tidied up.</summary>
    public string? UpdatedNote
    {
        get => updatedNote;
        private set
        {
            if (Set(ref updatedNote, value))
                Raise(nameof(ShowUpdatedNote));
        }
    }

    public bool ShowUpdatedNote => updatedNote is not null && !ShowUpdate;

    /// <summary>What the Settings page says of this launcher's version.</summary>
    public string VersionStateText =>
        update is not null ? $"{update.Version} is out"
        : updateChecked ? $"{Releases.Current}, up to date"
        : Releases.Current.ToString();

    public string UpdateButtonText =>
        checkingUpdate ? "Checking…" : update is not null ? $"Update to {update.Version}" : "Check for updates";

    public bool CanCheckUpdate => !checkingUpdate && updateStep != UpdateStep.Downloading;

    /// <summary>Whether the update prompt is up over the window.</summary>
    public bool PromptShown
    {
        get => promptShown;
        private set => Set(ref promptShown, value);
    }

    public UpdateStep UpdateStep
    {
        get => updateStep;
        private set
        {
            if (Set(ref updateStep, value))
                Raise(nameof(PromptTitle), nameof(PromptText), nameof(CanCheckUpdate));
        }
    }

    public string PromptTitle =>
        updateStep == UpdateStep.Failed ? "The update didn't finish"
        : outdated ? "This launcher is out of date"
        : "A new launcher is ready";

    public string PromptText =>
        updateStep == UpdateStep.Failed ? updateFailure ?? ""
        : update is null ? ""
        : outdated ? $"Battle City no longer takes version {Releases.Current}. Update to {update.Version} to keep playing."
        : $"Version {update.Version} is out (you have {Releases.Current}). Update now? It takes a few seconds, and your settings stay as they are.";

    public string DownloadText => update is null ? "" : $"Downloading {update.Version}…";

    /// <summary>How far the new launcher has come, in bytes.</summary>
    public UpdateProgress Downloaded
    {
        get => downloaded;
        private set
        {
            if (Set(ref downloaded, value))
                Raise(nameof(DownloadLeft), nameof(DownloadTotal));
        }
    }

    /// <summary>The kilobytes of the new launcher still to come, for the LP meter.</summary>
    public long DownloadLeft => Math.Max(0, downloaded.Total - downloaded.Done) / 1024;

    public long DownloadTotal => downloaded.Total / 1024;

    /// <summary>Asks GitHub for the latest release, and remembers it when it is newer than this launcher.</summary>
    public async Task CheckForUpdateAsync()
    {
        try
        {
            var latest = await Releases.LatestAsync(http);
            if (latest is not null && latest.Version > Releases.Current)
                Update = latest;
            else if (latest is not null)
                updateChecked = true;
        }
        catch (Exception)
        {
            // Like the server's status this is only news: a failure leaves the banner hidden.
        }
        Raise(nameof(VersionStateText));
    }

    // At start: the prompt comes up when a newer launcher is out, unless the game is running.
    async Task OfferUpdateAsync()
    {
        await CheckForUpdateAsync();
        if (update is not null && SelfUpdate.Offers(update, Releases.Current, running || await Task.Run(SelfUpdate.IsGameRunning)))
            PromptShown = true;
    }

    /// <summary>The Settings page's button: looks for a new launcher, or updates to the one found.</summary>
    public async Task CheckOrUpdateAsync()
    {
        if (update is not null)
        {
            await UpdateNowAsync();
            return;
        }
        if (checkingUpdate)
            return;
        checkingUpdate = true;
        Raise(nameof(UpdateButtonText), nameof(CanCheckUpdate));
        try
        {
            await CheckForUpdateAsync();
        }
        finally
        {
            checkingUpdate = false;
            Raise(nameof(UpdateButtonText), nameof(CanCheckUpdate));
        }
    }

    /// <summary>"Not now", or closing the prompt after a failure: it stays down until the next start.</summary>
    public void DismissPrompt()
    {
        if (updateStep == UpdateStep.Downloading)
            return;
        PromptShown = false;
        UpdateStep = UpdateStep.Offer;
    }

    /// <summary>
    /// Calls off the download of the new launcher, as the window closes: what came of it is
    /// deleted, and the prompt says the update didn't finish.
    /// </summary>
    public void CancelUpdate() => updateCancel?.Cancel();

    /// <summary>
    /// Raised once the new launcher is in place and started: the window closes at once.
    /// </summary>
    public event Action? Restarting;

    /// <summary>
    /// Updates the launcher to the newer release, in the prompt with the LP meter counting the
    /// download down. With no release to update to, the release page opens as before.
    /// </summary>
    public async Task UpdateNowAsync()
    {
        if (update is not { } release)
        {
            Open(UpdatePage);
            return;
        }
        if (updateStep == UpdateStep.Downloading)
            return;
        PromptShown = true;
        if (running)
        {
            Fail(UpdateOutcome.GameRunning, release);
            return;
        }
        if (SelfUpdate.ForThisLauncher(http) is not { } self || !release.CanSelfUpdate)
        {
            Fail(UpdateOutcome.NotOffered, release);
            return;
        }
        Downloaded = new UpdateProgress(0, release.Exe!.Size);
        using var cancel = new CancellationTokenSource();
        updateCancel = cancel;
        UpdateStep = UpdateStep.Downloading;
        UpdateOutcome outcome;
        try
        {
            outcome = await self.UpdateAsync(release, new Progress<UpdateProgress>(step => Downloaded = step), cancel.Token);
        }
        catch (Exception)
        {
            // Whatever went wrong, the prompt must not be left stuck downloading: it says the
            // update didn't finish, and Close takes it down.
            outcome = UpdateOutcome.Unreachable;
        }
        finally
        {
            updateCancel = null;
        }
        if (outcome == UpdateOutcome.Restarting)
        {
            Restarting?.Invoke();
            return;
        }
        Fail(outcome, release);
    }

    void Fail(UpdateOutcome outcome, LauncherRelease release)
    {
        updateFailure = outcome switch
        {
            UpdateOutcome.GameRunning => $"The game is running. Close it, then update to {release.Version}.",
            UpdateOutcome.Unwritable => $"Windows won't let the launcher replace itself in its folder, so nothing was changed. Download {release.Version} from the release page instead.",
            UpdateOutcome.Unreachable => $"GitHub could not be reached, so nothing was changed. Try again later, or download {release.Version} from the release page.",
            UpdateOutcome.Refused => $"The download didn't match the release, so nothing was changed. Try again later, or download {release.Version} from the release page.",
            UpdateOutcome.NotStarted => $"The new launcher would not start, so this one was kept. Download {release.Version} from the release page instead.",
            _ => $"This release can't be installed from here. Download {release.Version} from the release page instead.",
        };
        UpdateStep = UpdateStep.Failed;
        Raise(nameof(PromptText));
    }

    // A launcher started by its own update tidies up after the one it replaced.
    async Task FinishUpdateAsync()
    {
        if (SelfUpdate.UpdatedFrom(Environment.GetCommandLineArgs()) is not { } old
            || SelfUpdate.ForThisLauncher(http) is not { } self)
            return;
        UpdatedNote = await self.FinishAsync(old);
    }

    // --- The launcher itself -----------------------------------------------------------------

    public string VersionText => $"v{Releases.Current}";

    /// <summary>Opens a page in the player's browser.</summary>
    public static void Open(Uri page) =>
        Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
}
