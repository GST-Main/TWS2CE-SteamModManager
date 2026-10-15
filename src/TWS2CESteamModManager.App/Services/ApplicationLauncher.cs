using TWS2CESteamModManager.App.Localization;
using System.Diagnostics;
using System.IO;
using TWS2CESteamModManager.Core;

namespace TWS2CESteamModManager.App.Services;

public interface IApplicationLauncher
{
    void LaunchSteam();
    void LaunchGame(string gameFolder);
}

public sealed class ApplicationLauncher : IApplicationLauncher
{
    private readonly Action<ProcessStartInfo> start;

    public ApplicationLauncher(Action<ProcessStartInfo>? start = null)
    {
        this.start = start ?? (info => { using Process? process = Process.Start(info); });
    }

    public void LaunchSteam() => start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });

    public void LaunchGame(string gameFolder)
    {
        if (!Path.IsPathFullyQualified(gameFolder))
            throw new ArgumentException(L.Text("msg.selectGameFolder"), nameof(gameFolder));
        string directory = Path.GetFullPath(gameFolder);
        string executable = Path.Combine(directory, GameConstants.GameExecutable);
        if (!File.Exists(executable))
            throw new FileNotFoundException(L.Text("msg.gameExecutableMissing"), executable);
        // Let Steam create the game's launch context instead of starting the EXE
        // from the manager's Workshop session (which uses a different App ID).
        start(new ProcessStartInfo($"steam://run/{GameConstants.GameAppId}") { UseShellExecute = true });
    }
}
