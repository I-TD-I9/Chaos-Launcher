using System.Windows;
using System.Windows.Threading;

namespace Chaos.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        LauncherSettings.MoveOldFolder();
        base.OnStartup(e);
    }

    // Report uncaught errors and keep the launcher open.
    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Something went wrong: {e.Exception.GetBaseException().Message}", "Chaos Launcher",
                        MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        if (MainWindow is not { IsLoaded: true })
            Shutdown(1);
    }
}
