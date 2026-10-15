using TWS2CESteamModManager.App.Localization;
using System.ComponentModel;
using TWS2CESteamModManager.App.Services;
using TWS2CESteamModManager.Steam;

namespace TWS2CESteamModManager.App.ViewModels;

public sealed record SortOption(string Key, string Label);
public sealed record CategoryOption(string? Name, string Label, ulong Count)
{
    public string CountText => Name is null ? "" : Count.ToString("N0");
}

public sealed class WorkshopBrowserViewModel : ObservableObject, IDisposable
{
    private readonly IWorkshopBrowserService service;
    private readonly IThumbnailLoader thumbnails;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? activeQuery;
    private int version;
    private bool initialized, disposed, isBusy, categoriesLoading, hasLoaded;
    private string searchText = "", appliedSearch = "", categoryError = "";
    private string? error;
    private uint page = 1, total;
    private readonly CategoryOption allCategories = new(null, L.Text("label.allCategories"), 0);
    private CategoryOption selectedCategory;
    private SortOption selectedSort;
    private IReadOnlyList<CategoryOption> categories;
    private IReadOnlyList<WorkshopCardViewModel> items = Array.Empty<WorkshopCardViewModel>();
    private WorkshopDetailsViewModel? detail;
    private readonly IWorkshopDownloadController? downloads;
    private readonly IApplicationLauncher launcher;
    private bool needsSteam;

    public WorkshopBrowserViewModel(IWorkshopBrowserService service, IThumbnailLoader thumbnails, IWorkshopDownloadController? downloads = null,
        IApplicationLauncher? launcher = null)
    {
        this.service = service;
        this.thumbnails = thumbnails;
        this.downloads = downloads;
        this.launcher = launcher ?? new ApplicationLauncher();
        if (downloads is not null) downloads.PropertyChanged += DownloadsChanged;
        selectedCategory = allCategories;
        categories = new[] { allCategories };
        selectedSort = SortOptions[0];
        SearchCommand = new(() => _ = SubmitSearchAsync());
        RefreshCommand = new(() => _ = LoadPageAsync(page));
        PreviousCommand = new(() => _ = LoadPageAsync(page - 1), () => CanPrevious);
        NextCommand = new(() => _ = LoadPageAsync(page + 1), () => CanNext);
        RetryCategoriesCommand = new(() => _ = LoadCategoriesAsync(), () => !categoriesLoading);
        CloseDetailsCommand = new(CloseDetails);
        LaunchSteamCommand = new(LaunchSteam, () => NeedsSteam && !disposed);
    }

    public IReadOnlyList<SortOption> SortOptions { get; } = new[]
    {
        new SortOption("popular", L.Text("sort.popular")),
        new SortOption("subscribed", L.Text("sort.subscribed")),
        new SortOption("rated", L.Text("sort.rated")),
        new SortOption("newest", L.Text("sort.newest")),
        new SortOption("updated", L.Text("sort.updated"))
    };
    public DelegateCommand SearchCommand { get; }
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand PreviousCommand { get; }
    public DelegateCommand NextCommand { get; }
    public DelegateCommand RetryCategoriesCommand { get; }
    public DelegateCommand CloseDetailsCommand { get; }
    public DelegateCommand LaunchSteamCommand { get; }
    public WorkshopDetailsViewModel? Detail
    {
        get => detail;
        private set { detail = value; Notify(); Notify(nameof(IsDetailOpen)); }
    }
    public bool IsDetailOpen => Detail is not null;

    public Task OpenDetailsAsync(WorkshopCardViewModel item)
    {
        if (disposed) return Task.CompletedTask;
        CloseDetails();
        Detail = new WorkshopDetailsViewModel(item, service, thumbnails, downloads);
        return Detail.LoadAsync();
    }

    public void CloseDetails()
    {
        Detail?.Dispose();
        Detail = null;
    }
    public Task OpenInstalledDetailsAsync(InstalledModViewModel mod)
    {
        if (disposed) return Task.CompletedTask;
        CloseDetails();
        WorkshopCardViewModel? card = mod.Snapshot.Mod.WorkshopItemId is { } itemId
            ? new WorkshopCardViewModel(new WorkshopItem(itemId, mod.Name, "", 0, DateTimeOffset.MinValue, "", 0, 0, null), downloads)
                { Thumbnail = mod.Image }
            : null;
        Detail = new WorkshopDetailsViewModel(card, service, thumbnails, downloads, mod);
        return Detail.LoadAsync();
    }
    public IReadOnlyList<WorkshopCardViewModel> Items { get => items; private set { items = value; Notify(); NotifyState(); } }
    public IReadOnlyList<CategoryOption> Categories { get => categories; private set { categories = value; Notify(); } }
    public string SearchText { get => searchText; set { searchText = value; Notify(); } }
    public CategoryOption SelectedCategory
    {
        get => selectedCategory;
        set
        {
            if (value is null || value == selectedCategory) return;
            selectedCategory = value; Notify(); Notify(nameof(Heading));
            if (initialized) _ = LoadPageAsync(1);
        }
    }
    public SortOption SelectedSort
    {
        get => selectedSort;
        set
        {
            if (value is null || value == selectedSort) return;
            selectedSort = value; Notify();
            if (initialized) _ = LoadPageAsync(1);
        }
    }
    public bool IsBusy { get => isBusy; private set { isBusy = value; Notify(); NotifyState(); } }
    public bool CategoriesLoading { get => categoriesLoading; private set { categoriesLoading = value; Notify(); RetryCategoriesCommand.Refresh(); } }
    public string CategoryError { get => categoryError; private set { categoryError = value; Notify(); Notify(nameof(HasCategoryError)); } }
    public bool HasCategoryError => CategoryError.Length > 0;
    public string? Error { get => error; private set { error = value; Notify(); NotifyState(); } }
    public bool HasError => Error is not null;
    public bool NeedsSteam => needsSteam;
    public string ErrorHeading => NeedsSteam ? L.Text("msg.steamRequired") : L.Text("msg.workshopLoadFailed");
    public bool IsEmpty => hasLoaded && !IsBusy && !HasError && Items.Count == 0;
    public uint CurrentPage => page;
    public uint TotalMatchingItems => total;
    public string Heading => SelectedCategory.Name is null ? L.Text("label.exploreWorkshop") : SelectedCategory.Label;
    public string ResultSummary => IsBusy ? L.Text("status.findingItems")
        : !hasLoaded ? L.Text("msg.discoverWorkshop")
        : HasError ? L.Text("msg.unableToLoadItems")
        : appliedSearch.Length > 0 ? L.Text("label.searchResultCount", total, appliedSearch) : L.Text("label.resultCount", total);
    public string PageText => L.Text("label.page", page, Math.Max(1UL, ((ulong)total + 49) / 50));
    public string ConnectionText => IsBusy ? L.Text("status.connectingSteam") : HasError ? L.Text("status.connectionAttention") : hasLoaded ? L.Text("status.steamConnected") : L.Text("status.steamWorkshop");
    public bool CanPrevious => !IsBusy && !HasError && page > 1;
    public bool CanNext => !IsBusy && !HasError && (ulong)page * 50 < total;

