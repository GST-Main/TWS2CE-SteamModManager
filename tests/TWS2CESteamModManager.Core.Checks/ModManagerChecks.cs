using System.Text;
using System.Text.Json;
using TWS2CESteamModManager.Core;

internal static class ModManagerChecks
{
    public static int Run(string parent)
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            count++;
            Console.WriteLine($"PASS {message}");
        }
        void Reject<T>(Action action, string message) where T : Exception
        {
            bool rejected = false;
            try { action(); }
            catch (T) { rejected = true; }
            Check(rejected, message);
        }

        string root = Path.Combine(parent, "mod-management");
        Directory.CreateDirectory(root);
        string game = Path.Combine(root, "game");
        string data = Path.Combine(game, "data");
        string appData = Path.Combine(root, "appdata");
        string sources = Path.Combine(root, "imports");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(sources);
        File.WriteAllText(Path.Combine(game, GameConstants.GameExecutable), "fixture");
        var folders = new ManagerManifest { AppDataPath = appData, GameInstallPath = game };
        string manifestPath = Path.Combine(root, "mods.json");
        string scriptPath = GameConstants.GetUserScriptPath(appData);
        UserScriptService.LoadOrCreate(appData);
        File.WriteAllText(Path.Combine(data, "base_game.pack"), "untouchable base pack");
        File.WriteAllText(Path.Combine(data, "existing.pack"), "existing local mod");
        File.WriteAllText(scriptPath, "# initial comment\r\nmod existing.pack; // keep inline comment\r\nother_setting true;\r\n", Encoding.Unicode);

        var downloader = new FakeDownloader(Path.Combine(root, "packages"));
        Guid importedId, firstWorkshopId, secondWorkshopId;
        using (ModManager manager = ModManager.Open(folders, manifestPath, downloader))
        {
            IReadOnlyList<ManagedMod> initial = manager.List();
            Check(initial.Count == 1 && initial[0].FileName == "existing.pack" && initial[0].IsEnabled,
                "existing script entries adopted as local enabled mods");
            Check(initial[0].Source == ModSource.Imported && initial[0].WorkshopItemId is null,
                "adopted local mods have no inferred Workshop association");
            Check(manager.List().All(mod => mod.FileName != "base_game.pack"), "base-game data packs are not auto-managed");
            Check(File.ReadAllText(scriptPath).Contains("# initial comment")
                && File.ReadAllText(scriptPath).Contains("// keep inline comment")
                && File.ReadAllText(scriptPath).Contains("other_setting true;"),
                "script synchronization preserves standalone/inline comments and other settings");
            Reject<IOException>(() => { using ModManager other = ModManager.Open(folders, manifestPath); },
                "concurrent manager for the same manifest is rejected");

            string importSource = Path.Combine(sources, "local 日本語.pack");
            File.WriteAllText(importSource, "manual import bytes");
            ManagedMod imported = manager.Import(importSource);
            importedId = imported.Id;
            Check(imported.FileName == "local 日本語.pack" && imported.Order == 1 && imported.IsEnabled
                && imported.WorkshopItemId is null, "import appends enabled without renaming or Workshop metadata");
            Check(File.ReadAllText(manager.GetFilePath(imported.Id)) == "manual import bytes"
                && File.ReadAllText(importSource) == "manual import bytes", "import copies into data and preserves its source");
            Check(UserScriptService.LoadOrCreate(appData).Mods.SequenceEqual(new[] { "existing.pack", "local 日本語.pack" }),
                "import immediately updates enabled script order including spaced/Unicode filenames");

            string inPlace = Path.Combine(data, "MOD_777_manually_added.pack");
            File.WriteAllText(inPlace, "already in data");
            ManagedMod localPrefix = manager.Import(inPlace);
            Check(localPrefix.Source == ModSource.Imported && localPrefix.WorkshopItemId is null
                && File.ReadAllText(inPlace) == "already in data", "in-place import works and does not infer an ID from a MOD_ filename");
            Reject<InvalidOperationException>(() => manager.Import(inPlace), "duplicate managed import rejected");
            string caseDuplicate = Path.Combine(sources, "EXISTING.pack");
            File.WriteAllText(caseDuplicate, "must not overwrite");
            Reject<InvalidOperationException>(() => manager.Import(caseDuplicate), "case-insensitive duplicate filename rejected");
            string collision = Path.Combine(sources, "base_game.pack");
            File.WriteAllText(collision, "collision");
            Reject<IOException>(() => manager.Import(collision), "unmanaged data filename collision rejected");
            Check(File.ReadAllText(Path.Combine(data, "base_game.pack")) == "untouchable base pack", "unmanaged/base pack survives collision");

            IReadOnlyList<ManagedMod> downloaded = manager.Download(42);
            firstWorkshopId = downloaded[0].Id;
            secondWorkshopId = downloaded[1].Id;
            Check(downloaded.Select(mod => mod.FileName).SequenceEqual(new[] { "MOD_42_alpha.pack", "MOD_42_Zebra.PACK" }),
                "Workshop packs use MOD_<item ID>_<original filename> with deterministic multi-pack order");
            Check(downloaded.All(mod => mod.IsEnabled && mod.Source == ModSource.Workshop && mod.WorkshopItemId == 42)
                && downloaded[0].Order == 3 && downloaded[1].Order == 4, "Workshop entries append enabled with their item association");
            Check(downloaded.All(mod => File.ReadAllText(manager.GetFilePath(mod.Id)) == "workshop payload"),
                "downloaded pack bytes installed into game data");
            Check(UserScriptService.LoadOrCreate(appData).Mods.SequenceEqual(manager.List().Select(mod => mod.FileName)),
                "download automatically synchronizes the enabled script");
            Reject<InvalidOperationException>(() => manager.Download(42), "duplicate Workshop item rejected before a second transfer");
            Check(downloader.CallCount == 1, "duplicate download did not contact transport");

            manager.SetEnabled(importedId, false);
            Check(!manager.List().Single(mod => mod.Id == importedId).IsEnabled
                && !UserScriptService.LoadOrCreate(appData).Mods.Contains(imported.FileName), "disable persists state and removes only its script declaration");
            Check(File.Exists(manager.GetFilePath(importedId)), "disabling leaves the pack installed");
            manager.Move(secondWorkshopId, 0);
            Check(manager.List()[0].Id == secondWorkshopId
                && UserScriptService.LoadOrCreate(appData).Mods[0] == "MOD_42_Zebra.PACK", "move updates stored and enabled script order");
            manager.Move(importedId, 0);
            manager.SetEnabled(importedId, true);
            Check(UserScriptService.LoadOrCreate(appData).Mods[0] == "local 日本語.pack", "re-enable uses the mod's current list position");
            Check(manager.List().Select(mod => mod.Order).SequenceEqual(Enumerable.Range(0, manager.List().Count)),
                "stored order values stay contiguous after moves");
            Reject<ArgumentOutOfRangeException>(() => manager.Move(importedId, 999), "out-of-range move rejected");
            Reject<KeyNotFoundException>(() => manager.SetEnabled(Guid.NewGuid(), false), "unknown entry cannot be toggled");
            Reject<KeyNotFoundException>(() => manager.Delete(Guid.NewGuid()), "unknown entry cannot delete any file");

            using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            {
                JsonElement entry = json.RootElement.GetProperty("mods").EnumerateArray()
                    .Single(mod => mod.GetProperty("id").GetGuid() == firstWorkshopId);
                Check(entry.GetProperty("source").GetString() == "workshop"
                    && entry.GetProperty("workshopItemId").GetUInt64() == 42
                    && entry.GetProperty("isEnabled").GetBoolean(), "mod manifest contains source, Workshop ID, enable and order metadata");
            }
            Check(File.ReadAllBytes(scriptPath).AsSpan().StartsWith(Encoding.Unicode.GetPreamble()), "managed updates preserve UTF-16 LE BOM");
            Check(File.ReadAllText(scriptPath).Contains("\r\n") && !File.ReadAllText(scriptPath).Replace("\r\n", "").Contains('\n'),
                "managed updates preserve CRLF line endings");

            manager.Delete(firstWorkshopId);
            Check(!File.Exists(Path.Combine(data, "MOD_42_alpha.pack"))
                && manager.List().All(mod => mod.Id != firstWorkshopId)
                && !UserScriptService.LoadOrCreate(appData).Mods.Contains("MOD_42_alpha.pack"), "delete removes pack, manifest record and enabled script entry");
            File.Delete(manager.GetFilePath(localPrefix.Id));
            manager.Delete(localPrefix.Id);
            Check(manager.List().All(mod => mod.Id != localPrefix.Id), "delete handles an already missing managed file");

            // Force the script write to fail after the mod manifest has been saved.
            byte[] beforeManifest = File.ReadAllBytes(manifestPath);
            byte[] beforeScript = File.ReadAllBytes(scriptPath);
            string failingSource = Path.Combine(sources, "rollback.pack");
            File.WriteAllText(failingSource, "rollback payload");
            bool failed = false;
            using (var locked = new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { manager.Import(failingSource); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed = true; }
            }
            Check(failed, "locked script produces an operation failure");
            Check(beforeManifest.SequenceEqual(File.ReadAllBytes(manifestPath)) && beforeScript.SequenceEqual(File.ReadAllBytes(scriptPath))
                && !File.Exists(Path.Combine(data, "rollback.pack"))
                && manager.List().All(mod => mod.FileName != "rollback.pack"), "failed script write rolls back manifest, installed file and memory state");
            Check(!Directory.EnumerateFiles(data, ".tws2-*").Any(), "failed import leaves no manager staging files");

            failed = false;
            using (var locked = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { manager.Import(failingSource); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed = true; }
            }
            Check(failed && beforeManifest.SequenceEqual(File.ReadAllBytes(manifestPath))
                && beforeScript.SequenceEqual(File.ReadAllBytes(scriptPath)) && !File.Exists(Path.Combine(data, "rollback.pack")),
                "failed manifest write rolls back installed files without changing script");

            // Deletion uses a temporary rename until metadata and script writes succeed.
            string deleteTarget = manager.GetFilePath(secondWorkshopId);
            failed = false;
            using (var locked = new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { manager.Delete(secondWorkshopId); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed = true; }
            }
            Check(failed && File.Exists(deleteTarget) && manager.List().Any(mod => mod.Id == secondWorkshopId)
                && beforeManifest.SequenceEqual(File.ReadAllBytes(manifestPath)), "failed delete restores renamed pack and original mod manifest");

            if (OperatingSystem.IsWindows())
            {
                failed = false;
                File.SetAttributes(deleteTarget, FileAttributes.ReadOnly);
                try
                {
                    try { manager.Delete(secondWorkshopId); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed = true; }
                    Check(failed && File.Exists(deleteTarget) && beforeManifest.SequenceEqual(File.ReadAllBytes(manifestPath))
                        && beforeScript.SequenceEqual(File.ReadAllBytes(scriptPath))
                        && manager.List().Any(mod => mod.Id == secondWorkshopId),
                        "failure deleting a read-only pack restores file, manifest, script and memory");
                }
                finally
                {
                    if (File.Exists(deleteTarget)) File.SetAttributes(deleteTarget, FileAttributes.Normal);
                }
            }

            byte[] unchanged = File.ReadAllBytes(manifestPath);
            downloader.Mode = DownloadMode.Fail;
            Reject<IOException>(() => manager.Download(99), "transport failure reported");
            Check(unchanged.SequenceEqual(File.ReadAllBytes(manifestPath)) && beforeScript.SequenceEqual(File.ReadAllBytes(scriptPath)),
                "transport failure leaves manifest and script unchanged");
            downloader.Mode = DownloadMode.Empty;
            Reject<InvalidDataException>(() => manager.Download(99), "Workshop item without pack files rejected");
            downloader.Mode = DownloadMode.DuplicateNames;
            Reject<IOException>(() => manager.Download(99), "colliding filenames within a Workshop package rejected");
            Check(!File.Exists(Path.Combine(data, "MOD_99_alpha.pack")) && !Directory.EnumerateFiles(data, ".tws2-*").Any(),
                "multi-pack preflight failure installs no partial package");
            downloader.Mode = DownloadMode.OutsidePackage;
            Reject<InvalidDataException>(() => manager.Download(99), "transport cannot install a file outside its package root");
            downloader.Mode = DownloadMode.Normal;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Reject<OperationCanceledException>(() => manager.Import(failingSource, cancelled.Token), "cancelled import creates no managed entry");
                Reject<OperationCanceledException>(() => manager.Download(99, cancellationToken: cancelled.Token), "cancelled download does not start transport");
            }

            // Avoid silently replacing external edits while a manager is open.
            File.AppendAllText(scriptPath, "# external edit\r\n", Encoding.Unicode);
            Reject<ModStateConflictException>(() => manager.SetEnabled(importedId, false), "external script edits cause a conflict instead of overwrite");
            Check(File.ReadAllText(scriptPath).Contains("# external edit"), "external edit retained after conflict");
        }

        using (ModManager reopened = ModManager.Open(folders, manifestPath))
        {
            Check(reopened.List().Single(mod => mod.Id == importedId).IsEnabled && reopened.List()[0].Id == importedId,
                "enabled state, stable IDs and order survive reopening");
            Check(File.ReadAllText(scriptPath).Contains("# external edit"), "reopen retains external comments while resynchronizing mod declarations");
            byte[] persisted = File.ReadAllBytes(manifestPath);
            File.AppendAllText(manifestPath, "\n");
            Reject<ModStateConflictException>(() => reopened.Move(importedId, 1), "external mod-manifest edits cause a conflict instead of overwrite");
            File.WriteAllBytes(manifestPath, persisted);
            foreach (ManagedMod mod in reopened.List().ToArray()) reopened.SetEnabled(mod.Id, false);
            Check(UserScriptService.LoadOrCreate(appData).Mods.Count == 0 && reopened.List().Count > 0,
                "disabling every mod produces an empty enabled list while retaining records");
            Check(File.ReadAllText(scriptPath).Contains("other_setting true;"), "all-disabled script still retains unrelated settings");
            ManagedMod disabled = reopened.List()[0];
            reopened.Delete(disabled.Id);
            Check(reopened.List().All(mod => mod.Id != disabled.Id) && !File.Exists(Path.Combine(data, disabled.FileName)),
                "disabled mod can be deleted from disk and manifest");
        }
        var disposedManager = ModManager.Open(folders, manifestPath);
        disposedManager.Dispose();
        Reject<ObjectDisposedException>(() => disposedManager.List(), "disposed manager rejects operations");

        // Manifest validation must reject filenames that could escape data, duplicate
        // IDs/order, false Workshop associations, or the folder-settings JSON.
        string invalidManifest = Path.Combine(root, "invalid.json");
        File.WriteAllText(invalidManifest, "{\"schemaVersion\":1,\"appDataPath\":null,\"gameInstallPath\":null}");
        Reject<InvalidDataException>(() => ModManifestStore.Load(invalidManifest), "folder-settings manifest cannot be used as a mod manifest");
        ManagedMod valid = new() { Id = Guid.NewGuid(), FileName = "safe.pack", OriginalFileName = "safe.pack", Order = 0 };
        Reject<InvalidDataException>(() => ModManifestStore.Save(invalidManifest, new ModManifest { Mods = [valid with { FileName = "../outside.pack" }] }),
            "path-traversal mod filename rejected");
        Reject<InvalidDataException>(() => ModManifestStore.Save(invalidManifest, new ModManifest { Mods = [valid, valid with { Order = 1 }] }),
            "duplicate mod IDs/filenames rejected");
        Reject<InvalidDataException>(() => ModManifestStore.Save(invalidManifest, new ModManifest { Mods = [valid with { Order = 4 }] }),
            "invalid persisted order rejected");
        Reject<InvalidDataException>(() => ModManifestStore.Save(invalidManifest, new ModManifest { Mods = [valid with { WorkshopItemId = 42 }] }),
            "imported record cannot acquire a Workshop association");
        Reject<InvalidDataException>(() => ModManifestStore.Save(invalidManifest, new ModManifest
            { Mods = [valid with { Source = ModSource.Workshop, WorkshopItemId = 42 }] }),
            "Workshop record requires its naming convention");
        Check(File.ReadAllText(Path.Combine(data, "base_game.pack")) == "untouchable base pack", "all mod operations leave untracked base packs intact");

        // UTF-8 scripts, LF endings and malformed/unknown lines must also survive updates.
        string utfAppData = Path.Combine(root, "utf8-appdata");
        Directory.CreateDirectory(utfAppData);
        UserScriptDocument utfScript = UserScriptService.LoadOrCreate(utfAppData);
        File.WriteAllText(utfScript.FilePath, "setting true;\nmod old.pack; # comment\nmod broken.pack\n", new UTF8Encoding(false));
        UserScriptService.UpdateEnabledMods(utfAppData, new[] { "first.pack", "has space.pack" });
        byte[] utfBytes = File.ReadAllBytes(utfScript.FilePath);
        string utfText = File.ReadAllText(utfScript.FilePath);
        Check(!utfBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()) && !utfText.Contains('\r')
            && utfText.Contains("# comment") && utfText.Contains("mod broken.pack") && utfText.Contains("setting true;"),
            "direct script update preserves UTF-8/no BOM, LF, comments, malformed and unknown lines");
        Check(UserScriptService.Parse(utfText).Mods.SequenceEqual(new[] { "first.pack", "has space.pack" }),
            "direct script update writes precisely the requested enabled order");
        byte[] utfBefore = File.ReadAllBytes(utfScript.FilePath);
        Reject<ArgumentException>(() => UserScriptService.UpdateEnabledMods(utfAppData, new[] { "../escape.pack" }),
            "direct script writer rejects path injection");
        Check(utfBefore.SequenceEqual(File.ReadAllBytes(utfScript.FilePath)), "invalid script update preserves original bytes");
        UserScriptService.UpdateEnabledMods(utfAppData, new[] { " leading space.pack" });
        Check(UserScriptService.LoadOrCreate(utfAppData).Mods.SequenceEqual(new[] { " leading space.pack" }),
            "quoted filenames retain meaningful leading whitespace");
        return count;
    }

    private enum DownloadMode { Normal, Fail, Empty, DuplicateNames, OutsidePackage }

    private sealed class FakeDownloader(string root) : IWorkshopModDownloader
    {
        public DownloadMode Mode { get; set; }
        public int CallCount { get; private set; }

        public WorkshopModPackage Download(ulong itemId, string cacheDirectory,
            Action<string>? report = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Mode == DownloadMode.Fail) throw new IOException("Simulated Steam transfer failure.");
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            if (Mode == DownloadMode.Empty) return new WorkshopModPackage(directory, []);
            string alpha = Path.Combine(directory, "alpha.pack");
            string zebra = Path.Combine(directory, "Zebra.PACK");
            File.WriteAllText(alpha, "workshop payload");
            File.WriteAllText(zebra, "workshop payload");
            if (Mode == DownloadMode.OutsidePackage)
            {
                string outside = Path.Combine(root, "outside.pack");
                File.WriteAllText(outside, "outside");
                return new WorkshopModPackage(directory, [outside]);
            }
            if (Mode == DownloadMode.DuplicateNames)
            {
                string nested = Path.Combine(directory, "nested");
                Directory.CreateDirectory(nested);
                string duplicate = Path.Combine(nested, "alpha.pack");
                File.WriteAllText(duplicate, "different");
                return new WorkshopModPackage(directory, [alpha, duplicate]);
            }
            return new WorkshopModPackage(directory, [zebra, alpha]);
        }
    }
}
