using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TWS2CESteamModManager.Steam;

public sealed record WorkshopTag(string Name, string DisplayName, string Group, ulong ItemCount);

public static class WorkshopTags
{
    public const string SourceUrl = "https://steamcommunity.com/app/34330/workshop/?l=english";
    private static readonly HttpClient Client = CreateClient();
    private static readonly Regex PageData = new(
        "<script\\b[^>]*\\bid\\s*=\\s*[\"']valve-ssr-data[\"'][^>]*>(?<json>.*?)</script\\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 8 * 1024 * 1024
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TWS2CESteamModManager/0.1");
        return client;
    }

    public static async Task<IReadOnlyList<WorkshopTag>> FetchAsync(CancellationToken cancellationToken = default)
    {
        string html = await Client.GetStringAsync(SourceUrl, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ParsePage(html);
    }

    // Read structured page data rather than inferring categories from a sample of items.
    // This is a public website schema, so fail clearly if Steam changes it.
    public static IReadOnlyList<WorkshopTag> ParsePage(string html)
    {
        try
        {
            Match match = PageData.Match(html);
            if (!match.Success)
                throw FormatChanged();
            using JsonDocument page = JsonDocument.Parse(match.Groups["json"].Value);
            if (!page.RootElement.TryGetProperty("loaderData", out JsonElement loaders)
                || loaders.ValueKind != JsonValueKind.Array)
                throw FormatChanged();

            JsonElement? declared = null;
            JsonElement? facets = null;
            foreach (JsonElement value in loaders.EnumerateArray())
            {
                // Steam currently serializes loader records as JSON strings.
                // Also accept object records without depending on their array position.
                using JsonDocument loader = JsonDocument.Parse(
                    value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
                JsonElement root = loader.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("app", out JsonElement app)
                    || app.ValueKind != JsonValueKind.Object
                    || !app.TryGetProperty("appid", out JsonElement appId)
                    || ReadCount(appId) != 34330)
                    continue;

                if (root.TryGetProperty("declaredTags", out JsonElement tags)
                    && tags.ValueKind == JsonValueKind.Object
                    && tags.TryGetProperty("declared_tags", out JsonElement groups))
                    declared = groups.Clone();
                if (root.TryGetProperty("rgTagFacets", out JsonElement counts))
                    facets = counts.Clone();
            }

            if (declared is not { ValueKind: JsonValueKind.Array } categories
                || facets is not { ValueKind: JsonValueKind.Array } tagCounts)
                throw FormatChanged();

            var countsByName = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement facet in tagCounts.EnumerateArray())
                countsByName[ReadText(facet, "value")] = ReadCount(facet.GetProperty("count"));

            var result = new List<WorkshopTag>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement group in categories.EnumerateArray())
            {
                string groupName = ReadText(group, "name");
                if (groupName.Length == 0)
                    groupName = "Categories";
                foreach (JsonElement tag in group.GetProperty("tags").EnumerateArray())
                {
                    if (tag.TryGetProperty("admin_only", out JsonElement adminOnly) && adminOnly.GetBoolean())
                        continue;
                    string name = ReadText(tag, "name").Trim();
                    if (name.Length is 0 or > 255 || name.Contains(',') || name.Any(char.IsControl))
                        throw FormatChanged();
                    if (!names.Add(name))
                        continue;
                    string display = ReadText(tag, "display_name");
                    result.Add(new WorkshopTag(name, display.Length == 0 ? name : display, groupName,
                        countsByName.GetValueOrDefault(name)));
                }
            }
            return result.AsReadOnly();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or RegexMatchTimeoutException)
        {
            throw FormatChanged(exception);
        }
    }

    private static string ReadText(JsonElement value, string property) =>
        value.GetProperty(property).GetString() ?? throw FormatChanged();

    private static ulong ReadCount(JsonElement value)
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong count)
            ? count : throw FormatChanged();
    }

    private static InvalidOperationException FormatChanged(Exception? inner = null) =>
        new("Steam's Workshop category data is missing or has an unexpected format. The page parser may need updating.", inner);
}
