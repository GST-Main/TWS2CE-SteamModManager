using TWS2CESteamModManager.App.Localization;
using System.Windows.Media;
using TWS2CESteamModManager.App.Services;
using TWS2CESteamModManager.Steam;

namespace TWS2CESteamModManager.App.ViewModels;

public sealed class WorkshopScreenshotViewModel(WorkshopScreenshot screenshot) : ObservableObject
{
    private ImageSource? image;
    public string Caption => screenshot.Caption;
    public string Url => screenshot.Url;
    public ImageSource? Image { get => image; set { image = value; Notify(); Notify(nameof(HasImage)); } }
    public bool HasImage => Image is not null;
}

public sealed class WorkshopDetailsViewModel : ObservableObject, IDisposable
{
    private readonly IWorkshopBrowserService service;
    private readonly IThumbnailLoader images;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? active;
    private bool disposed, isBusy;
    private int version;
    private string description = "", error = "";
    private WorkshopCardViewModel? card;
    private bool hasWorkshopMetadata;
    private IReadOnlyList<WorkshopScreenshotViewModel> screenshots = Array.Empty<WorkshopScreenshotViewModel>();
    private WorkshopScreenshotViewModel? selectedScreenshot;
    private readonly IWorkshopDownloadController? downloads;

    public WorkshopDetailsViewModel(WorkshopCardViewModel? item, IWorkshopBrowserService service, IThumbnailLoader images,
        IWorkshopDownloadController? downloads = null, InstalledModViewModel? installedMod = null)
    {
        if (item is null && installedMod is null) throw new ArgumentException("Provide a Workshop item or an installed mod.");
        card = item;
        InstalledMod = installedMod;
        this.service = service;
        this.images = images;
        this.downloads = downloads;
        RetryCommand = new(() => _ = LoadAsync());
        PreviousImageCommand = new(() => SelectImage(-1), () => SelectedIndex > 0);
        NextImageCommand = new(() => SelectImage(1), () => SelectedIndex >= 0 && SelectedIndex < Screenshots.Count - 1);
        if (installedMod is not null)
        {
            SetLocalPreview();
            description = item is null ? L.Text("msg.localDescription") : L.Text("msg.loadingDescription");
        }
    }

    public InstalledModViewModel? InstalledMod { get; }
    public bool IsInstalledDetail => InstalledMod is not null;
    public bool HasWorkshopItem => Card is not null;
    public bool ShowGallery => IsInstalledDetail ? HasScreenshots : HasWorkshopItem || HasScreenshots;
    public bool ShowWorkshopMetadata => HasWorkshopItem && (!IsInstalledDetail || hasWorkshopMetadata);
    public string Title => InstalledMod is { } mod && (!string.IsNullOrWhiteSpace(mod.Snapshot.Mod.DisplayName) || !hasWorkshopMetadata)
        ? mod.Name : Card!.Title;
    public string Subtitle => IsInstalledDetail && !hasWorkshopMetadata ? L.Text("label.localMod", InstalledMod!.Source) : Card!.Author;
    public WorkshopCardViewModel? Card
    {
        get => card;
        private set { card = value; Notify(); Notify(nameof(Title)); Notify(nameof(Subtitle)); Notify(nameof(ShowWorkshopMetadata)); }
    }
    public string Description { get => description; private set { description = value; Notify(); } }
    public bool IsBusy { get => isBusy; private set { isBusy = value; Notify(); } }
    public string Error { get => error; private set { error = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public IReadOnlyList<WorkshopScreenshotViewModel> Screenshots
    {
        get => screenshots;
        private set { screenshots = value; Notify(); Notify(nameof(HasScreenshots)); Notify(nameof(ShowGallery)); }
    }
    public bool HasScreenshots => Screenshots.Count > 0;
    private int SelectedIndex => SelectedScreenshot is null ? -1 : Screenshots.ToList().IndexOf(SelectedScreenshot);
    public WorkshopScreenshotViewModel? SelectedScreenshot
    {
        get => selectedScreenshot;
        set
        {
            selectedScreenshot = value; Notify(); Notify(nameof(ImagePosition));
            PreviousImageCommand.Refresh(); NextImageCommand.Refresh();
        }
    }
    public string ImagePosition => SelectedIndex < 0 ? L.Text("label.noScreenshots") : L.Text("label.imagePosition", SelectedIndex + 1, Screenshots.Count);
    public DelegateCommand RetryCommand { get; }
    public DelegateCommand PreviousImageCommand { get; }
    public DelegateCommand NextImageCommand { get; }
    private void SelectImage(int offset) => SelectedScreenshot = Screenshots[SelectedIndex + offset];

    private void SetLocalPreview()
    {
        if (InstalledMod?.Image is not { } image) return;
        Screenshots = new[] { new WorkshopScreenshotViewModel(new WorkshopScreenshot("", L.Text("label.installedImage"))) { Image = image } };
        SelectedScreenshot = Screenshots[0];
    }

    public async Task LoadAsync()
    {
        if (disposed) return;
        active?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        active = request;
        int currentVersion = ++version;
        IsBusy = true;
        Error = "";
        try
        {
            if (InstalledMod is { Image: null, Snapshot.ImagePath: not null })
            {
                await InstalledMod.LoadImageAsync();
                if (disposed || currentVersion != version) return;
                SetLocalPreview();
                if (Card is not null) Card.Thumbnail = InstalledMod.Image;
            }
            if (Card is not { } workshopCard) return;
            WorkshopItemDetails details = await service.FetchDetailsAsync(workshopCard.Item.Id, request.Token);
            if (disposed || currentVersion != version) return;
            hasWorkshopMetadata = true;
            Card = new WorkshopCardViewModel(details.Item, downloads) { Thumbnail = workshopCard.Thumbnail };
            Description = SteamDescription.ToPlainText(details.Description);
            if (Description.Length == 0) Description = L.Text("msg.noDescription");
            if (details.Screenshots.Count > 0 || InstalledMod?.Image is null)
            {
                Screenshots = details.Screenshots.Select(screenshot => new WorkshopScreenshotViewModel(screenshot)).ToArray();
                SelectedScreenshot = Screenshots.FirstOrDefault();
                if (SelectedScreenshot is not null) SelectedScreenshot.Image = Card.Thumbnail;
            }
            IsBusy = false;
            await Task.WhenAll(Screenshots.Where(screenshot => screenshot.Url.Length > 0).Select(async screenshot =>
            {
                try
                {
                    ImageSource? image = await images.LoadAsync(screenshot.Url, request.Token);
                    if (!disposed && currentVersion == version && image is not null) screenshot.Image = image;
                }
                catch (OperationCanceledException) { }
            }));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!disposed && currentVersion == version)
            {
                Error = exception.Message;
                if (IsInstalledDetail && !hasWorkshopMetadata) Description = L.Text("msg.detailsUnavailable");
            }
        }
        finally
        {
            if (!disposed && currentVersion == version) { IsBusy = false; active = null; }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
    }
}
