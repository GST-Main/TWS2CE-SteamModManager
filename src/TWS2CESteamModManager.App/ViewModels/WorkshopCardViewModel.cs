using TWS2CESteamModManager.App.Localization;
using System.Windows.Media;
using TWS2CESteamModManager.Steam;

namespace TWS2CESteamModManager.App.ViewModels;

public sealed class WorkshopCardViewModel : ObservableObject
{
    private ImageSource? thumbnail;
    private readonly IWorkshopDownloadController? downloads;
    public WorkshopCardViewModel(WorkshopItem item, IWorkshopDownloadController? downloads = null)
    {
        Item = item;
        this.downloads = downloads;
        Tags = item.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DownloadCommand = new(() => _ = downloads!.DownloadAsync(Item.Id), () => downloads?.CanDownload(Item.Id) == true);
    }
    public WorkshopItem Item { get; }
    public DelegateCommand DownloadCommand { get; }
    public string DownloadLabel => downloads?.GetDownloadLabel(Item.Id) ?? L.Text("action.download");
    public string DownloadHint => downloads?.GetDownloadHint(Item.Id) ?? L.Text("tip.setupFolders");
    public bool IsDownloading => downloads?.DownloadingItemId == Item.Id;
    public string DownloadStatus => downloads?.LastDownloadItemId == Item.Id ? downloads.DownloadStatus : "";
    public bool HasDownloadStatus => DownloadStatus.Length > 0;
    public DelegateCommand? CancelDownloadCommand => downloads?.CancelDownloadCommand;
    public void RefreshDownloadState()
    {
        foreach (string property in new[] { nameof(DownloadLabel), nameof(DownloadHint), nameof(IsDownloading), nameof(DownloadStatus), nameof(HasDownloadStatus) }) Notify(property);
        DownloadCommand.Refresh();
    }
    public string Title => Item.Title;
    public string Author => L.Text("label.byAuthor", Item.AuthorName ?? L.Text("label.steamUser", Item.AuthorSteamId));
    public IReadOnlyList<string> Tags { get; }
    public string UpVotes => $"↑ {Item.VotesUp:N0}";
    public string DownVotes => $"↓ {Item.VotesDown:N0}";
    public string Rating => (ulong)Item.VotesUp + Item.VotesDown == 0 ? L.Text("label.unrated")
        : L.Text("label.positiveRating", 100.0 * Item.VotesUp / ((ulong)Item.VotesUp + Item.VotesDown));
    public string Updated => L.Text("label.updated", Item.UpdatedAt);
    public ImageSource? Thumbnail
    {
        get => thumbnail;
        set { thumbnail = value; Notify(); }
    }
}
