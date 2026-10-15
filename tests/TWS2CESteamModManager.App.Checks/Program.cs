using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TWS2CESteamModManager.App;
using TWS2CESteamModManager.App.Localization;
using TWS2CESteamModManager.App.Services;
using TWS2CESteamModManager.App.ViewModels;
using TWS2CESteamModManager.Steam;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        // Most checks assert English labels, regardless of the machine's UI language.
        L.Use(new LocalizationService(uiCulture: CultureInfo.GetCultureInfo("en-US")));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/TWS2CESteamModManager;component/Theme.xaml", UriKind.Relative) });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        int exitCode = 0;
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                checks += ConfigChecks.Run();
                checks += WorkshopDownloadChecks.Run();
                checks += await NoticeChecks.RunAsync();
                await CheckBrowserAsync();
                checks += await ModIntegrationChecks.RunAsync(RenderAsync);
                checks += await LocalizationChecks.RunAsync(RenderAsync);
                if (args.Contains("--live")) await CheckLiveAsync();
                Console.WriteLine($"PASS: {checks} UI and integration checks.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        checks++;
    }

    private static WorkshopItem Item(string title = "Expanded campaign and historical units") => new(
        123, title, "Description", 456, DateTimeOffset.UtcNow, "historical,campaign,units", 1234, 25, null, "Workshop creator");

    private static async Task CheckBrowserAsync()
    {
        var service = new FakeBrowser();
        var launched = new List<ProcessStartInfo>();
        bool failLaunch = false;
        var launcher = new ApplicationLauncher(info =>
        {
            if (failLaunch) throw new System.ComponentModel.Win32Exception("Steam is not installed.");
            launched.Add(info);
        });
        using var vm = new WorkshopBrowserViewModel(service, new NoThumbnails(), launcher: launcher);
        await vm.InitializeAsync();
        Check(service.Queries.Count == 1 && service.Queries[0].Sort == "popular", "Initial query uses popular sorting.");
        Check(vm.Categories.Count == 3 && vm.Categories.All(c => c.Name is null || c.Count > 0), "Hide zero-count categories.");
        Check(vm.Items[0].Author.Contains("Workshop creator") && vm.Items[0].Tags.Count == 3, "Show author name and tags.");
        Check(vm.Items[0].Thumbnail is null && !vm.HasError, "Missing thumbnails leave usable results.");
        Check(vm.CanNext && !vm.CanPrevious && vm.PageText == "Page 1 of 3", "First page boundaries.");
        vm.SearchText = "  castle  ";
        Check(service.Queries.Count == 1, "Typing does not submit a search.");
        await vm.SubmitSearchAsync();
        Check(service.Queries[^1].Search == "castle" && service.Queries[^1].Page == 1, "Submit trims search and resets page.");
        vm.SearchText = "unsubmitted edit";
        vm.SelectedCategory = vm.Categories[1];
        Check(service.Queries[^1].Tags.SequenceEqual(new[] { "historical" }) && service.Queries[^1].Search == "castle", "One category and committed search are sent.");
        vm.SelectedCategory = vm.Categories[2];
        Check(service.Queries[^1].Tags.SequenceEqual(new[] { "map" }), "Category selection replaces the previous tag.");
        foreach (SortOption sort in vm.SortOptions)
        {
            vm.SelectedSort = sort;
            Check(service.Queries[^1].Sort == sort.Key, "Every dropdown sort reaches the API.");
        }
        await vm.LoadPageAsync(3);
        Check(vm.CanPrevious && !vm.CanNext && vm.CurrentPage == 3, "Last page boundaries.");
        vm.SelectedCategory = vm.Categories[0];
        Check(service.Queries[^1].Tags.Count == 0 && vm.CurrentPage == 1, "All categories clears filter and resets paging.");

        var oldResult = new TaskCompletionSource<WorkshopPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Handler = (options, _) => options.Search == "old" ? oldResult.Task
            : Task.FromResult(new WorkshopPage(1, 1, new[] { Item("New result") }));
        vm.SearchText = "old";
        Task oldRequest = vm.SubmitSearchAsync();
        vm.SearchText = "new";
        await vm.SubmitSearchAsync();
        oldResult.SetResult(new WorkshopPage(1, 1, new[] { Item("Obsolete result") }));
        await oldRequest;
        Check(vm.Items.Single().Title == "New result" && !vm.IsBusy, "Late stale results cannot replace newer results.");

        service.Handler = (_, _) => Task.FromException<WorkshopPage>(new InvalidOperationException("Steam is offline."));
        await vm.LoadPageAsync(1);
        Check(vm.HasError && vm.Error == "Steam is offline." && !vm.IsBusy, "Query errors expose retry state.");
        Check(!vm.NeedsSteam && vm.ErrorHeading == "Couldn't load the Workshop", "Other query failures retain the general Workshop error.");
        service.Handler = (_, _) => Task.FromResult(new WorkshopPage(1, 0, Array.Empty<WorkshopItem>()));
        await vm.LoadPageAsync(1);
        Check(vm.IsEmpty && !vm.HasError && !vm.CanNext, "Retry recovers to empty state.");
        service.FailTags = true;
        await vm.LoadCategoriesAsync();
        Check(vm.HasCategoryError, "Category failure is separate from item results.");
        service.FailTags = false;
        await vm.LoadCategoriesAsync();
        Check(!vm.HasCategoryError && vm.Categories.Count == 3, "Category retry recovers.");

        service.Handler = (options, _) => Task.FromResult(new WorkshopPage(options.Page, 123,
            Enumerable.Range(0, 9).Select(i => Item(i % 3 == 0 ? "Expanded campaign and historical units" : i % 3 == 1 ? "New battle maps for multiplayer" : "The road to Kyoto — complete overhaul")).ToArray()));
        vm.SearchText = "";
        await vm.SubmitSearchAsync();
        var window = new MainWindow(vm);
        Check(((ListBox)window.FindName("CategoryList")).SelectionMode == SelectionMode.Single, "Category control allows one selection.");
        Check(((TextBox)window.FindName("SearchBox")).InputBindings.OfType<KeyBinding>().Any(k => k.Key == Key.Enter), "Search box binds Enter.");
        await RenderAsync(window, "workshop-browser", 1440, 900);
        await RenderAsync(window, "workshop-browser-small", 1080, 700);
        var successfulQuery = service.Handler;
        service.Handler = (_, _) => Task.FromException<WorkshopPage>(new SteamNotRunningException());
        await vm.LoadPageAsync(1);
        Check(vm.NeedsSteam && vm.ErrorHeading == "Steam must be running to browse Workshop"
            && vm.LaunchSteamCommand.CanExecute(null), "A stopped Steam client shows the launch action and specific notice.");
        await RenderAsync(window, "workshop-steam-not-running", 1080, 700);
        Check(((Button)window.FindName("LaunchSteamButton")).Visibility == Visibility.Visible,
            "The Steam launch button is visible in the stopped-client notice.");
        vm.LaunchSteamCommand.Execute(null);
        Check(launched.Count == 1 && launched[0].FileName == "steam://open/main" && launched[0].UseShellExecute,
            "Launch Steam opens the client protocol without starting a game.");
        Check(vm.Error!.Contains("then select Retry") && vm.NeedsSteam,
            "Steam launch explains how to retry after sign-in.");
        failLaunch = true;
        vm.LaunchSteamCommand.Execute(null);
        Check(vm.Error!.Contains("Couldn't launch Steam") && vm.LaunchSteamCommand.CanExecute(null),
            "A failed Steam launch stays recoverable without crashing.");
        failLaunch = false;
        service.Handler = successfulQuery;
        await vm.LoadPageAsync(1);
        Check(!vm.HasError && !vm.NeedsSteam && !vm.LaunchSteamCommand.CanExecute(null) && vm.Items.Count > 0,
            "Retry after Steam starts clears the notice and restores browsing.");
        service.DetailsHandler = (_, _) => Task.FromResult(new WorkshopItemDetails(Item() with
        { Description = "[h1]A new campaign[/h1]\n[b]Historical units[/b] &amp; new maps\n[url=https://example.com]Read more[/url]" },
            new[] { new WorkshopScreenshot("https://example.com/first.jpg", "First image"), new WorkshopScreenshot("https://example.com/second.png", "Second image") }));
        await vm.OpenDetailsAsync(vm.Items[0]);
        Check(vm.IsDetailOpen && vm.Detail is { IsBusy: false, HasError: false }, "Opening an item loads one detail popup.");
        Check(vm.Detail!.Description.Contains("Historical units & new maps") && !vm.Detail.Description.Contains("[b]"), "Full description is converted to readable text.");
        Check(vm.Detail.Description.Contains("Read more (https://example.com)"), "Description preserves link destinations as text.");
        Check(vm.Detail.Screenshots.Count == 2 && vm.Detail.ImagePosition == "1 / 2", "Screenshots populate the gallery.");
        vm.Detail.NextImageCommand.Execute(null);
        Check(vm.Detail.ImagePosition == "2 / 2" && !vm.Detail.NextImageCommand.CanExecute(null), "Gallery advances within bounds.");
        vm.Detail.PreviousImageCommand.Execute(null);
        Check(vm.Detail.ImagePosition == "1 / 2", "Gallery moves backward.");
        await RenderAsync(window, "workshop-detail", 1440, 900);
        await RenderAsync(window, "workshop-detail-small", 1080, 700);
        Check(!((Grid)window.FindName("BrowserContent")).IsEnabled, "Dimmed browser cannot receive interaction while details are open.");
        vm.CloseDetails();
        Check(!vm.IsDetailOpen && vm.Detail is null, "Close clears the popup state.");
        var late = new TaskCompletionSource<WorkshopItemDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken detailToken = default;
        service.DetailsHandler = (_, token) => { detailToken = token; return late.Task; };
        Task pending = vm.OpenDetailsAsync(vm.Items[0]);
        Check(vm.Detail!.IsBusy, "Popup opens while its details load.");
        vm.CloseDetails();
        Check(detailToken.IsCancellationRequested, "Closing cancels the detail request.");
        late.SetResult(new WorkshopItemDetails(Item("Late item"), Array.Empty<WorkshopScreenshot>()));
        await pending;
        Check(!vm.IsDetailOpen, "Late detail responses cannot reopen the popup.");
        service.DetailsHandler = (_, _) => Task.FromException<WorkshopItemDetails>(new IOException("Details offline"));
        await vm.OpenDetailsAsync(vm.Items[0]);
        Check(vm.Detail!.HasError && !vm.Detail.IsBusy && !vm.HasError, "Detail error remains separate from browser results.");
        service.DetailsHandler = (_, _) => Task.FromResult(new WorkshopItemDetails(Item() with { Description = "" }, Array.Empty<WorkshopScreenshot>()));
        await vm.Detail.LoadAsync();
        Check(!vm.Detail.HasError && !vm.Detail.HasScreenshots && vm.Detail.Description.Contains("hasn't provided"), "Retry supports items with no description or screenshots.");
        vm.CloseDetails();
        Check(WorkshopThumbnailDownloader.DetectExtension(new byte[] { 255, 216, 255, 0 }) == ".jpg", "Thumbnail suffix is detected from bytes.");
        window.Close();
        Check(service.Disposed, "Closing the window disposes the browser service.");
        Check(await new ThumbnailLoader().LoadAsync("file:///invalid", CancellationToken.None) is null, "Reject non-web thumbnail URLs.");
    }

    private static async Task CheckLiveAsync()
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        var steam = new SteamWorkshopBrowserService();
        using var vm = new WorkshopBrowserViewModel(steam, new ThumbnailLoader());
        await vm.InitializeAsync();
        Check(!vm.HasError && vm.Items.Count > 0, "Live native Steam query returns items: " + vm.Error);
        Check(vm.Categories.Count > 1 && !vm.HasCategoryError, "Live categories load.");
        Check(vm.Items.Any(i => i.Item.AuthorName is { } name && !name.StartsWith("Steam user ")), "Resolve live author display names.");
        Check(vm.Items.Any(i => i.Thumbnail is not null), "Decode live thumbnail images.");
        Console.WriteLine($"Live: {vm.TotalMatchingItems} items, {vm.Categories.Count - 1} categories, first author {vm.Items[0].Author}.");
        var window = new MainWindow(vm);
        await RenderAsync(window, "workshop-browser-live", 1440, 900);
        foreach (WorkshopCardViewModel item in vm.Items.Take(8))
        {
            await vm.OpenDetailsAsync(item);
            Check(vm.Detail is { HasError: false }, "Live detail query succeeds: " + vm.Detail?.Error);
            if (vm.Detail!.Screenshots.Count > 1) break;
        }
        Check(vm.Detail!.Description.Length > 255, "Live detail description exceeds the list summary limit.");
        Check(vm.Detail.Screenshots.Count > 1 && vm.Detail.Screenshots.Any(s => s.Image is not null), "Live additional screenshots load.");
        Console.WriteLine($"Live detail: {vm.Detail.Description.Length} description characters, {vm.Detail.Screenshots.Count} images.");
        await RenderAsync(window, "workshop-detail-live", 1440, 1000);
        await RenderAsync(window, "workshop-detail-live-small", 1080, 700);
        string thumbnailPath = await WorkshopThumbnailDownloader.DownloadAsync(vm.Detail.Screenshots[0].Url,
            Path.Combine(AppContext.BaseDirectory, "visual-checks", "downloaded-thumbnail-" + Guid.NewGuid().ToString("N")));
        Check(File.Exists(thumbnailPath) && new FileInfo(thumbnailPath).Length > 0, "Live thumbnail downloader saves image bytes without downloading a mod.");
        checks += await ModIntegrationChecks.CheckSteamWorkerAsync(steam);
        window.Close();
    }

    private static async Task RenderAsync(MainWindow window, string name, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = Path.Combine(AppContext.BaseDirectory, "visual-checks");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".png");
        using var file = File.Create(path);
        encoder.Save(file);
        Console.WriteLine("Rendered " + path);
    }

    private sealed class NoThumbnails : IThumbnailLoader
    {
        public Task<ImageSource?> LoadAsync(string? url, CancellationToken cancellationToken) => Task.FromResult<ImageSource?>(null);
    }

    private sealed class FakeBrowser : IWorkshopBrowserService
    {
        public List<WorkshopOptions> Queries { get; } = new();
        public bool FailTags { get; set; }
        public bool Disposed { get; private set; }
        public Func<WorkshopOptions, CancellationToken, Task<WorkshopPage>> Handler { get; set; } =
            (options, _) => Task.FromResult(new WorkshopPage(options.Page, 123, new[] { Item() }));
        public Func<ulong, CancellationToken, Task<WorkshopItemDetails>> DetailsHandler { get; set; } =
            (_, _) => Task.FromResult(new WorkshopItemDetails(Item(), Array.Empty<WorkshopScreenshot>()));
        public Task<IReadOnlyList<WorkshopTag>> FetchTagsAsync(CancellationToken cancellationToken = default) => FailTags
            ? Task.FromException<IReadOnlyList<WorkshopTag>>(new IOException("Offline"))
            : Task.FromResult<IReadOnlyList<WorkshopTag>>(new[] {
                new WorkshopTag("historical", "Historical", "Categories", 2300),
                new WorkshopTag("map", "Map", "Categories", 154),
                new WorkshopTag("empty", "Empty", "Categories", 0) });
        public Task<WorkshopPage> FetchItemsAsync(WorkshopOptions options, CancellationToken cancellationToken = default)
        { Queries.Add(options); return Handler(options, cancellationToken); }
        public Task<WorkshopItemDetails> FetchDetailsAsync(ulong itemId, CancellationToken cancellationToken = default) =>
            DetailsHandler(itemId, cancellationToken);
        public void Dispose() => Disposed = true;
    }
}
