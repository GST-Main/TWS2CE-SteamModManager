using Steamworks;

namespace TWS2CESteamModManager.Steam;

public sealed record WorkshopScreenshot(string Url, string Caption);
public sealed record WorkshopItemDetails(WorkshopItem Item, IReadOnlyList<WorkshopScreenshot> Screenshots)
{
    public string Description => Item.Description;
}

public static class WorkshopDetails
{
    // Like WorkshopCatalog.Fetch, this runs on the initialized Steam thread.
    public static WorkshopItemDetails Fetch(ulong itemId, CancellationToken cancellationToken = default)
    {
        if (itemId is 0 or ulong.MaxValue) throw new ArgumentException("Invalid Workshop item ID.", nameof(itemId));
        cancellationToken.ThrowIfCancellationRequested();
        var id = new PublishedFileId_t(itemId);
        UGCQueryHandle_t query = SteamUGC.CreateQueryUGCDetailsRequest([id], 1);
        if (query == UGCQueryHandle_t.Invalid) throw new InvalidOperationException("Steam could not create the item details query.");
        try
        {
            if (!SteamUGC.SetReturnLongDescription(query, true) || !SteamUGC.SetReturnAdditionalPreviews(query, true))
                throw new InvalidOperationException("Steam could not configure the full item details query.");
            SteamUGCQueryCompleted_t response = SteamCall.Wait<SteamUGCQueryCompleted_t>(
                () => SteamUGC.SendQueryUGCRequest(query), TimeSpan.FromSeconds(30), cancellationToken);
            if (response.m_eResult != EResult.k_EResultOK || response.m_unNumResultsReturned != 1)
                throw new InvalidOperationException($"Item lookup failed: {response.m_eResult}.");
            if (!SteamUGC.GetQueryUGCResult(query, 0, out SteamUGCDetails_t details) || details.m_eResult != EResult.k_EResultOK)
                throw new InvalidOperationException("This Workshop item is unavailable.");
            if (details.m_nPublishedFileId != id || details.m_nConsumerAppID.m_AppId != 34330 || details.m_bBanned)
                throw new InvalidOperationException("This is not an available SHOGUN 2 Workshop item.");
            string? preview = SteamUGC.GetQueryUGCPreviewURL(query, 0, out string url, 4096) ? url : null;
            var items = new List<WorkshopItem> { new(itemId, details.m_rgchTitle, details.m_rgchDescription,
                details.m_ulSteamIDOwner, DateTimeOffset.FromUnixTimeSeconds(details.m_rtimeUpdated),
                details.m_rgchTags, details.m_unVotesUp, details.m_unVotesDown, preview) };
            WorkshopCatalog.ResolveAuthors(items, cancellationToken);
            var screenshots = new List<WorkshopScreenshot>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string? imageUrl, string caption)
            {
                if (Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? image) && image.Scheme is "https" or "http" && seen.Add(imageUrl!))
                    screenshots.Add(new WorkshopScreenshot(imageUrl!, caption));
            }
            Add(preview, "Main preview");
            uint count = SteamUGC.GetQueryUGCNumAdditionalPreviews(query, 0);
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SteamUGC.GetQueryUGCAdditionalPreview(query, 0, index, out string imageUrl, 4096,
                    out string filename, 1024, out EItemPreviewType type) && type == EItemPreviewType.k_EItemPreviewType_Image)
                    Add(imageUrl, string.IsNullOrWhiteSpace(filename) ? $"Screenshot {index + 1}" : filename);
            }
            return new WorkshopItemDetails(items[0], screenshots.AsReadOnly());
        }
        finally { SteamUGC.ReleaseQueryUGCRequest(query); }
    }
}
