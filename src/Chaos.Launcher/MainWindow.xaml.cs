using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Chaos.Game;
using Microsoft.Win32;

namespace Chaos.Launcher;

public partial class MainWindow : Window
{
    // How much of the slab shows through the tint: faint, so that the glass still reads as glass.
    const double SlabOpacity = 0.40;

    // How long the window takes to turn from glass to solid and back as it gains or loses focus.
    static readonly TimeSpan GlassFade = TimeSpan.FromMilliseconds(150);

    readonly MainViewModel model = new();
    string? slabFolder;
    int slabLoads;
    bool acrylic;
    bool closeAfterUpdate;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = model;
        model.PropertyChanged += Model_PropertyChanged;
        // The new launcher is in place and running: this one makes way for it at once.
        model.Restarting += () => Application.Current.Shutdown();
        Loaded += async (_, _) => await model.StartAsync();
        Activated += (_, _) => ShowGlass(true);
        Deactivated += (_, _) => ShowGlass(false);
        Closing += Window_Closing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Backdrop.HideCaptionButtons(this);
        // With the acrylic, the desktop shows through the slab and the tint; without it, the
        // same layers lie on navy.
        acrylic = Backdrop.TryAcrylic(this);
        Rim.Visibility = acrylic ? Visibility.Collapsed : Visibility.Visible;
        Fallback.Opacity = acrylic && IsActive ? 0 : 1;
    }

    // The glass is only see-through while the window has the focus: once another window is
    // clicked, the navy fades back in under the slab and the tint, so the desktop never shows
    // through, whatever DWM does with the backdrop of an inactive window. Without the acrylic
    // the navy is always there, and nothing changes.
    internal void ShowGlass(bool active)
    {
        double opacity = acrylic && active ? 0 : 1;
        Fallback.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, GlassFade));
    }

    async void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Game))
            await LoadSlabAsync(model.Game?.Path);
        else if (e.PropertyName == nameof(MainViewModel.UpdateStep) && closeAfterUpdate
                 && model.UpdateStep != UpdateStep.Downloading)
            Close();
    }

    // Closed while the new launcher downloads: the download is called off first, so that no
    // part of the file is left behind, and the window closes once it has stopped.
    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (model.UpdateStep != UpdateStep.Downloading)
            return;
        e.Cancel = true;
        closeAfterUpdate = true;
        model.CancelUpdate();
    }

    // --- The slab --------------------------------------------------------------------------------

    // The slab comes from the player's own game folder, read again whenever the folder changes;
    // until there is one, or if it can't be read, the window goes without.
    //
    // Only the archive is read on another thread. The picture is decoded here, on the window's
    // thread: a bitmap decoded on another thread stays tied to that thread's decoder even once
    // frozen, and freezing the brushes that tile it would then fail here.
    async Task LoadSlabAsync(string? folder)
    {
        if (folder == slabFolder)
            return;
        slabFolder = folder;
        int load = ++slabLoads;
        byte[]? bmp = await Task.Run(() => SlabTexture.Read(folder));
        // A folder chosen since has a load of its own.
        if (load == slabLoads)
            ShowSlab(bmp);
    }

    // The game draws a frame of its own round the slab, 6 pixels wide; the tile is what lies
    // inside it (as the website's copy is), or the seams would show as a grid of lines.
    const int SlabFrame = 6;

    // The game's slab is 504 x 280 with its frame; anything far larger is not the slab, and is
    // not handed to the decoder at all.
    const int LargestSlab = 2048;

    static BitmapSource? Decode(byte[]? bmp)
    {
        // The width and height sit at bytes 18 to 25 of a BMP's header (a negative height is a
        // picture stored top down).
        if (bmp is null || bmp.Length < 26)
            return null;
        int width = BitConverter.ToInt32(bmp, 18), height = BitConverter.ToInt32(bmp, 22);
        if (width <= 0 || width > LargestSlab || height == 0 || height == int.MinValue || Math.Abs(height) > LargestSlab)
            return null;
        try
        {
            var decoder = new BmpBitmapDecoder(new MemoryStream(bmp), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (frame.PixelWidth > 8 * SlabFrame && frame.PixelHeight > 8 * SlabFrame)
                frame = new CroppedBitmap(frame, new Int32Rect(SlabFrame, SlabFrame,
                                                               frame.PixelWidth - 2 * SlabFrame, frame.PixelHeight - 2 * SlabFrame));
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            // A broken or hostile file can make the decoder throw nearly anything (a COMException,
            // an OutOfMemoryException): the window simply goes without the slab.
            return null;
        }
    }

    /// <summary>Lays the slab from these BMP bytes under the window, or takes it away for null.</summary>
    internal void ShowSlab(byte[]? bmp) => ShowSlab(Decode(bmp));

    void ShowSlab(BitmapSource? tile)
    {
        Slab.BeginAnimation(OpacityProperty, null);
        if (tile is null)
        {
            Slab.Fill = null;
            Slab.Opacity = 0;
            Glass.SetSlab(Root, null);
            return;
        }
        Slab.Fill = Tiled(tile, 0);
        Glass.SetSlab(Root, Blurred(tile));
        Slab.Opacity = 0;
        Slab.BeginAnimation(OpacityProperty, new DoubleAnimation(0, SlabOpacity, TimeSpan.FromMilliseconds(250)));
    }

    // The slab tiled at its own size in pixels, the first tile at (offset, offset).
    static ImageBrush Tiled(BitmapSource tile, double offset)
    {
        var brush = new ImageBrush(tile)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(offset, offset, tile.PixelWidth, tile.PixelHeight),
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        return brush;
    }

    // The glass panels' backdrop: the slab as the window shows it, blurred, made once as one
    // picture the size of the window (the window is fixed, and the slab never moves). It is
    // made a little larger and cut down, so that the blur does not fade at the window's edges.
    BitmapSource Blurred(BitmapSource tile)
    {
        const int pad = 24;
        int width = (int)Math.Ceiling(Root.ActualWidth > 0 ? Root.ActualWidth : Width);
        int height = (int)Math.Ceiling(Root.ActualHeight > 0 ? Root.ActualHeight : Height);
        var slab = new Rectangle
        {
            Width = width + 2 * pad,
            Height = height + 2 * pad,
            Fill = Tiled(tile, pad),
            Opacity = SlabOpacity,
            Effect = new BlurEffect { Radius = 10, KernelType = KernelType.Gaussian },
        };
        slab.Measure(new Size(slab.Width, slab.Height));
        slab.Arrange(new Rect(0, 0, slab.Width, slab.Height));
        var picture = new RenderTargetBitmap(width + 2 * pad, height + 2 * pad, 96, 96, PixelFormats.Pbgra32);
        picture.Render(slab);
        var cut = new CroppedBitmap(picture, new Int32Rect(pad, pad, width, height));
        cut.Freeze();
        return cut;
    }

    // --- The pages ---------------------------------------------------------------------------------

    async void Play_Click(object sender, RoutedEventArgs e) => await model.PlayAsync(new WindowInteropHelper(this).Handle);

    // The Settings page lists the resolutions that fit the monitor the launcher is on as it comes up.
    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Tabs && Tabs.SelectedItem == SettingsTab)
            model.ShowResolutions(new WindowInteropHelper(this).Handle);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await model.CheckServerAsync();

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the Yu-Gi-Oh! ONLINE folder, the one that holds online_pc.dat" };
        if (model.Game is { } game && Directory.Exists(game.Path))
            dialog.InitialDirectory = game.Path;
        if (dialog.ShowDialog(this) == true)
            model.ChooseFolder(dialog.FolderName);
    }

    void AccountLink_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;

    void NewAccount_Click(object sender, RoutedEventArgs e) => MainViewModel.OpenCreateAccount();

    void Forgot_Click(object sender, RoutedEventArgs e) => MainViewModel.OpenResetPassword();

    void Project_Click(object sender, RoutedEventArgs e) => MainViewModel.Open(Releases.ProjectPage);

    // --- The launcher's own updates ----------------------------------------------------------------

    async void UpdateNow_Click(object sender, RoutedEventArgs e) => await model.UpdateNowAsync();

    async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await model.CheckOrUpdateAsync();

    void NotNow_Click(object sender, RoutedEventArgs e) => model.DismissPrompt();

    void ReleasePage_Click(object sender, RoutedEventArgs e) => MainViewModel.Open(model.UpdatePage);

    // --- The caption ---------------------------------------------------------------------------------

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
