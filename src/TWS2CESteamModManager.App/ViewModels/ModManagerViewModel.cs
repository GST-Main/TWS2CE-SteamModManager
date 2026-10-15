using TWS2CESteamModManager.App.Localization;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TWS2CESteamModManager.App.Services;
using TWS2CESteamModManager.Core;

namespace TWS2CESteamModManager.App.ViewModels;

public interface IWorkshopDownloadController : INotifyPropertyChanged
{
    string GetDownloadLabel(ulong itemId);
    string GetDownloadHint(ulong itemId);
    bool CanDownload(ulong itemId);
    Task DownloadAsync(ulong itemId);
    ulong? DownloadingItemId { get; }
    ulong? LastDownloadItemId { get; }
    string DownloadStatus { get; }
    DelegateCommand CancelDownloadCommand { get; }
}

public sealed class InstalledModViewModel : ObservableObject
{
    private readonly Func<InstalledModViewModel, bool, Task> toggle;
    private bool isEnabled;
    private ImageSource? image;
    public InstalledModViewModel(InstalledModSnapshot snapshot, Func<InstalledModViewModel, bool, Task> toggle)
    {
        Snapshot = snapshot;
        this.toggle = toggle;
        isEnabled = snapshot.Mod.IsEnabled;
    }
    public InstalledModSnapshot Snapshot { get; }
    public Guid Id => Snapshot.Mod.Id;
    public string Name => string.IsNullOrWhiteSpace(Snapshot.Mod.DisplayName) ? FileName : Snapshot.Mod.DisplayName;
    public string FileName => Snapshot.Mod.FileName;
    public string FilePath => Snapshot.FilePath ?? FileName;
    public string EnabledState => IsEnabled ? L.Text("label.enabled") : L.Text("label.disabled");
    public string WorkshopId => Snapshot.Mod.WorkshopItemId?.ToString() ?? L.Text("label.noWorkshopId");
    public bool HasDisplayName => !string.IsNullOrWhiteSpace(Snapshot.Mod.DisplayName) && Name != FileName;
    public int Order => Snapshot.Mod.Order + 1;
    public string Source => Snapshot.Mod.Source == ModSource.Workshop ? L.Text("label.workshop") : L.Text("label.imported");
    public string Status => Snapshot.IsPresent ? L.Text("label.present") : L.Text("label.missingFile");
    public bool IsMissing => !Snapshot.IsPresent;
    public bool IsEnabled
    {
        get => isEnabled;
        set { if (isEnabled == value) return; isEnabled = value; Notify(); _ = toggle(this, value); }
    }
    public ImageSource? Image { get => image; private set { image = value; Notify(); } }
    internal void RestoreEnabled() { isEnabled = Snapshot.Mod.IsEnabled; Notify(nameof(IsEnabled)); }
    internal async Task LoadImageAsync()
    {
        if (Snapshot.ImagePath is not { } path) return;
        Image = await Task.Run<ImageSource?>(() =>
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (file.Length > 8 * 1024 * 1024) return null;
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 96;
                bitmap.StreamSource = file; bitmap.EndInit(); bitmap.Freeze();
                return bitmap;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
        });
    }
}

public sealed class ModManagerViewModel : ObservableObject, IWorkshopDownloadController
{
    private readonly IModManagementService service;
    private Task activeOperation = Task.CompletedTask;
    private Task nameRefresh = Task.CompletedTask;
    private CancellationTokenSource? namesCancellation;
    private int itemRefreshVersion;
    private CancellationTokenSource? downloadCancellation;
    private bool isBusy, isReady, editingFolders, closing;
    private string error = "", appDataPath = "", gameInstallPath = "", downloadStatus = "";
    private ulong? downloadingItemId;
    private ulong? lastDownloadItemId;
    private int downloadVersion;
    private IReadOnlyList<InstalledModViewModel> items = Array.Empty<InstalledModViewModel>();
    private InstalledModViewModel? selectedMod;

    public ModManagerViewModel(IModManagementService service)
    {
        this.service = service;
        ReloadCommand = new(() => _ = InitializeAsync(), () => !IsBusy && !closing);
        ApplyFoldersCommand = new(() => _ = ApplyFoldersAsync(), () => !IsBusy && !closing);
        EditFoldersCommand = new(() => { editingFolders = !editingFolders; NotifyState(); }, () => !IsBusy);
        MoveUpCommand = new(() => _ = MoveSelectedAsync(-1), () => CanManage && SelectedMod is { Order: > 1 });
        MoveDownCommand = new(() => _ = MoveSelectedAsync(1), () => CanManage && SelectedMod is { } mod && mod.Order < Items.Count);
        DeleteCommand = new(() => _ = DeleteSelectedAsync(), () => CanManage && SelectedMod is not null);
        CancelDownloadCommand = new(() => { downloadCancellation?.Cancel(); DownloadStatus = L.Text("status.cancellingDownload"); }, () => IsDownloading);
        DismissTransferCommand = new(() => DownloadStatus = "", () => !IsDownloading);
    }

