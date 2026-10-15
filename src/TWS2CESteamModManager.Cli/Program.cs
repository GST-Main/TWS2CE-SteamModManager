using Steamworks;
using TWS2CESteamModManager.Steam;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;
WorkshopOptions? workshopOptions = null;
WorkshopDownloadOptions? downloadOptions = null;
bool fetchTags = false;
bool includeEmptyTags = false;
try
{
    if (args.Contains("--help") || args.Contains("-h"))
    {
        Console.WriteLine("TWS2CESteamModManager.Cli                         Check Steam connectivity");
        Console.WriteLine("TWS2CESteamModManager.Cli tags [--all]             List Workshop categories and counts; --all includes empty categories");
        Console.WriteLine("TWS2CESteamModManager.Cli workshop [--page N] [--search TEXT] [--sort popular|subscribed|rated|newest|updated] [--tag CATEGORY]...");
        Console.WriteLine("Repeat --tag to require all selected categories (e.g. --tag map --tag multiplayer).");
        Console.WriteLine("TWS2CESteamModManager.Cli download ITEM_ID_OR_URL [--output FOLDER] [--timeout SECONDS] [--dry-run]");
        Console.WriteLine("Fetches up to 50 public SHOGUN 2 Workshop items per page. No API key required.");
        Console.WriteLine("Downloads into FOLDER/<item ID>/ (default: ./downloads). --dry-run only inspects metadata.");
        return 0;
    }

    if (args.Length > 0)
    {
        switch (args[0])
        {
            case "tags":
                if (args.Length > 2 || (args.Length == 2 && args[1] != "--all"))
                    throw new ArgumentException("Usage: TWS2CESteamModManager.Cli tags [--all]");
                fetchTags = true;
                includeEmptyTags = args.Length == 2;
                break;
            case "workshop":
                workshopOptions = WorkshopOptions.Parse(args[1..]);
                break;
            case "download":
                // Resolve output paths before switching to the executable directory.
                downloadOptions = WorkshopDownloadOptions.Parse(args[1..]);
                break;
            default:
                throw new ArgumentException("Unknown command. Use --help for usage.");
        }
    }
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

// Category discovery uses the public web page and runs independently of the Steam client.
if (fetchTags)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        IReadOnlyList<WorkshopTag> available = await WorkshopTags.FetchAsync(cancellation.Token);
        WorkshopTag[] tags = available.Where(tag => includeEmptyTags || tag.ItemCount > 0).ToArray();
        Console.WriteLine($"SHOGUN 2 Workshop categories ({tags.Length}):");
        foreach (WorkshopTag tag in tags)
            Console.WriteLine($"  {PlainText(tag.Name)} ({tag.ItemCount:N0})");
        Console.WriteLine("\nFilter items with: workshop --tag CATEGORY");
        Console.WriteLine($"Source: {WorkshopTags.SourceUrl}");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine(cancellation.IsCancellationRequested
            ? "Tag request cancelled." : "Tag request timed out after 30 seconds.");
        return 1;
    }
    catch (HttpRequestException exception)
    {
        Console.Error.WriteLine($"Could not fetch Workshop categories: {exception.Message}");
        return 1;
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
{
    Console.Error.WriteLine("TWS2CESteamModManager.Cli requires 64-bit Windows.");
    return 1;
}

// Steam looks for steam_appid.txt in the working directory when run outside Steam.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

const uint shogun2AppId = 34330;
bool initialized = false;

try
{
    Console.WriteLine("Checking Steam connectivity for Total War: SHOGUN 2...");
    initialized = SteamAPI.Init();

    if (!initialized)
    {
        Console.Error.WriteLine("Steam initialization failed. Check that:");
        Console.Error.WriteLine("- Steam is running and signed in to an account that owns SHOGUN 2.");
        Console.Error.WriteLine("- Steam and this program run as the same Windows user and elevation level.");
        Console.Error.WriteLine("- steam_appid.txt is beside the executable and contains 34330.");
        return 1;
    }

    uint appId = SteamUtils.GetAppID().m_AppId;
    Console.WriteLine($"Connected as: {SteamFriends.GetPersonaName()}");
    Console.WriteLine($"App ID: {appId}");

    if (appId != shogun2AppId)
    {
        Console.Error.WriteLine("Unexpected App ID. Check steam_appid.txt and SteamAppId environment settings.");
        return 1;
    }

    Console.WriteLine("Steam initialization succeeded.");

    if (workshopOptions is not null || downloadOptions is not null)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (downloadOptions is not null)
            {
                if (downloadOptions.DryRun)
                {
                    WorkshopDownloadInfo info = WorkshopDownloader.Inspect(downloadOptions, cancellation.Token);
                    Console.WriteLine($"Item: {info.ItemId} | {PlainText(info.Title)}");
                    Console.WriteLine($"Content format: {(info.Legacy ? "legacy single file" : "Steam Workshop folder")}");
                    Console.WriteLine($"Size reported by Steam: {info.SizeBytes:N0} bytes");
                    Console.WriteLine($"Destination: {info.Destination}");
                    Console.WriteLine("Dry run complete. No content downloaded.");
                }
                else
                {
                    WorkshopDownloadResult result = WorkshopDownloader.Download(downloadOptions,
                        message => Console.WriteLine(PlainText(message)), cancellation.Token);
                    Console.WriteLine($"Download complete: {result.FileCount} files, {result.SizeBytes:N0} bytes.");
                    Console.WriteLine($"Saved to: {result.Directory}");
                }
                return 0;
            }

            // This branch is reached only for a catalog query.
            WorkshopOptions catalogOptions = workshopOptions!;
            Console.WriteLine($"Fetching Workshop page {catalogOptions.Page} (sort: {catalogOptions.Sort})...");
            if (catalogOptions.Tags.Count > 0)
                Console.WriteLine($"Required categories (all): {string.Join(", ", catalogOptions.Tags)}");
            WorkshopPage page = WorkshopCatalog.Fetch(catalogOptions, cancellation.Token);
            Console.WriteLine($"Page {page.Page}: {page.Items.Count} items; {page.TotalMatchingItems} matching items in total.");
            foreach (WorkshopItem item in page.Items)
            {
                // Workshop titles and tags are user content; strip terminal control characters.
                Console.WriteLine($"\n{PlainText(item.Title)}");
                Console.WriteLine($"  ID: {item.Id} | Author Steam ID: {item.AuthorSteamId}");
                Console.WriteLine($"  Updated: {item.UpdatedAt:yyyy-MM-dd HH:mm} UTC | Votes: +{item.VotesUp} / -{item.VotesDown}");
                Console.WriteLine($"  Tags: {PlainText(item.Tags)}");
                Console.WriteLine($"  {item.Url}");
            }

            if (page.Items.Count == 0)
                Console.WriteLine("No items on this page. Try page 1 or different search/category filters.");
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Workshop request cancelled.");
    return 1;
}
catch (TimeoutException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (IOException exception)
{
    Console.Error.WriteLine($"File operation failed: {exception.Message}");
    return 1;
}
catch (UnauthorizedAccessException exception)
{
    Console.Error.WriteLine($"Cannot access the download folder: {exception.Message}");
    return 1;
}
catch (DllNotFoundException)
{
    Console.Error.WriteLine("Could not load steam_api64.dll. Rebuild and check the output directory.");
    return 1;
}

catch (BadImageFormatException)
{
    Console.Error.WriteLine("Steam library architecture mismatch. Use the Windows x64 Steamworks.NET libraries.");
    return 1;
}
catch (EntryPointNotFoundException)
{
    Console.Error.WriteLine("Steam library version mismatch. Use both DLLs from the same Steamworks.NET release.");
    return 1;
}
finally
{
    if (initialized)
    {
        SteamAPI.Shutdown();
    }
}

static string PlainText(string value) => new(value.Where(character => !char.IsControl(character)).ToArray());
