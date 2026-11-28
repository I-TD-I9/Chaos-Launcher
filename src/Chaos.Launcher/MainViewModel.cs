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
    Blocked,
    Offline,
}

/// <summary>State of the update prompt.</summary>
public enum UpdateStep
{
    Offer,
    Downloading,
    Failed,
}

/// <summary>View model for the main window.</summary>
public sealed class MainViewModel : ObservableObject
{
    readonly LauncherSettings settings = LauncherSettings.Load(LauncherSettings.DefaultPath);
    readonly ServerEndpoints server;
    readonly HttpClient http;
    readonly CardPictures pictures;
    readonly TitleNames titles;
    // UI thread, for the game's exit event.
    readonly SynchronizationContext ui = SynchronizationContext.Current ?? new SynchronizationContext();

    readonly bool elevated = Elevation.IsElevated();

    public MainViewModel()
    {
        server = settings.Server();
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1 << 20 };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChaosLauncher", Releases.Current.ToString()));
        pictures = new CardPictures(http, server.Api);
        titles = new TitleNames(http, server.Api);
    }

    public async Task StartAsync()
    {
        if (elevated)
            PlayError = AdminError;
        Game = await Task.Run(() => GameLocator.Find(settings.GameFolder));
        await Task.WhenAll(CheckServerAsync(), OfferUpdateAsync(), FinishUpdateAsync());
    }

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
        GameStatus.Unsupported => $"This copy is another version. Battle City needs the February 2006 client, {SupportedClient.Version}.",
        GameStatus.DataMissing => "This folder is missing patch.txt or merge.dat. Unpack the whole game again.",
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

    const string AdminError =
        "The game can't run as administrator. Close the launcher and start it again normally.";

    public string PlayText => running ? "GAME RUNNING" : starting ? "STARTING" : "PLAY";

    public string? PlayError
    {
        get => playError;
        private set => Set(ref playError, value);
    }

    public void ChooseFolder(string path)
    {
        try
        {
            Game = GameFolder.Inspect(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Game = new GameFolder(path, GameStatus.NotFound, null);
            PlayError = $"Couldn't read that folder: {e.Message}";
            return;
        }
        PlayError = elevated ? AdminError : null;
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
        }
    }

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
                PlayError = $"Couldn't prepare the game ({e.Message}).";
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                PlayError = $"Couldn't copy the game. If it's open, close it and try again. ({e.Message})";
                return;
            }

            string? home = CncDdraw.Folder(LauncherSettings.Folder);
            var resolution = home is null ? Resolution.Default : settings.Resolution;
            try
            {
                WindowPlacement? placement = null;
                if (resolution.IsWindow)
                {
                    // Use a window size that fits this monitor.
                    var area = Measure(window);
                    resolution = area.Resolve(resolution);
                    placement = resolution.IsWindow ? area.Place(resolution) : null;
                }
                await Task.Run(() => CncDdraw.Prepare(resolution, LauncherSettings.Folder, placement));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Usually the game is still open and holds the DLL.
                PlayError = $"Couldn't set the game up {Describe(resolution)}. If it's open, close it and try again, or pick another resolution. ({e.Message})";
                return;
            }

            string start = await PreparePicturesAsync(current);

            try
            {
                ClientKey.Prepare(ClientKey.SaveFolder, current.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                PlayError = $"Couldn't create the game's save folder. ({e.Message})";
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
                PlayError = $"Windows couldn't start the game: {e.Message}";
            }
        }
        finally
        {
            Starting = false;
        }
    }

    static string Describe(Resolution resolution) =>
        resolution.IsFullScreen ? "in full screen"
        : resolution.IsWindow ? $"in a {resolution.Width} x {resolution.Height} window"
        : "in its own window";

    string? picturesText;
    PicturesProgress? picturesMeter;

    public string? PicturesText
    {
        get => picturesText;
        private set
        {
            if (Set(ref picturesText, value))
                Raise(nameof(ShowUpToDate), nameof(ShowPicturesLine));
        }
    }

    public PicturesProgress? PicturesMeter
    {
        get => picturesMeter;
        private set
        {
            if (Set(ref picturesMeter, value))
                Raise(nameof(ShowPicturesMeter), nameof(ShowPicturesLine), nameof(PicturesDone), nameof(PicturesTotal));
        }
    }

    public bool ShowPicturesMeter => picturesMeter is not null;

    public bool ShowPicturesLine => picturesMeter is null && picturesText is not null;

    public bool ShowUpToDate => picturesText == PicturesUpToDate;

    public long PicturesDone => picturesMeter?.Done ?? 0;

    public long PicturesTotal => picturesMeter?.Total ?? 0;

    const string PicturesChecking = "Checking the client…";
    const string PicturesUpdating = "Updating the client…";
    const string PicturesUpToDate = "The client is up to date.";
    const string PicturesNotUpdated = "Couldn't update the client, so the game starts as it was.";
    const string PicturesUnfinished =
        "The update didn't finish in time, so the game starts as it was. The rest comes at the next Play.";
    const string PicturesUnwritten =
        "Part of the update couldn't be saved, so the game may be out of date.";
    const string PicturesSetupFailed = "Couldn't get the update ready, so the game starts as it was.";
    const string NoPicturesGameFolder =
        "The client can't be updated in this folder. A plain path, such as C:\\Games\\Yu-Gi-Oh, works.";
    const string NoPicturesLauncherFolder =
        "The client can't be updated: your Windows user name has letters the game can't read.";

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
            PicturesText = PicturesSetupFailed;
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
            PicturesText = step.Total == 0 ? PicturesChecking : PicturesUpdating;
            PicturesMeter = step.Total == 0 ? null : step;
        });
        PicturesText = PicturesChecking;
        string bc = Path.Combine(WorkingFolder.Path, WorkingFolder.Overrides);
        var fetchingTitles = titles.FetchAsync();
        var outcome = await pictures.UpdateAsync(bc, progress);
        done = true;
        PicturesMeter = null;
        PicturesText = outcome switch
        {
            PicturesOutcome.None => null,
            PicturesOutcome.UpToDate => PicturesUpToDate,
            PicturesOutcome.Unfinished => PicturesUnfinished,
            PicturesOutcome.Unwritable => PicturesUnwritten,
            _ => PicturesNotUpdated,
        };
        var reply = await fetchingTitles;
        await Task.Run(() => TitleNames.Write(bc, reply));
        await Task.Run(() => CardNames.Write(bc, current.Path));
        return start;
    }

    WorkArea? workArea;
    bool scaled = true;
    bool listing;
    IReadOnlyList<ResolutionChoice> resolutions = [];

    public IReadOnlyList<ResolutionChoice> Resolutions
    {
        get => resolutions;
        private set
        {
            if (Set(ref resolutions, value))
                Raise(nameof(ShowSharpNote));
        }
    }

    public ResolutionChoice? SelectedResolution
    {
        get
        {
            if (listing)
                return null;
            var shown = workArea is { } area ? area.Resolve(settings.Resolution, scaled) : settings.Resolution;
            return resolutions.FirstOrDefault(choice => choice.Resolution == shown);
        }
        set
        {
            // The drop-down sets null while its list changes.
            if (value is null)
                return;
            if (settings.Resolution != value.Resolution)
            {
                settings.Resolution = value.Resolution;
                SaveSettings();
            }
            Raise(nameof(SelectedResolution));
        }
    }

    public bool ShowSharpNote => resolutions.Any(choice => choice.Sharp);

    public bool CanChangeResolution => scaled;

    public void ShowResolutions(IntPtr window) => ShowResolutions(Measure(window));

    public void ShowResolutions(WorkArea area)
    {
        workArea = area;
        scaled = CncDdraw.Folder(LauncherSettings.Folder) is not null;
        Raise(nameof(CanChangeResolution));
        listing = true;
        Resolutions = [.. area.Choices(scaled).Select(resolution => new ResolutionChoice(resolution))];
        listing = false;
        Raise(nameof(SelectedResolution));
    }

    // The monitor the launcher is on.
    static WorkArea Measure(IntPtr window)
    {
        try
        {
            return WorkArea.FromWindow(window);
        }
        catch (Win32Exception)
        {
            return default;
        }
    }

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
        ServerState.Blocked => "Server blocked on this network",
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
            var probing = ServerProbe.CanConnectAsync(server, TimeSpan.FromSeconds(6));
            var fetched = await fetching;
            bool gate = await probing;
            if (fetched is not null)
                Outdated = fetched.TooOld(Releases.Current);
            ServerState = gate ? ServerState.Online : fetched is not null ? ServerState.Blocked : ServerState.Offline;
        }
        catch (Exception)
        {
            // Never crash on a status check.
            ServerState = ServerState.Offline;
        }
        finally
        {
            checking = false;
            Raise(nameof(CanCheck));
        }
    }

    // Accounts live on the website.
    public static void OpenAccount() => Open(ServerEndpoints.AccountPage);

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

    public Uri UpdatePage => update?.Page ?? Releases.LatestPage;

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

    public string VersionStateText =>
        update is not null ? $"{update.Version} is out"
        : updateChecked ? $"{Releases.Current}, up to date"
        : Releases.Current.ToString();

    public string UpdateButtonText =>
        checkingUpdate ? "Checking…" : update is not null ? $"Update to {update.Version}" : "Check for updates";

    public bool CanCheckUpdate => !checkingUpdate && updateStep != UpdateStep.Downloading;

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
        : $"Version {update.Version} is out (you have {Releases.Current}). Update now? Your settings are kept.";

    public string DownloadText => update is null ? "" : $"Downloading {update.Version}…";

    public UpdateProgress Downloaded
    {
        get => downloaded;
        private set
        {
            if (Set(ref downloaded, value))
                Raise(nameof(DownloadDone), nameof(DownloadTotal));
        }
    }

    public long DownloadDone => Math.Min(downloaded.Done, downloaded.Total) / 1024;

    public long DownloadTotal => downloaded.Total / 1024;

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
            // Not critical, the banner just stays hidden.
        }
        Raise(nameof(VersionStateText));
    }

    async Task OfferUpdateAsync()
    {
        await CheckForUpdateAsync();
        if (update is not null && SelfUpdate.Offers(update, Releases.Current, running || await Task.Run(SelfUpdate.IsGameRunning)))
            PromptShown = true;
    }

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

    public void DismissPrompt()
    {
        if (updateStep == UpdateStep.Downloading)
            return;
        PromptShown = false;
        UpdateStep = UpdateStep.Offer;
    }

    public void CancelUpdate() => updateCancel?.Cancel();

    public event Action? Restarting;

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
        if (SelfUpdate.Create(http) is not { } self || !release.CanSelfUpdate)
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
            // Don't leave the prompt stuck on downloading.
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
            UpdateOutcome.Unwritable => $"The launcher can't replace itself in its folder. Download {release.Version} from the release page instead.",
            UpdateOutcome.Unreachable => $"Couldn't reach GitHub. Try again later, or download {release.Version} from the release page.",
            UpdateOutcome.Refused => $"The download didn't match the release. Try again later, or download {release.Version} from the release page.",
            UpdateOutcome.NotStarted => $"The new launcher wouldn't start, so this one was kept. Download {release.Version} from the release page instead.",
            _ => $"This release can't be installed from here. Download {release.Version} from the release page instead.",
        };
        UpdateStep = UpdateStep.Failed;
        Raise(nameof(PromptText));
    }

    // After an update, clean up the old launcher.
    async Task FinishUpdateAsync()
    {
        if (SelfUpdate.UpdatedFrom(Environment.GetCommandLineArgs()) is not { } old
            || SelfUpdate.Create(http) is not { } self)
            return;
        UpdatedNote = await self.FinishAsync(old);
    }

    public string VersionText => $"v{Releases.Current}";

    public static void Open(Uri page) =>
        Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
}
