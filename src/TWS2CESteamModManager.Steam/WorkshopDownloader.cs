using Steamworks;

namespace TWS2CESteamModManager.Steam;

public sealed record WorkshopDownloadInfo(ulong ItemId, string Title, ulong SizeBytes, bool Legacy, string Destination);
public sealed record WorkshopDownloadResult(string Directory, int FileCount, ulong SizeBytes, string? ThumbnailPath = null, string? Title = null);

public static class WorkshopDownloader
{
    public static WorkshopDownloadInfo Inspect(WorkshopDownloadOptions options, CancellationToken cancellationToken = default)
    {
        SteamUGCDetails_t details = FetchDetails(options.ItemId, cancellationToken, out _);
        return Describe(options, details);
    }

    public static WorkshopDownloadResult Download(WorkshopDownloadOptions options,
        Action<string>? report = null, CancellationToken cancellationToken = default,
        Func<string, object?[], string>? localize = null)
    {
        if (options.DryRun)
            throw new ArgumentException("Use Inspect for a dry run.", nameof(options));

        SteamUGCDetails_t details = FetchDetails(options.ItemId, cancellationToken, out string? previewUrl);
        WorkshopDownloadInfo info = Describe(options, details);
        RejectLinkedAncestors(info.Destination);
        if (Directory.Exists(info.Destination) || File.Exists(info.Destination))
            throw new IOException($"Destination already exists: {info.Destination}. Choose another --output folder.");

        Directory.CreateDirectory(options.OutputRoot);
        string staging = Path.Combine(options.OutputRoot, $".{options.ItemId}.{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(staging);
        report?.Invoke(ProgressText(localize, "status.downloadingItem", "Downloading item {0} ({1:N0} bytes reported by Steam)...", info.ItemId, info.SizeBytes));
        try
        {
            if (info.Legacy)
                DownloadLegacy(details, staging, options.Timeout, report, cancellationToken, localize);
            else
                DownloadModern(options.ItemId, staging, options.Timeout, report, cancellationToken, localize);

            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any())
                throw new IOException("Steam completed the download but no content files were found.");
            string? thumbnail = null;
            if (!string.IsNullOrWhiteSpace(previewUrl))
            {
                report?.Invoke(ProgressText(localize, "status.downloadingThumbnail", "Downloading the Workshop thumbnail..."));
                try
                {
                    thumbnail = WorkshopThumbnailDownloader.DownloadAsync(previewUrl,
                        Path.Combine(staging, $".workshop-thumbnail-{Guid.NewGuid():N}"), cancellationToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or IOException or OperationCanceledException or ArgumentException)
                { report?.Invoke(ProgressText(localize, "status.thumbnailUnavailable", "Thumbnail unavailable; keeping the mod download: {0}", exception.Message)); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo[] files = new DirectoryInfo(staging).GetFiles("*", SearchOption.AllDirectories);
            if (files.Length == 0)
                throw new IOException("Steam completed the download but no content files were found.");
            ulong size = 0;
            foreach (FileInfo file in files)
                size = checked(size + (ulong)file.Length);
            Directory.Move(staging, info.Destination);
            return new WorkshopDownloadResult(info.Destination, files.Length, size,
                thumbnail is null ? null : Path.Combine(info.Destination, Path.GetFileName(thumbnail)), info.Title);
        }
        catch
        {
            // Steam may still be writing after timeout/cancellation. Retain staging
            // instead of racing the native downloader or exposing partial content as complete.
            report?.Invoke(ProgressText(localize, "status.partialDownload", "Incomplete files may remain at: {0}", staging));
            throw;
        }
    }

    private static WorkshopDownloadInfo Describe(WorkshopDownloadOptions options, SteamUGCDetails_t details)
    {
        bool legacy = details.m_hFile != UGCHandle_t.Invalid && details.m_hFile.m_UGCHandle != 0;
        ulong size = legacy ? (ulong)Math.Max(0, details.m_nFileSize) : details.m_ulTotalFilesSize;
        return new WorkshopDownloadInfo(options.ItemId, details.m_rgchTitle, size, legacy,
            Path.Combine(Path.GetFullPath(options.OutputRoot), options.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static SteamUGCDetails_t FetchDetails(ulong id, CancellationToken cancellationToken, out string? previewUrl)
    {
        if (id is 0 or ulong.MaxValue)
            throw new ArgumentException("Invalid Workshop item ID.", nameof(id));
        var publishedId = new PublishedFileId_t(id);
        UGCQueryHandle_t query = SteamUGC.CreateQueryUGCDetailsRequest([publishedId], 1);
        if (query == UGCQueryHandle_t.Invalid)
            throw new InvalidOperationException("Steam could not create the item details query.");
        try
        {
            SteamUGCQueryCompleted_t response = SteamCall.Wait<SteamUGCQueryCompleted_t>(
                () => SteamUGC.SendQueryUGCRequest(query), TimeSpan.FromSeconds(30), cancellationToken);
            if (response.m_eResult != EResult.k_EResultOK)
                throw new InvalidOperationException($"Item lookup failed: {response.m_eResult}.");
            if (response.m_unNumResultsReturned != 1
                || !SteamUGC.GetQueryUGCResult(query, 0, out SteamUGCDetails_t details))
                throw new InvalidOperationException("Steam returned no details for this item.");
            if (details.m_eResult != EResult.k_EResultOK)
                throw new InvalidOperationException($"Item is unavailable: {details.m_eResult}.");
            if (details.m_nPublishedFileId != publishedId || details.m_nConsumerAppID.m_AppId != 34330)
                throw new InvalidOperationException("This is not a SHOGUN 2 Workshop item.");
            if (details.m_bBanned)
                throw new InvalidOperationException("This Workshop item has been banned.");
            if (details.m_eFileType != EWorkshopFileType.k_EWorkshopFileTypeCommunity)
                throw new InvalidOperationException("Select an individual mod or map. This item type is not downloadable by this command.");
            previewUrl = SteamUGC.GetQueryUGCPreviewURL(query, 0, out string url, 4096) ? url : null;
            return details;
        }
        finally
        {
            SteamUGC.ReleaseQueryUGCRequest(query);
        }
    }

    private static void DownloadLegacy(SteamUGCDetails_t details, string staging, TimeSpan timeout,
        Action<string>? report, CancellationToken cancellationToken, Func<string, object?[], string>? localize)
    {
        // Discard any author-provided directory components before using a cloud filename.
        string name = Path.GetFileName(details.m_pchFileName);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' '))
            throw new IOException("Steam returned an invalid content filename.");
        string path = Path.Combine(staging, name);
        RemoteStorageDownloadUGCResult_t response = SteamCall.Wait<RemoteStorageDownloadUGCResult_t>(
            () => SteamRemoteStorage.UGCDownloadToLocation(details.m_hFile, path, 0),
            timeout, cancellationToken, () =>
            {
                if (SteamRemoteStorage.GetUGCDownloadProgress(details.m_hFile, out int downloaded, out int total))
                    report?.Invoke(ProgressText(localize, "status.downloadProgress", "Downloaded {0:N0} / {1:N0} bytes", downloaded, total));
                else
                    report?.Invoke(ProgressText(localize, "status.waitingSteam", "Waiting for Steam to download content..."));
            });
        if (response.m_eResult != EResult.k_EResultOK || response.m_hFile != details.m_hFile)
            throw new InvalidOperationException($"Content download failed: {response.m_eResult}.");
        ValidateLegacyDownload(path, response.m_nSizeInBytes, details.m_nFileSize);
    }

    internal static void ValidateLegacyDownload(string path, int completionSize, int metadataSize)
    {
        // UGCDownloadToLocation can succeed with a zero completion size even when
        // it wrote a nonempty file (for example, Workshop item 2039059067).
        // In that case verify against the size from the item details query.
        long expectedSize = completionSize == 0 ? metadataSize : completionSize;
        long actualSize = File.Exists(path) ? new FileInfo(path).Length : -1;
        if (actualSize < 0 || expectedSize < 0 || actualSize != expectedSize)
            throw new IOException($"Downloaded file size does not match Steam's reported size (file: {actualSize:N0}; completion: {completionSize:N0}; metadata: {metadataSize:N0} bytes).");
    }

    private static void DownloadModern(ulong id, string staging, TimeSpan timeout,
        Action<string>? report, CancellationToken cancellationToken, Func<string, object?[], string>? localize)
    {
        var publishedId = new PublishedFileId_t(id);
        DownloadItemResult_t? completion = null;
        using var callback = Callback<DownloadItemResult_t>.Create(result =>
        {
            if (result.m_unAppID.m_AppId == 34330 && result.m_nPublishedFileId == publishedId)
                completion = result;
        });
        // Normal priority preserves the user's existing Steam download queue.
        if (!SteamUGC.DownloadItem(publishedId, false))
            throw new InvalidOperationException("Steam could not start the item download. Check your login and access to the item.");
        SteamCall.PumpUntil(() => completion.HasValue, timeout, cancellationToken, () =>
        {
            if (SteamUGC.GetItemDownloadInfo(publishedId, out ulong downloaded, out ulong total))
                report?.Invoke(ProgressText(localize, "status.downloadProgress", "Downloaded {0:N0} / {1:N0} bytes", downloaded, total));
            else
                report?.Invoke(ProgressText(localize, "status.waitingQueue", "Waiting in Steam's download queue..."));
        });
        if (completion!.Value.m_eResult != EResult.k_EResultOK)
            throw new InvalidOperationException($"Item download failed: {completion.Value.m_eResult}.");
        if (!SteamUGC.GetItemInstallInfo(publishedId, out _, out string source, 32768, out _))
            throw new IOException("Steam completed the download but did not provide its local content path.");

        report?.Invoke(ProgressText(localize, "status.copyingContent", "Copying completed content from Steam's cache..."));
        CopyContent(source, staging, cancellationToken);
    }

    private static string ProgressText(Func<string, object?[], string>? localize, string key, string english, params object?[] arguments) =>
        localize?.Invoke(key, arguments) ?? string.Format(System.Globalization.CultureInfo.CurrentCulture, english, arguments);

    private static void CopyContent(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Workshop content contains a linked file or folder, which cannot be copied.");
        if (File.Exists(source))
        {
            using var input = File.OpenRead(source);
            using var output = new FileStream(Path.Combine(destination, Path.GetFileName(source)), FileMode.CreateNew);
            byte[] buffer = new byte[81920];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
            return;
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Workshop content contains a linked file or folder, which cannot be copied.");
            string target = destination;
            if (Directory.Exists(entry))
            {
                target = Path.Combine(destination, Path.GetFileName(entry));
                Directory.CreateDirectory(target);
            }
            CopyContent(entry, target, cancellationToken);
        }
    }

    private static void RejectLinkedAncestors(string destination)
    {
        for (string? path = Path.GetFullPath(destination); path is not null; path = Path.GetDirectoryName(path))
        {
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose an output folder without symbolic links or junctions.");
        }
    }
}
