using System.Diagnostics;
using Steamworks;

namespace TWS2CESteamModManager.Steam;

public sealed record WorkshopItem(
    ulong Id, string Title, string Description, ulong AuthorSteamId,
    DateTimeOffset UpdatedAt, string Tags, uint VotesUp, uint VotesDown,
    string? PreviewUrl, string? AuthorName = null)
{
    public string Url => $"https://steamcommunity.com/sharedfiles/filedetails/?id={Id}";
}

public sealed record WorkshopPage(uint Page, uint TotalMatchingItems, IReadOnlyList<WorkshopItem> Items);

public static class WorkshopCatalog
{
    // The caller owns SteamAPI.Init/Shutdown. Keep this query and its callback pump
    // on the calling thread. SteamWorkshopBrowserService supplies the UI worker thread.
    public static WorkshopPage Fetch(WorkshopOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Page == 0)
            throw new ArgumentException("Page must start at 1.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();

        UGCQueryHandle_t query = SteamUGC.CreateQueryAllUGCRequest(
            options.QueryType, EUGCMatchingUGCType.k_EUGCMatchingUGCType_Items,
            AppId_t.Invalid, new AppId_t(34330), options.Page);

        if (query == UGCQueryHandle_t.Invalid)
            throw new InvalidOperationException("Steam could not create the SHOGUN 2 Workshop query.");

        try
        {
            if (options.Sort == "popular" && !SteamUGC.SetRankedByTrendDays(query, 7))
                throw new InvalidOperationException("Steam could not configure popular items for the past week.");
            if (options.Search is not null && !SteamUGC.SetSearchText(query, options.Search))
                throw new InvalidOperationException("Steam could not configure the Workshop search.");

            // Apply categories on Steam's query so paging and totals reflect the filter.
            if (options.Tags.Count > 0)
            {
                if (!SteamUGC.SetMatchAnyTag(query, false))
                    throw new InvalidOperationException("Steam could not configure category matching.");
                foreach (string tag in options.Tags)
                {
                    if (!SteamUGC.AddRequiredTag(query, tag))
                        throw new InvalidOperationException($"Steam could not apply category: {tag}.");
                }
            }

            SteamUGCQueryCompleted_t? completion = null;
            bool ioFailure = false;
            using var callResult = CallResult<SteamUGCQueryCompleted_t>.Create((result, failed) =>
            {
                completion = result;
                ioFailure = failed;
            });

            SteamAPICall_t call = SteamUGC.SendQueryUGCRequest(query);
            if (call == SteamAPICall_t.Invalid)
                throw new InvalidOperationException("Steam could not send the Workshop query.");
            callResult.Set(call);

            var timer = Stopwatch.StartNew();
            while (completion is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed >= TimeSpan.FromSeconds(30))
                    throw new TimeoutException("Workshop query timed out after 30 seconds. Check Steam's connection and try again.");

                SteamAPI.RunCallbacks();
                if (completion is null)
                    Thread.Sleep(10);
            }

            SteamUGCQueryCompleted_t response = completion.Value;
            if (ioFailure || response.m_eResult != EResult.k_EResultOK)
                throw new InvalidOperationException($"Workshop query failed: {(ioFailure ? "Steam I/O failure" : response.m_eResult.ToString())}.");

            var items = new List<WorkshopItem>();
            for (uint index = 0; index < response.m_unNumResultsReturned; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SteamUGC.GetQueryUGCResult(query, index, out SteamUGCDetails_t details)
                    || details.m_eResult != EResult.k_EResultOK)
                    throw new InvalidOperationException($"Steam could not read Workshop result {index + 1}.");

                if (details.m_nConsumerAppID.m_AppId != 34330)
                    throw new InvalidOperationException("Steam returned an item for a different game.");

                bool hasPreview = SteamUGC.GetQueryUGCPreviewURL(query, index, out string previewUrl, 4096);
                items.Add(new WorkshopItem(
                    details.m_nPublishedFileId.m_PublishedFileId,
                    details.m_rgchTitle, details.m_rgchDescription, details.m_ulSteamIDOwner,
                    DateTimeOffset.FromUnixTimeSeconds(details.m_rtimeUpdated),
                    details.m_rgchTags, details.m_unVotesUp, details.m_unVotesDown,
                    hasPreview ? previewUrl : null));
            }

            ResolveAuthors(items, cancellationToken);
            return new WorkshopPage(options.Page, response.m_unTotalMatchingResults, items.AsReadOnly());
        }
        finally
        {
            SteamUGC.ReleaseQueryUGCRequest(query);
        }
    }

    internal static void ResolveAuthors(List<WorkshopItem> items, CancellationToken cancellationToken)
    {
        var pending = new HashSet<ulong>();
        using var callback = Callback<PersonaStateChange_t>.Create(change => pending.Remove(change.m_ulSteamID));
        foreach (ulong id in items.Select(item => item.AuthorSteamId).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SteamFriends.RequestUserInformation(new CSteamID(id), true)) pending.Add(id);
        }
        var timer = Stopwatch.StartNew();
        while (pending.Count > 0 && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SteamAPI.RunCallbacks();
            if (pending.Count > 0) Thread.Sleep(10);
        }
        for (int index = 0; index < items.Count; index++)
        {
            string name = SteamFriends.GetFriendPersonaName(new CSteamID(items[index].AuthorSteamId));
            items[index] = items[index] with
            {
                AuthorName = string.IsNullOrWhiteSpace(name) || name is "[unknown]" or "[unknown user]"
                    ? $"Steam user {items[index].AuthorSteamId}" : name
            };
        }
    }
}