    public Task InitializeAsync()
    {
        if (initialized) return Task.CompletedTask;
        initialized = true;
        return Task.WhenAll(LoadCategoriesAsync(), LoadPageAsync(1));
    }

    public Task SubmitSearchAsync()
    {
        appliedSearch = SearchText.Trim();
        return LoadPageAsync(1);
    }

    public async Task LoadCategoriesAsync()
    {
        if (disposed || CategoriesLoading) return;
        CategoriesLoading = true;
        CategoryError = "";
        try
        {
            IReadOnlyList<WorkshopTag> tags = await service.FetchTagsAsync(lifetime.Token);
            if (disposed) return;
            Categories = new[] { allCategories }.Concat(tags.Where(tag => tag.ItemCount > 0)
                .Select(tag => new CategoryOption(tag.Name, tag.DisplayName, tag.ItemCount))).ToArray();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!disposed) CategoryError = L.Text("msg.categoriesFailed"); }
        finally { if (!disposed) CategoriesLoading = false; }
    }

    public async Task LoadPageAsync(uint requestedPage)
    {
        if (disposed) return;
        if (requestedPage == 0) throw new ArgumentOutOfRangeException(nameof(requestedPage));
        activeQuery?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeQuery = request;
        int currentVersion = ++version;
        var options = new WorkshopOptions(requestedPage,
            appliedSearch.Length == 0 ? null : appliedSearch, SelectedSort.Key)
        {
            Tags = SelectedCategory.Name is { } tag ? new[] { tag } : Array.Empty<string>()
        };
        needsSteam = false;
        Error = null;
        page = requestedPage;
        Items = Array.Empty<WorkshopCardViewModel>();
        IsBusy = true;
        try
        {
            WorkshopPage result = await service.FetchItemsAsync(options, request.Token);
            if (disposed || currentVersion != version) return;
            page = result.Page;
            total = result.TotalMatchingItems;
            hasLoaded = true;
            WorkshopCardViewModel[] cards = result.Items.Select(item => new WorkshopCardViewModel(item, downloads)).ToArray();
            Items = cards;
            IsBusy = false;
            await Task.WhenAll(cards.Select(async card =>
            {
                try
                {
                    var image = await thumbnails.LoadAsync(card.Item.PreviewUrl, request.Token);
                    if (!disposed && currentVersion == version) card.Thumbnail = image;
                }
                catch (OperationCanceledException) { }
            }));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!disposed && currentVersion == version)
            {
                hasLoaded = true;
                needsSteam = exception is SteamNotRunningException;
                Error = needsSteam ? L.Text("msg.launchSteamThenRetry")
                    : exception is DllNotFoundException or BadImageFormatException
                    ? L.Text("msg.runtimeUnavailable")
                    : exception.Message;
            }
        }
        finally
        {
            if (!disposed && currentVersion == version)
            {
                IsBusy = false;
                if (ReferenceEquals(activeQuery, request)) activeQuery = null;
            }
        }
    }

    private void LaunchSteam()
    {
        try
        {
            launcher.LaunchSteam();
            Error = L.Text("msg.steamOpening");
        }
        catch (Exception exception)
        {
            Error = L.Text("msg.launchSteamFailed", exception.Message);
        }
    }

    private void NotifyState()
    {
        foreach (string property in new[] { nameof(IsEmpty), nameof(HasError), nameof(ResultSummary), nameof(PageText),
            nameof(CurrentPage), nameof(TotalMatchingItems), nameof(ConnectionText), nameof(CanPrevious), nameof(CanNext),
            nameof(NeedsSteam), nameof(ErrorHeading) }) Notify(property);
        PreviousCommand.Refresh();
        NextCommand.Refresh();
        LaunchSteamCommand.Refresh();
    }

    private void DownloadsChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (WorkshopCardViewModel card in Items) card.RefreshDownloadState();
        Detail?.Card?.RefreshDownloadState();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (downloads is not null) downloads.PropertyChanged -= DownloadsChanged;
        CloseDetails();
        lifetime.Cancel();
        service.Dispose();
    }
}
