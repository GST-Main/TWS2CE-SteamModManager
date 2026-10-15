using System.IO;
using TWS2CESteamModManager.App.Localization;
using TWS2CESteamModManager.Core;
using TWS2CESteamModManager.Steam;

namespace TWS2CESteamModManager.App.Services;

public sealed record InstalledModSnapshot(ManagedMod Mod, bool IsPresent, string? ImagePath, string? FilePath = null);

public interface IModManagementService : IAsyncDisposable
{
    Task<GameLocationResult> ResolveAsync();
    Task OpenAsync(ManagerManifest folders);
    Task<IReadOnlyList<InstalledModSnapshot>> ReadAsync();
    Task ImportAsync(string path, CancellationToken cancellationToken);
    Task SetEnabledAsync(Guid id, bool enabled);
    Task MoveAsync(Guid id, int index);
    Task DeleteAsync(Guid id);
    Task DownloadAsync(ulong itemId, IProgress<string> progress, CancellationToken cancellationToken);
    Task<bool> RefreshWorkshopNamesAsync(CancellationToken cancellationToken);
}

/// <summary>Serializes folder/session changes and keeps file IO off the UI thread.</summary>
public sealed class ModManagementService : IModManagementService
{
    private readonly string storageDirectory;
    private readonly AppConfigStore config;
    private readonly GameLocationService locations;
    private readonly IWorkshopModInstaller installer;
    private readonly IWorkshopModDownloader downloader;
    private readonly SemaphoreSlim gate = new(1);
    private ModManager? manager;
    private bool disposed;
    private bool forceFolderSetup;
    private int sessionVersion;

    public ModManagementService(IWorkshopModInstaller installer, string? storageDirectory = null,
        GameLocationService? locations = null, IWorkshopModDownloader? downloader = null, bool forceFolderSetup = false)
    {
        this.installer = installer;
        this.storageDirectory = Path.GetFullPath(storageDirectory ?? AppContext.BaseDirectory);
        config = new AppConfigStore(Path.Combine(this.storageDirectory, "config.json"));
        this.locations = locations ?? new GameLocationService();
        this.downloader = downloader ?? new SteamWorkshopModDownloader(localize: L.Text);
        this.forceFolderSetup = forceFolderSetup;
    }

    public Task<GameLocationResult> ResolveAsync() => RunAsync(() => Task.Run(() =>
        forceFolderSetup
            ? new GameLocationResult(new ManagerManifest(), SetupStatus.NeedsBothFolders,
                [L.Text("msg.manualSetupRequested")])
            : locations.Resolve(config.Load().GetFolders())));

    public Task OpenAsync(ManagerManifest folders) => RunAsync(async () =>
    {
        await Task.Run(() =>
        {
            ManagerManifest validated = GameLocationService.SelectAppDataFolder(folders, folders.AppDataPath ?? "");
            validated = GameLocationService.SelectGameInstallFolder(validated, folders.GameInstallPath ?? "");
            manager?.Dispose();
            manager = null;
            sessionVersion++;
            config.Update(current => current with { AppDataPath = validated.AppDataPath, GameInstallPath = validated.GameInstallPath });
            manager = ModManager.Open(validated, Path.Combine(storageDirectory, "mods.json"), downloader);
            forceFolderSetup = false;
        });
        return true;
    });

    public Task<IReadOnlyList<InstalledModSnapshot>> ReadAsync() => RunAsync(() => Task.Run<IReadOnlyList<InstalledModSnapshot>>(() =>
        RequireManager().List().Select(mod => new InstalledModSnapshot(mod,
            File.Exists(RequireManager().GetFilePath(mod.Id)), RequireManager().GetImagePath(mod.Id), RequireManager().GetFilePath(mod.Id))).ToArray()));

    public Task ImportAsync(string path, CancellationToken cancellationToken) => ChangeAsync(() => RequireManager().Import(path, cancellationToken));
    public Task SetEnabledAsync(Guid id, bool enabled) => ChangeAsync(() => RequireManager().SetEnabled(id, enabled));
    public Task MoveAsync(Guid id, int index) => ChangeAsync(() => RequireManager().Move(id, index));
    public Task DeleteAsync(Guid id) => ChangeAsync(() => RequireManager().Delete(id));
    public Task DownloadAsync(ulong itemId, IProgress<string> progress, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await installer.InstallAsync(RequireManager(), itemId, progress.Report, cancellationToken);
        return true;
    });

    public async Task<bool> RefreshWorkshopNamesAsync(CancellationToken cancellationToken)
    {
        if (installer is not IWorkshopBrowserService metadata) return false;
        var session = await RunAsync(() => Task.Run(() => (Version: sessionVersion, Ids: RequireManager().List()
            .Where(mod => mod.WorkshopItemId is not null && string.IsNullOrWhiteSpace(mod.DisplayName))
            .Select(mod => mod.WorkshopItemId!.Value).Distinct().ToArray()))).ConfigureAwait(false);
        var titles = new Dictionary<ulong, string>();
        foreach (ulong id in session.Ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                WorkshopItemDetails details = await metadata.FetchDetailsAsync(id, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(details.Item.Title)) titles[id] = details.Item.Title;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            // Names are optional: keep the filename fallback when Steam is unavailable.
            catch (Exception exception) when (exception is SteamUnavailableException or DllNotFoundException or BadImageFormatException or TimeoutException) { break; }
            catch (Exception) { /* An inaccessible item must not hide other available titles. */ }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (titles.Count == 0) return false;
        return await RunAsync(() => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return session.Version == sessionVersion && RequireManager().SetWorkshopDisplayNames(titles);
        })).ConfigureAwait(false);
    }

    private Task ChangeAsync(Action action) => RunAsync(async () => { await Task.Run(action); return true; });
    private ModManager RequireManager() => manager ?? throw new InvalidOperationException(L.Text("msg.setupRequired"));
    private async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            manager?.Dispose();
            manager = null;
        }
        finally { gate.Release(); }
    }
}