    public DelegateCommand ReloadCommand { get; }
    public DelegateCommand ApplyFoldersCommand { get; }
    public DelegateCommand EditFoldersCommand { get; }
    public DelegateCommand MoveUpCommand { get; }
    public DelegateCommand MoveDownCommand { get; }
    public DelegateCommand DeleteCommand { get; }
    public DelegateCommand CancelDownloadCommand { get; }
    public DelegateCommand DismissTransferCommand { get; }
    public IReadOnlyList<InstalledModViewModel> Items { get => items; private set { items = value; Notify(); NotifyState(); } }
    public InstalledModViewModel? SelectedMod { get => selectedMod; set { selectedMod = value; Notify(); NotifyState(); } }
    public bool IsBusy { get => isBusy; private set { isBusy = value; Notify(); NotifyState(); } }
    public bool IsReady { get => isReady; private set { isReady = value; Notify(); NotifyState(); } }
    public bool CanManage => IsReady && !IsBusy && !ShowFolderSetup && !closing;
    public bool CanConfigure => !IsBusy && !closing;
    public bool ShowFolderSetup => !IsReady || editingFolders;
    public bool IsEmpty => IsReady && !IsBusy && Items.Count == 0;
    public string Summary => !IsReady ? L.Text("msg.selectFolders")
        : L.Text("label.modSummary", Items.Count, Items.Count(mod => mod.IsEnabled));
    public string AppDataPath { get => appDataPath; set { appDataPath = value; Notify(); } }
    public string GameInstallPath { get => gameInstallPath; set { gameInstallPath = value; Notify(); } }
    public string Error { get => error; private set { error = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public ulong? DownloadingItemId { get => downloadingItemId; private set { downloadingItemId = value; Notify(); NotifyState(); } }
    public ulong? LastDownloadItemId { get => lastDownloadItemId; private set { lastDownloadItemId = value; Notify(); } }
    public bool IsDownloading => DownloadingItemId is not null;
    public string DownloadStatus { get => downloadStatus; private set { downloadStatus = value; Notify(); Notify(nameof(HasTransferStatus)); } }
    public bool HasTransferStatus => DownloadStatus.Length > 0;

    public Task InitializeAsync() => RunAsync(async () =>
    {
        IsReady = false;
        Items = Array.Empty<InstalledModViewModel>();
        SelectedMod = null;
        GameLocationResult resolved = await service.ResolveAsync();
        AppDataPath = resolved.Manifest.AppDataPath ?? "";
        GameInstallPath = resolved.Manifest.GameInstallPath ?? "";
        if (resolved.Status == SetupStatus.Ready) await OpenAsync(resolved.Manifest);
    }, refresh: false);

    public Task ApplyFoldersAsync() => RunAsync(async () =>
    {
        ManagerManifest folders;
        try { folders = GameLocationService.SelectAppDataFolder(new ManagerManifest(), AppDataPath); }
        catch (ArgumentException) { Error = L.Text("msg.invalidAppDataFolder"); return; }
        try { folders = GameLocationService.SelectGameInstallFolder(folders, GameInstallPath); }
        catch (ArgumentException) { Error = L.Text("msg.invalidGameFolder"); return; }
        IsReady = false;
        await OpenAsync(folders);
    }, refresh: false);

    private async Task OpenAsync(ManagerManifest folders)
    {
        await service.OpenAsync(folders);
        editingFolders = false;
        await RefreshItemsAsync();
        IsReady = true;
    }

    public Task ImportAsync(IEnumerable<string> paths)
    {
        if (!CanManage) return Task.CompletedTask;
        return RunAsync(async () => { foreach (string path in paths) await service.ImportAsync(path, CancellationToken.None); });
    }

    private Task ToggleAsync(InstalledModViewModel mod, bool enabled)
    {
        if (!CanManage) { mod.RestoreEnabled(); return Task.CompletedTask; }
        return RunAsync(() => service.SetEnabledAsync(mod.Id, enabled));
    }
    public Task SetEnabledAsync(Guid id, bool enabled) => !CanManage ? Task.CompletedTask : RunAsync(() => service.SetEnabledAsync(id, enabled));
    public Task MoveSelectedAsync(int offset)
    {
        if (!CanManage || SelectedMod is not { } mod) return Task.CompletedTask;
        int index = mod.Order - 1 + offset;
        return MoveToIndexAsync(mod.Id, index);
    }
    public Task MoveToIndexAsync(Guid id, int index)
    {
        if (!CanManage || index < 0 || index >= Items.Count || Items.FirstOrDefault(mod => mod.Id == id) is not { } mod
            || mod.Order - 1 == index) return Task.CompletedTask;
        SelectedMod = mod;
        return RunAsync(() => service.MoveAsync(id, index));
    }
    public Task DeleteSelectedAsync() => !CanManage || SelectedMod is not { } mod
        ? Task.CompletedTask : RunAsync(() => service.DeleteAsync(mod.Id));

    public string GetDownloadLabel(ulong itemId)
    {
        InstalledModViewModel[] installed = Items.Where(mod => mod.Snapshot.Mod.WorkshopItemId == itemId).ToArray();
        if (installed.Length > 0) return installed.Any(mod => mod.IsMissing) ? L.Text("status.installedMissing") : L.Text("status.installed");
        return DownloadingItemId == itemId ? L.Text("status.downloading") : L.Text("action.download");
    }
    public string GetDownloadHint(ulong itemId) => Items.Any(mod => mod.Snapshot.Mod.WorkshopItemId == itemId && mod.IsMissing)
        ? L.Text("tip.removeMissing")
        : !IsReady || ShowFolderSetup ? L.Text("tip.setupBeforeDownload")
        : L.Text("tip.download");
    public bool CanDownload(ulong itemId) => CanManage && !Items.Any(mod => mod.Snapshot.Mod.WorkshopItemId == itemId);

    public Task DownloadAsync(ulong itemId)
    {
        if (!CanDownload(itemId)) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            downloadCancellation = cancellation;
            int currentVersion = ++downloadVersion;
            DownloadingItemId = itemId;
            LastDownloadItemId = itemId;
            DownloadStatus = L.Text("status.preparingDownload", itemId);
            var progress = new Progress<string>(message =>
            {
                if (currentVersion == downloadVersion && DownloadingItemId == itemId) DownloadStatus = message;
            });
            try
            {
                await service.DownloadAsync(itemId, progress, cancellation.Token);
                DownloadStatus = L.Text("status.downloadInstalled", itemId);
            }
            catch (OperationCanceledException) { DownloadStatus = L.Text("status.downloadCancelled", itemId); }
            catch (Exception exception) { DownloadStatus = L.Text("status.downloadFailed", exception.Message); throw; }
            finally { downloadCancellation = null; DownloadingItemId = null; }
        });
    }

