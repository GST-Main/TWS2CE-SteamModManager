using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TWS2CESteamModManager.App;
using TWS2CESteamModManager.App.Controls;
using TWS2CESteamModManager.App.Services;
using TWS2CESteamModManager.App.ViewModels;
using TWS2CESteamModManager.Core;
using TWS2CESteamModManager.Steam;

internal static class ModIntegrationChecks
{
    public static async Task<int> RunAsync(Func<MainWindow, string, double, double, Task> render)
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
            Console.WriteLine("PASS " + message);
        }
        string root = Path.GetFullPath(Path.Combine(".test-results", "ui-integration-" + Guid.NewGuid().ToString("N")));
        string storage = Path.Combine(root, "portable"), game = Path.Combine(root, "game"), data = Path.Combine(game, "data");
        string roaming = Path.Combine(root, "roaming"), appData = GameConstants.GetDefaultAppDataPath(roaming);
        Directory.CreateDirectory(storage); Directory.CreateDirectory(data); Directory.CreateDirectory(appData);
        File.WriteAllText(Path.Combine(game, GameConstants.GameExecutable), "fixture");
        File.WriteAllText(Path.Combine(data, "MOD_101_installed.pack"), "installed payload");
        File.WriteAllText(Path.Combine(data, "MOD_103_manual.pack"), "manual payload");
        File.WriteAllText(Path.Combine(data, "legacy.pack"), "legacy payload");
        var folders = new ManagerManifest { AppDataPath = appData, GameInstallPath = game };
        new AppConfigStore(Path.Combine(storage, "config.json")).Update(current => current with { AppDataPath = appData, GameInstallPath = game });
        ModManifestStore.Save(Path.Combine(storage, "mods.json"), new ModManifest { Mods = new[] {
            new ManagedMod { Id = Guid.NewGuid(), FileName = "MOD_101_installed.pack", OriginalFileName = "installed.pack", Source = ModSource.Workshop, WorkshopItemId = 101, Order = 0 },
            new ManagedMod { Id = Guid.NewGuid(), FileName = "MOD_102_missing.pack", OriginalFileName = "missing.pack", Source = ModSource.Workshop, WorkshopItemId = 102, Order = 1 },
            new ManagedMod { Id = Guid.NewGuid(), FileName = "MOD_103_manual.pack", OriginalFileName = "MOD_103_manual.pack", Source = ModSource.Imported, Order = 2 }
        } });
        UserScriptService.LoadOrCreate(appData);
        string scriptPath = GameConstants.GetUserScriptPath(appData);
        File.WriteAllText(scriptPath, "# keep settings\nmod legacy.pack;\nother_setting true;\n", System.Text.Encoding.Unicode);

        var transport = new FixtureTransport(Path.Combine(root, "packages"));
        var service = new ModManagementService(transport, storage, new GameLocationService(roaming, Array.Empty<IGameInstallationLocator>()), transport);
        var mods = new ModManagerViewModel(service);
        var workshop = new FixtureBrowser();
        var browser = new WorkshopBrowserViewModel(workshop, new NoImages(), mods);
        var launches = new List<ProcessStartInfo>();
        bool failLaunch = false;
        var launcher = new ApplicationLauncher(info =>
        {
            if (failLaunch) throw new System.ComponentModel.Win32Exception("Launch failed.");
            launches.Add(info);
        });
        var shell = new MainWindowViewModel(browser, mods, launcher);
        Check(!shell.LaunchCommand.CanExecute(null), "game launch is disabled until folders finish loading");
        await shell.InitializeAsync();
        Check(shell.IsModsSelected && mods.IsReady && !mods.HasError && mods.Items.Count == 4, "startup loads persisted mods and adopts existing script entries");
        Check(shell.CanLaunch && shell.LaunchCommand.CanExecute(null), "resolved game folders enable the top Launch button");
        shell.LaunchCommand.Execute(null);
        Check(launches.Count == 1 && launches[0].FileName == "steam://run/3846830" && launches[0].UseShellExecute,
            "Launch asks Steam to start Complete Edition using its game App ID rather than the Workshop App ID");
        failLaunch = true;
        shell.LaunchCommand.Execute(null);
        Check(shell.LaunchError.Contains("Couldn't launch the game") && shell.CanLaunch,
            "failed game launch exposes an error and stays retryable");
        failLaunch = false;
        shell.LaunchCommand.Execute(null);
        Check(shell.LaunchError == "" && launches.Count == 2, "successful launch retry clears its previous error");
        string gameExecutable = Path.Combine(game, GameConstants.GameExecutable);
        File.Delete(gameExecutable);
        shell.LaunchCommand.Execute(null);
        Check(shell.LaunchError.Contains("executable was not found") && launches.Count == 2,
            "a removed game executable reports a helpful error without launching anything");
        File.WriteAllText(gameExecutable, "fixture");
        shell.LaunchCommand.Execute(null);
        mods.EditFoldersCommand.Execute(null);
        Check(!shell.CanLaunch && !shell.LaunchCommand.CanExecute(null), "editing folders disables game launch");
        mods.EditFoldersCommand.Execute(null);
        Check(shell.CanLaunch, "finishing folder editing restores game launch");
        Check(mods.Items.Single(mod => mod.Snapshot.Mod.WorkshopItemId == 102).Status == "Missing file", "installed list distinguishes missing packs");
        Check(browser.Items.Single(item => item.Item.Id == 101).DownloadLabel == "Installed", "installed label matches Workshop ID despite a different title");
        Check(browser.Items.Single(item => item.Item.Id == 102).DownloadLabel == "Installed · missing files"
            && !browser.Items.Single(item => item.Item.Id == 102).DownloadCommand.CanExecute(null), "missing managed item cannot be duplicated");
        Check(browser.Items.Single(item => item.Item.Id == 103).DownloadCommand.CanExecute(null), "an imported MOD_ filename does not imply a Workshop association");
        var window = new MainWindow(shell);
        await render(window, "mods-installed", 1440, 900);
        await render(window, "mods-installed-small", 1080, 700);

        byte[] manifestBeforeDetails = File.ReadAllBytes(Path.Combine(storage, "mods.json"));
        byte[] scriptBeforeDetails = File.ReadAllBytes(scriptPath);
        Button nameButton = Descendants<Button>((DependencyObject)window.Content)
            .First(button => button.Tag is InstalledModViewModel row && row.Snapshot.Mod.WorkshopItemId == 101);
        nameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, nameButton));
        Check(shell.IsModsSelected && browser.Detail is { IsInstalledDetail: true, HasWorkshopItem: true }, "clicking an installed mod name opens detail without leaving Mods");
        Check(browser.Detail!.InstalledMod!.FilePath == Path.Combine(data, "MOD_101_installed.pack")
            && browser.Detail.Description == "Full description" && browser.Detail.ShowWorkshopMetadata, "installed Workshop detail includes local path and remote description");
        await render(window, "mod-detail-workshop", 1440, 1000);
        browser.CloseDetails();
        int callsBeforeLocalDetail = workshop.DetailCalls;
        await shell.OpenInstalledDetailsAsync(mods.Items.Single(mod => mod.Source == "Imported" && mod.FileName == "MOD_103_manual.pack"));
        Check(browser.Detail is { HasWorkshopItem: false, ShowWorkshopMetadata: false, ShowGallery: false }
            && browser.Detail.InstalledMod!.WorkshopId == "None (local import)", "imported mod detail shows local information without fake Workshop metadata");
        Check(workshop.DetailCalls == callsBeforeLocalDetail && browser.Detail!.Description.Contains("imported locally"), "local detail never queries Steam");
        await render(window, "mod-detail-imported", 1080, 700);
        browser.CloseDetails();
        workshop.FailDetails = true;
        await shell.OpenInstalledDetailsAsync(mods.Items.Single(mod => mod.Snapshot.Mod.WorkshopItemId == 102));
        Check(browser.Detail is { HasError: true, ShowWorkshopMetadata: false }
            && browser.Detail.InstalledMod!.Status == "Missing file", "missing pack details stay available when the Workshop query fails");
        Check(browser.Detail!.Card!.DownloadLabel == "Installed · missing files", "missing installed detail retains the correct download label");
        await render(window, "mod-detail-missing", 1080, 700);
        browser.CloseDetails();
        workshop.FailDetails = false;
        Check(manifestBeforeDetails.SequenceEqual(File.ReadAllBytes(Path.Combine(storage, "mods.json")))
            && scriptBeforeDetails.SequenceEqual(File.ReadAllBytes(scriptPath)), "viewing mod details never changes manifest or script state");

        InstalledModViewModel first = mods.Items[0];
        first.IsEnabled = false;
        await mods.WaitForIdleAsync();
        Check(!mods.Items.Single(mod => mod.Id == first.Id).IsEnabled && !UserScriptService.LoadOrCreate(appData).Mods.Contains(first.FileName), "checkbox persists disabled state and script removal");
        await mods.SetEnabledAsync(first.Id, true);
        mods.SelectedMod = mods.Items.Single(mod => mod.Id == first.Id);
        await mods.MoveSelectedAsync(1);
        Check(mods.Items[1].Id == first.Id && mods.SelectedMod?.Id == first.Id, "Move Down preserves selection and saves order");
        Check(UserScriptService.LoadOrCreate(appData).Mods.SequenceEqual(mods.Items.Where(mod => mod.IsEnabled).Select(mod => mod.FileName)), "enabled script follows the visible order using filenames");
        await mods.MoveSelectedAsync(-1);
        Check(mods.Items[0].Id == first.Id && !mods.MoveUpCommand.CanExecute(null), "Move Up respects the first position");
        foreach (var move in new[] { (0, 4, 3), (3, 0, 0), (1, 2, 1), (1, 1, 1), (2, 1, 1), (3, 4, 3), (0, 1, 0) })
            Check(ModReorder.TargetIndex(move.Item1, move.Item2, 4) == move.Item3, $"drag boundary {move.Item2} from row {move.Item1} resolves to row {move.Item3}");
        await mods.MoveToIndexAsync(first.Id, ModReorder.TargetIndex(0, mods.Items.Count, mods.Items.Count));
        Check(mods.Items[^1].Id == first.Id && mods.SelectedMod?.Id == first.Id, "drag move to the end persists and retains selection");
        Check(UserScriptService.LoadOrCreate(appData).Mods.SequenceEqual(mods.Items.Where(mod => mod.IsEnabled).Select(mod => mod.FileName)), "drag move updates script load order");
        await mods.MoveToIndexAsync(first.Id, 0);
        byte[] beforeInvalidMove = File.ReadAllBytes(Path.Combine(storage, "mods.json"));
        await mods.MoveToIndexAsync(first.Id, -1);
        Check(beforeInvalidMove.SequenceEqual(File.ReadAllBytes(Path.Combine(storage, "mods.json"))), "invalid drag target leaves the manifest unchanged");

        string importFolder = Path.Combine(root, "imports"); Directory.CreateDirectory(importFolder);
        string importedPack = Path.Combine(importFolder, "Local 日本語.pack");
        File.WriteAllText(importedPack, "imported payload");
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=");
        File.WriteAllBytes(Path.ChangeExtension(importedPack, ".png"), png);
        await mods.ImportAsync(new[] { importedPack });
        Check(mods.Items[^1].Name == "Local 日本語" && mods.Items[^1].FileName == "Local 日本語.pack" && mods.Items[^1].IsEnabled && mods.Items[^1].Source == "Imported", "Import displays a persisted filename stem while retaining its installed filename");
        Check(File.Exists(Path.Combine(data, "Local 日本語.png")), "manual image is copied for the installed view");
        Check(mods.Items[^1].Image is not null, "installed view decodes a manual mod image");
        await shell.OpenInstalledDetailsAsync(mods.Items[^1]);
        Check(browser.Detail is { HasWorkshopItem: false, HasScreenshots: true } && browser.Detail.SelectedScreenshot!.HasImage,
            "local detail shows its imported image without a Workshop association");
        browser.CloseDetails();
        mods.SelectedMod = mods.Items[^1];
        await mods.DeleteSelectedAsync();
        Check(!File.Exists(Path.Combine(data, "Local 日本語.pack")) && !File.Exists(Path.Combine(data, "Local 日本語.png")), "Delete removes the selected pack and image");

        Guid toggleId = mods.Items[0].Id;
        bool enabledBefore = mods.Items[0].IsEnabled;
        using (var locked = new FileStream(scriptPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            await mods.SetEnabledAsync(toggleId, !enabledBefore);
        Check(mods.HasError && mods.Items.Single(mod => mod.Id == toggleId).IsEnabled == enabledBefore, "IO failure restores checkbox state and exposes an error");

        transport.WaitForRelease = true;
        Task download = mods.DownloadAsync(200);
        await transport.Entered.Task;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(mods.IsDownloading && mods.IsBusy && !mods.CanManage, "downloading disables overlapping mod mutations");
        int launchesBeforeDownload = launches.Count;
        shell.LaunchCommand.Execute(null);
        Check(!shell.CanLaunch && launches.Count == launchesBeforeDownload, "game cannot launch while mod files are being installed");
        Check(browser.Items.Single(item => item.Item.Id == 200).DownloadLabel == "Downloading…", "browser label tracks the active item");
        Check(!browser.Items.Single(item => item.Item.Id == 201).DownloadCommand.CanExecute(null), "a second download cannot start concurrently");
        shell.IsModsSelected = false;
        await render(window, "browse-download-progress", 1440, 900);
        await browser.OpenDetailsAsync(browser.Items.Single(item => item.Item.Id == 200));
        Check(browser.Detail!.Card!.IsDownloading && browser.Detail.Card.CancelDownloadCommand!.CanExecute(null), "detail popup shares active download and cancellation state");
        await render(window, "detail-download-progress", 1440, 900);
        transport.Release.Set();
        await download;
        Check(!mods.IsBusy && !mods.HasError && mods.Items[^1].Snapshot.Mod.WorkshopItemId == 200 && mods.Items[^1].IsEnabled, "download installs and appends an enabled managed mod");
        Check(shell.CanLaunch, "game launch is available again after installation completes");
        Check(File.Exists(Path.Combine(data, "MOD_200_payload.pack")) && File.Exists(Path.Combine(data, "MOD_200_payload.png")), "integrated download installs pack and thumbnail sidecar");
        Check(mods.Items[^1].Image is not null, "installed view decodes the downloaded thumbnail");
        Check(mods.Items[^1].Name == "Campaign expansion 200" && mods.Items[^1].Snapshot.Mod.DisplayName == "Campaign expansion 200", "downloaded Workshop title is saved and displayed");
        Check(browser.Items.Single(item => item.Item.Id == 200).DownloadLabel == "Installed"
            && browser.Detail!.Card.DownloadLabel == "Installed", "browser and open detail labels update after installation");
        await render(window, "detail-installed", 1440, 900);
        browser.CloseDetails();

        transport.ResetWait();
        Task cancelled = mods.DownloadAsync(201);
        await transport.Entered.Task;
        mods.CancelDownloadCommand.Execute(null);
        await cancelled;
        Check(!mods.IsDownloading && !mods.IsBusy && mods.DownloadStatus.Contains("cancelled"), "download cancellation completes and restores controls");
        Check(mods.Items.All(mod => mod.Snapshot.Mod.WorkshopItemId != 201) && !File.Exists(Path.Combine(data, "MOD_201_payload.pack")), "cancelled download creates no installed entry");
        transport.WaitForRelease = false; transport.Fail = true;
        await mods.DownloadAsync(201);
        Check(mods.HasError && mods.DownloadStatus.StartsWith("Download failed") && mods.CanDownload(201), "download failure remains retryable");
        transport.Fail = false;
        await mods.DownloadAsync(201);
        Check(!mods.HasError && !mods.CanDownload(201), "retry succeeds and prevents a duplicate download");
        mods.SelectedMod = mods.Items.Single(mod => mod.Snapshot.Mod.WorkshopItemId == 200);
        await mods.DeleteSelectedAsync();
        Check(browser.Items.Single(item => item.Item.Id == 200).DownloadLabel == "Download"
            && browser.Items.Single(item => item.Item.Id == 200).DownloadCommand.CanExecute(null), "delete makes the Workshop item downloadable again");
        Check(File.ReadAllText(scriptPath).Contains("# keep settings") && File.ReadAllText(scriptPath).Contains("other_setting true;"), "integrated mutations retain unrelated script content");
        shell.IsModsSelected = true;
        await render(window, "mods-after-download", 1440, 900);
        transport.ResetWait();
        Task closingDownload = mods.DownloadAsync(202);
        await transport.Entered.Task;
        await shell.ShutdownAsync();
        await closingDownload;
        Check(!shell.LaunchCommand.CanExecute(null), "shutdown prevents further game launches");
        Check(!mods.IsBusy && !mods.IsDownloading && mods.Items.All(mod => mod.Snapshot.Mod.WorkshopItemId != 202), "shutdown cancels an active download before releasing the mod session");
        window.Close();

        var reopened = new ModManagerViewModel(new ModManagementService(transport, storage,
            new GameLocationService(roaming, Array.Empty<IGameInstallationLocator>()), transport));
        await reopened.InitializeAsync();
        Check(reopened.IsReady && reopened.Items.Any(mod => mod.Snapshot.Mod.WorkshopItemId == 201), "installed state survives reopening from manifests");
        await reopened.ShutdownAsync();

        string namesStorage = Path.Combine(root, "names-portable"); Directory.CreateDirectory(namesStorage);
        new AppConfigStore(Path.Combine(namesStorage, "config.json")).Update(current => current with { AppDataPath = appData, GameInstallPath = game });
        File.Copy(Path.Combine(storage, "mods.json"), Path.Combine(namesStorage, "mods.json"));
        var metadata = new MetadataInstaller();
        var namedMods = new ModManagerViewModel(new ModManagementService(metadata, namesStorage));
        await namedMods.InitializeAsync();
        await metadata.Entered.Task;
        Check(namedMods.IsReady && !namedMods.IsBusy && namedMods.CanManage, "background title lookup leaves local mod management available");
        Guid namedFirst = namedMods.Items[0].Id;
        await namedMods.MoveToIndexAsync(namedFirst, 1).WaitAsync(TimeSpan.FromSeconds(3));
        Check(metadata.FirstToken.IsCancellationRequested && namedMods.Items[1].Id == namedFirst, "local reorder cancels optional title lookup without waiting for Steam");
        metadata.Release.TrySetResult();
        await namedMods.WaitForNamesAsync();
        Check(namedMods.Items.Single(mod => mod.Snapshot.Mod.WorkshopItemId == 101).Name == "Resolved Workshop name 101", "older installed Workshop title is fetched and displayed");
        Check(ModManifestStore.Load(Path.Combine(namesStorage, "mods.json"))!.Mods.Single(mod => mod.WorkshopItemId == 101).DisplayName == "Resolved Workshop name 101", "resolved old title is saved to the mod manifest");
        Check(namedMods.Items.Single(mod => mod.Snapshot.Mod.WorkshopItemId == 102).Name == "MOD_102_missing.pack" && !namedMods.HasError,
            "unavailable item metadata retains a filename fallback without a management error");
        Check(namedMods.Items.Single(mod => mod.Snapshot.Mod.WorkshopItemId == 201).Name == "Campaign expansion 201", "saved names remain unchanged during metadata backfill");
        var namesShell = new MainWindowViewModel(new WorkshopBrowserViewModel(new FixtureBrowser(), new NoImages()), namedMods);
        var namesWindow = new MainWindow(namesShell);
        await render(namesWindow, "mods-display-names", 1440, 900);
        await render(namesWindow, "mods-display-names-small", 1080, 700);
        await namesShell.ShutdownAsync(); namesWindow.Close();

        byte[] savedFolders = File.ReadAllBytes(Path.Combine(storage, "config.json"));
        byte[] savedMods = File.ReadAllBytes(Path.Combine(storage, "mods.json"));
        byte[] savedScript = File.ReadAllBytes(scriptPath);
        var forcedService = new ModManagementService(transport, storage,
            new GameLocationService(roaming, Array.Empty<IGameInstallationLocator>()), transport, forceFolderSetup: true);
        var forcedMods = new ModManagerViewModel(forcedService);
        await forcedMods.InitializeAsync();
        Check(forcedMods.ShowFolderSetup && !forcedMods.IsReady && !forcedMods.HasError
            && forcedMods.AppDataPath == "" && forcedMods.GameInstallPath == "",
            "forced setup ignores saved and detectable paths and shows empty folder selectors");
        Check(!forcedMods.CanManage && !forcedMods.CanDownload(200), "forced setup blocks mod changes and downloads");
        await forcedMods.InitializeAsync();
        Check(forcedMods.ShowFolderSetup && forcedMods.AppDataPath == "" && forcedMods.GameInstallPath == "",
            "reload keeps manual folder setup required until folders are saved");
        await forcedMods.ShutdownAsync();
        Check(savedFolders.SequenceEqual(File.ReadAllBytes(Path.Combine(storage, "config.json")))
            && savedMods.SequenceEqual(File.ReadAllBytes(Path.Combine(storage, "mods.json")))
            && savedScript.SequenceEqual(File.ReadAllBytes(scriptPath)),
            "exiting forced setup preserves saved manifests and the game script");
        await using (var applyService = new ModManagementService(transport, storage,
            new GameLocationService(roaming, Array.Empty<IGameInstallationLocator>()), transport, forceFolderSetup: true))
        {
            await applyService.OpenAsync(folders);
            Check((await applyService.ResolveAsync()).Status == SetupStatus.Ready,
                "saving manual folder selections restores normal resolution for the session");
        }

        string missingStorage = Path.Combine(root, "new-portable");
        var setupMods = new ModManagerViewModel(new ModManagementService(transport, missingStorage,
            new GameLocationService(Path.Combine(root, "missing-roaming"), Array.Empty<IGameInstallationLocator>()), transport));
        var setupBrowser = new WorkshopBrowserViewModel(new FixtureBrowser(), new NoImages(), setupMods);
        var setupShell = new MainWindowViewModel(setupBrowser, setupMods);
        await setupShell.InitializeAsync();
        Check(setupMods.ShowFolderSetup && !setupMods.IsReady && !setupMods.CanDownload(200), "missing folders show setup and block installation");
        Check(!setupShell.CanLaunch, "missing folders keep the game Launch button disabled");
        Check(!Directory.Exists(Path.Combine(root, "missing-roaming")), "UI setup does not fabricate a missing app-data folder");
        var setupWindow = new MainWindow(setupShell);
        await render(setupWindow, "mods-folder-setup", 1080, 700);
        setupMods.AppDataPath = appData; setupMods.GameInstallPath = game;
        await setupMods.ApplyFoldersAsync();
        Check(setupMods.IsReady && !setupMods.ShowFolderSetup && new AppConfigStore(Path.Combine(missingStorage, "config.json")).Load().GetFolders() == folders, "manual folder selection saves resolved paths in config.json");
        Check(setupShell.CanLaunch, "saving valid folders enables game launch without restarting the app");
        await setupShell.ShutdownAsync(); setupWindow.Close();
        string corruptStorage = Path.Combine(root, "corrupt-portable"); Directory.CreateDirectory(corruptStorage);
        string corruptManifest = Path.Combine(corruptStorage, "config.json");
        File.WriteAllText(corruptManifest, "{broken");
        var corruptMods = new ModManagerViewModel(new ModManagementService(transport, corruptStorage,
            new GameLocationService(roaming, Array.Empty<IGameInstallationLocator>()), transport));
        await corruptMods.InitializeAsync();
        Check(!corruptMods.IsReady && corruptMods.HasError && File.ReadAllText(corruptManifest) == "{broken", "startup reports a corrupt manifest without replacing it");
        await corruptMods.ShutdownAsync();
        Console.WriteLine("Integration fixtures: " + root);
        return count;
    }

    private sealed class NoImages : IThumbnailLoader
    {
        public Task<ImageSource?> LoadAsync(string? url, CancellationToken cancellationToken) => Task.FromResult<ImageSource?>(null);
    }
    private sealed class FixtureBrowser : IWorkshopBrowserService
    {
        public bool FailDetails { get; set; }
        public int DetailCalls { get; private set; }
        private static WorkshopItem Item(ulong id) => new(id, "Workshop title " + id, "Full description", 123,
            DateTimeOffset.UtcNow, "campaign,historical", 100, 2, null, "Test creator");
        public Task<IReadOnlyList<WorkshopTag>> FetchTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkshopTag>>(Array.Empty<WorkshopTag>());
        public Task<WorkshopPage> FetchItemsAsync(WorkshopOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkshopPage(1, 5, new ulong[] { 101, 102, 103, 200, 201 }.Select(Item).ToArray()));
        public Task<WorkshopItemDetails> FetchDetailsAsync(ulong itemId, CancellationToken cancellationToken = default)
        {
            DetailCalls++;
            return FailDetails ? Task.FromException<WorkshopItemDetails>(new IOException("Workshop unavailable"))
                : Task.FromResult(new WorkshopItemDetails(Item(itemId), Array.Empty<WorkshopScreenshot>()));
        }
        public void Dispose() { }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class MetadataInstaller : IWorkshopModInstaller, IWorkshopBrowserService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstToken { get; private set; }
        public Task<IReadOnlyList<ManagedMod>> InstallAsync(ModManager manager, ulong itemId, Action<string>? report = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<WorkshopTag>> FetchTagsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkshopPage> FetchItemsAsync(WorkshopOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<WorkshopItemDetails> FetchDetailsAsync(ulong itemId, CancellationToken cancellationToken = default)
        {
            if (!Entered.Task.IsCompleted) { FirstToken = cancellationToken; Entered.TrySetResult(); }
            await Release.Task.WaitAsync(cancellationToken);
            if (itemId == 102) throw new IOException("This item is unavailable");
            return new WorkshopItemDetails(new WorkshopItem(itemId, "Resolved Workshop name " + itemId, "Description", 123,
                DateTimeOffset.UtcNow, "campaign", 1, 0, null), Array.Empty<WorkshopScreenshot>());
        }
        public void Dispose() { }
    }
    private sealed class FixtureTransport(string root) : IWorkshopModInstaller, IWorkshopModDownloader
    {
        public bool WaitForRelease { get; set; }
        public bool Fail { get; set; }
        public string? LastThreadName { get; private set; }
        public ManualResetEventSlim Release { get; private set; } = new(false);
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ResetWait() { Release = new(false); Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); WaitForRelease = true; }
        public Task<IReadOnlyList<ManagedMod>> InstallAsync(ModManager manager, ulong itemId, Action<string>? report = null, CancellationToken cancellationToken = default) =>
            Task.Run(() => manager.Download(itemId, report, cancellationToken), cancellationToken);
        public WorkshopModPackage Download(ulong itemId, string cacheDirectory, Action<string>? report = null, CancellationToken cancellationToken = default)
        {
            LastThreadName = Thread.CurrentThread.Name;
            Entered.TrySetResult();
            report?.Invoke("Downloaded 10 / 100 bytes");
            if (WaitForRelease) Release.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("Simulated transfer failure");
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string pack = Path.Combine(directory, "payload.pack"), image = Path.Combine(directory, "preview.png");
            File.WriteAllText(pack, "Workshop payload");
            File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII="));
            return new WorkshopModPackage(directory, new[] { pack }, image, $"Campaign expansion {itemId}");
        }
    }

    public static async Task<int> CheckSteamWorkerAsync(IWorkshopModInstaller installer)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "worker-fixture-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "game"), appData = Path.Combine(root, "appdata");
        Directory.CreateDirectory(Path.Combine(game, "data")); Directory.CreateDirectory(appData);
        File.WriteAllText(Path.Combine(game, GameConstants.GameExecutable), "fixture");
        var transport = new FixtureTransport(Path.Combine(root, "packages"));
        await using var service = new ModManagementService(installer, Path.Combine(root, "portable"), downloader: transport);
        await service.OpenAsync(new ManagerManifest { AppDataPath = appData, GameInstallPath = game });
        await service.DownloadAsync(300, new Progress<string>(), CancellationToken.None);
        IReadOnlyList<InstalledModSnapshot> installed = await service.ReadAsync();
        if (transport.LastThreadName != "Steam Workshop worker" || installed.Single().Mod.WorkshopItemId != 300 || !installed[0].IsPresent)
            throw new InvalidOperationException("Steam worker did not install the isolated package on its owning thread.");
        Console.WriteLine("PASS real Steam worker installs an isolated fake package on its owning thread (no Workshop content transfer)");
        return 1;
    }
}
