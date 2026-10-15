using TWS2CESteamModManager.Core;

namespace TWS2CESteamModManager.Steam;

/// <summary>Bridges the existing Steam client downloader into the core mod manager.
/// The caller owns Steam initialization and calls Download on the Steam thread.</summary>
public sealed class SteamWorkshopModDownloader(TimeSpan? timeout = null, Func<string, object?[], string>? localize = null) : IWorkshopModDownloader
{
    public WorkshopModPackage Download(ulong itemId, string cacheDirectory,
        Action<string>? report = null, CancellationToken cancellationToken = default)
    {
        string session = Path.Combine(Path.GetFullPath(cacheDirectory), Guid.NewGuid().ToString("N"));
        var options = new WorkshopDownloadOptions(itemId, session, false, timeout ?? TimeSpan.FromMinutes(10));
        WorkshopDownloadResult result = WorkshopDownloader.Download(options, report, cancellationToken, localize);
        string[] files = Directory.EnumerateFiles(result.Directory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".pack", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new WorkshopModPackage(result.Directory, files, result.ThumbnailPath, result.Title);
    }
}
