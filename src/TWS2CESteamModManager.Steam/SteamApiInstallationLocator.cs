using TWS2CESteamModManager.Core;
using Steamworks;

namespace TWS2CESteamModManager.Steam;

/// <summary>Optional native locator. The caller must already own an initialized Steam API session.</summary>
public sealed class SteamApiInstallationLocator : IGameInstallationLocator
{
    public IReadOnlyList<string> FindInstallations()
    {
        var appId = new AppId_t(GameConstants.GameAppId);
        if (!SteamApps.BIsAppInstalled(appId)) return Array.Empty<string>();
        if (SteamApps.GetAppInstallDir(appId, out string path, 32768) == 0
            || !GameLocationService.IsGameInstallation(path)) return Array.Empty<string>();
        return new[] { Path.GetFullPath(path) };
    }
}
