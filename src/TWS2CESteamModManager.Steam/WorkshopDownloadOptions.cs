using System.Globalization;

namespace TWS2CESteamModManager.Steam;

public sealed record WorkshopDownloadOptions(ulong ItemId, string OutputRoot, bool DryRun, TimeSpan Timeout)
{
    public static WorkshopDownloadOptions Parse(string[] arguments)
    {
        if (arguments.Length == 0)
            throw new ArgumentException("Download requires a Workshop item ID or item-page URL.");

        ulong itemId = ParseItemId(arguments[0]);
        string output = Path.GetFullPath("downloads");
        bool dryRun = false;
        int timeoutSeconds = 600;
        for (int index = 1; index < arguments.Length; index++)
        {
            string option = arguments[index];
            if (option == "--dry-run")
            {
                dryRun = true;
                continue;
            }
            if (option is not ("--output" or "--timeout"))
                throw new ArgumentException($"Unknown download option: {option}.");
            if (++index >= arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{option} requires a value.");

            if (option == "--output")
            {
                if (string.IsNullOrWhiteSpace(arguments[index]))
                    throw new ArgumentException("Output folder cannot be empty.");
                output = Path.GetFullPath(arguments[index]);
            }
            else if (!int.TryParse(arguments[index], NumberStyles.None, CultureInfo.InvariantCulture, out timeoutSeconds)
                     || timeoutSeconds is < 1 or > 86400)
                throw new ArgumentException("Download timeout must be between 1 and 86400 seconds.");
        }

        return new WorkshopDownloadOptions(itemId, output, dryRun, TimeSpan.FromSeconds(timeoutSeconds));
    }

    public static ulong ParseItemId(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https"
            && (uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase))
            && uri.AbsolutePath.TrimEnd('/') is "/sharedfiles/filedetails" or "/workshop/filedetails")
        {
            string[] ids = uri.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2))
                .Where(pair => pair.Length == 2 && pair[0] == "id")
                .Select(pair => Uri.UnescapeDataString(pair[1])).ToArray();
            value = ids.Length == 1 ? ids[0] : "";
        }

        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
            || id is 0 or ulong.MaxValue)
            throw new ArgumentException("Use a positive Workshop item ID or a Steam Community item-page URL containing id=NUMBER.");
        return id;
    }
}