    private Task RunAsync(Func<Task> action, bool refresh = true)
    {
        if (IsBusy || closing) return Task.CompletedTask;
        namesCancellation?.Cancel();
        itemRefreshVersion++;
        IsBusy = true;
        Error = "";
        return activeOperation = ExecuteAsync();
        async Task ExecuteAsync()
        {
            try { await action(); }
            catch (Exception exception) { Error = L.Text("msg.operationFailed", exception.Message); }
            finally
            {
                if (refresh && IsReady)
                {
                    try { await RefreshItemsAsync(); }
                    catch (Exception exception) { Error = Error.Length == 0 ? exception.Message : L.Text("msg.reloadToRecover", Error); }
                }
                IsBusy = false;
                StartNameRefresh();
            }
        }
    }

    private async Task RefreshItemsAsync()
    {
        int version = ++itemRefreshVersion;
        IReadOnlyList<InstalledModSnapshot> snapshot = await service.ReadAsync();
        if (version != itemRefreshVersion || closing) return;
        Guid? selected = SelectedMod?.Id;
        Items = snapshot.Select(mod => new InstalledModViewModel(mod, ToggleAsync)).ToArray();
        SelectedMod = Items.FirstOrDefault(mod => mod.Id == selected) ?? Items.FirstOrDefault();
        await Task.WhenAll(Items.Select(mod => mod.LoadImageAsync()));
    }

    private void StartNameRefresh()
    {
        if (!CanManage || HasError || !Items.Any(mod => mod.Snapshot.Mod.WorkshopItemId is not null
            && string.IsNullOrWhiteSpace(mod.Snapshot.Mod.DisplayName))) return;
        namesCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        namesCancellation = cancellation;
        nameRefresh = RefreshNamesAsync();
        async Task RefreshNamesAsync()
        {
            try
            {
                bool changed = await service.RefreshWorkshopNamesAsync(cancellation.Token);
                if (changed && !cancellation.IsCancellationRequested && !closing && !IsBusy) await RefreshItemsAsync();
            }
            catch (Exception) { /* Optional names never prevent local mod management. */ }
            finally
            {
                if (ReferenceEquals(namesCancellation, cancellation)) namesCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void NotifyState()
    {
        foreach (string property in new[] { nameof(CanManage), nameof(CanConfigure), nameof(ShowFolderSetup), nameof(IsEmpty), nameof(Summary), nameof(IsDownloading) }) Notify(property);
        ReloadCommand?.Refresh(); ApplyFoldersCommand?.Refresh(); EditFoldersCommand?.Refresh();
        MoveUpCommand?.Refresh(); MoveDownCommand?.Refresh(); DeleteCommand?.Refresh();
        CancelDownloadCommand?.Refresh(); DismissTransferCommand?.Refresh();
    }

    public async Task ShutdownAsync()
    {
        closing = true;
        namesCancellation?.Cancel();
        itemRefreshVersion++;
        downloadCancellation?.Cancel();
        NotifyState();
        await activeOperation;
        await nameRefresh;
        await service.DisposeAsync();
    }

    public Task WaitForIdleAsync() => activeOperation;
    public Task WaitForNamesAsync() => nameRefresh;
}
