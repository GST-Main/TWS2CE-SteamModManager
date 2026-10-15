using System.Globalization;
using Steamworks;

namespace TWS2CESteamModManager.Steam;

public sealed record WorkshopOptions(uint Page = 1, string? Search = null, string Sort = "updated")
{
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public EUGCQuery QueryType => Sort switch
    {
        "popular" => EUGCQuery.k_EUGCQuery_RankedByTrend,
        "rated" => EUGCQuery.k_EUGCQuery_RankedByVote,
        "updated" => EUGCQuery.k_EUGCQuery_RankedByLastUpdatedDate,
        "newest" => EUGCQuery.k_EUGCQuery_RankedByPublicationDate,
        "subscribed" => EUGCQuery.k_EUGCQuery_RankedByTotalUniqueSubscriptions,
        _ => throw new ArgumentException("Sort must be popular, subscribed, rated, newest, or updated.")
    };

    public static WorkshopOptions Parse(string[] arguments)
    {
        var options = new WorkshopOptions();
        var tags = new List<string>();
        for (int index = 0; index < arguments.Length; index++)
        {
            string name = arguments[index];
            if (name is not ("--page" or "--search" or "--sort" or "--tag"))
                throw new ArgumentException($"Unknown option: {name}. Use --help for usage.");

            if (++index >= arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{name} requires a value.");

            string value = arguments[index];
            switch (name)
            {
                case "--page":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint page) || page == 0)
                        throw new ArgumentException("Page must be a positive whole number, starting at 1.");
                    options = options with { Page = page };
                    break;
                case "--search":
                    if (string.IsNullOrWhiteSpace(value))
                        throw new ArgumentException("Search text cannot be empty.");
                    options = options with { Search = value };
                    break;
                case "--sort":
                    options = options with { Sort = value.ToLowerInvariant() };
                    break;
                case "--tag":
                    string tag = value.Trim().ToLowerInvariant();
                    if (tag.Length is 0 or > 255 || tag.Contains(',') || tag.Any(char.IsControl))
                        throw new ArgumentException("Each --tag must contain one category (1 to 255 characters, without commas or control characters). Repeat --tag for multiple categories.");
                    if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        tags.Add(tag);
                    break;
            }
        }

        _ = options.QueryType;
        return options with { Tags = tags.AsReadOnly() };
    }
}
