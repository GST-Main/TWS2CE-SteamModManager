using System.IO;
using System.Windows;
using TWS2CESteamModManager.App.Services;
using TWS2CESteamModManager.App.Localization;

namespace TWS2CESteamModManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        L.Use(new LocalizationService());
        L.Current.ApplyCulture();
        // Closing the notice must not shut down an accepted launch before its
        // main window is created. Declining or closing it exits explicitly.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var preferences = new NoticePreferencesStore();
        if (preferences.ShouldShow() && new StartupNoticeWindow(preferences).ShowDialog() != true)
        {
            Shutdown();
            return;
        }
        MainWindow = new MainWindow(e.Args.Contains("--force-folder-setup", StringComparer.OrdinalIgnoreCase));
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow.Show();
    }
}
