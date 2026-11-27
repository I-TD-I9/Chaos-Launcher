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
    // How much of the slab shows through the tint.
    const double SlabOpacity = 0.40;

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
        // The new launcher is running, so close this one.
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
        // With acrylic, the desktop shows through.
        acrylic = Backdrop.TryAcrylic(this);
        Rim.Visibility = acrylic ? Visibility.Collapsed : Visibility.Visible;
        Fallback.Opacity = acrylic && IsActive ? 0 : 1;
    }

    // Glass is only see-through while the window is active.
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

    // Cancel the download before closing.
    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (model.UpdateStep != UpdateStep.Downloading)
            return;
        e.Cancel = true;
        closeAfterUpdate = true;
        model.CancelUpdate();
    }

    async Task LoadSlabAsync(string? folder)
    {
        if (folder == slabFolder)
            return;
        slabFolder = folder;
        int load = ++slabLoads;
        byte[]? bmp = await Task.Run(() => SlabTexture.Read(folder));
        // Skip if a newer load started.
        if (load == slabLoads)
            ShowSlab(bmp);
    }

    // Width of the game's own frame round the slab.
    const int SlabFrame = 6;

    // The game's slab is 504 x 280 with its frame.
    const int MaxSlabSize = 2048;

    static BitmapSource? Decode(byte[]? bmp)
    {
        if (bmp is null || bmp.Length < 26)
            return null;
        int width = BitConverter.ToInt32(bmp, 18), height = BitConverter.ToInt32(bmp, 22);
        if (width <= 0 || width > MaxSlabSize || height == 0 || height == int.MinValue || Math.Abs(height) > MaxSlabSize)
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
            return null;
        }
    }

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

    // Tiles the slab at its pixel size.
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

    // The glass panels' backdrop.
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

    async void Play_Click(object sender, RoutedEventArgs e) => await model.PlayAsync(new WindowInteropHelper(this).Handle);

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Tabs && Tabs.SelectedItem == SettingsTab)
            model.ShowResolutions(new WindowInteropHelper(this).Handle);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await model.CheckServerAsync();

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the Yu-Gi-Oh! ONLINE folder" };
        if (model.Game is { } game && Directory.Exists(game.Path))
            dialog.InitialDirectory = game.Path;
        if (dialog.ShowDialog(this) == true)
            model.ChooseFolder(dialog.FolderName);
    }

    void AccountLink_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;

    void Account_Click(object sender, RoutedEventArgs e) => MainViewModel.OpenAccount();

    void Project_Click(object sender, RoutedEventArgs e) => MainViewModel.Open(Releases.ProjectPage);

    async void UpdateNow_Click(object sender, RoutedEventArgs e) => await model.UpdateNowAsync();

    async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await model.CheckOrUpdateAsync();

    void NotNow_Click(object sender, RoutedEventArgs e) => model.DismissPrompt();

    void ReleasePage_Click(object sender, RoutedEventArgs e) => MainViewModel.Open(model.UpdatePage);

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
