using TWS2CESteamModManager.Core;

internal static class DisabledModChecks
{
    public static int Run(string parent)
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++; Console.WriteLine("PASS " + message);
        }
        void Reject<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { Check(true, message); return; }
            throw new InvalidOperationException(message);
        }
        string root = Path.Combine(parent, "disabled-mods");
        string game = Path.Combine(root, "game"), data = Path.Combine(game, "data"), appData = Path.Combine(root, "app-data");
        string disabled = Path.Combine(data, "disabled_mods"), manifest = Path.Combine(root, "mods.json");
        Directory.CreateDirectory(data); Directory.CreateDirectory(appData);
        File.WriteAllText(Path.Combine(game, GameConstants.GameExecutable), "fixture");
        var folders = new ManagerManifest { AppDataPath = appData, GameInstallPath = game };
        var local = new ManagedMod { Id = Guid.NewGuid(), FileName = "legacy.pack", OriginalFileName = "legacy.pack", IsEnabled = false };
        string activeLocal = Path.Combine(data, local.FileName), disabledLocal = Path.Combine(disabled, local.FileName);
        File.WriteAllText(activeLocal, "original local bytes");
        File.WriteAllText(Path.ChangeExtension(activeLocal, ".png"), "thumbnail");
        ModManifestStore.Save(manifest, new ModManifest { Mods = [local] });
        var transport = new Transport(Path.Combine(root, "packages"));
        using (ModManager manager = ModManager.Open(folders, manifest, transport))
        {
            string script = GameConstants.GetUserScriptPath(appData);
            Check(!File.Exists(activeLocal) && File.ReadAllText(disabledLocal) == "original local bytes",
                "opening an older manifest relocates disabled packs so auto-loading cannot see them in data");
            Check(manager.List().Single().Id == local.Id && manager.GetFilePath(local.Id) == disabledLocal
                && manager.GetImagePath(local.Id) == Path.ChangeExtension(activeLocal, ".png"),
                "disabled mods keep stable metadata, present-file paths and usable thumbnail images");
            manager.SetEnabled(local.Id, true);
            Check(File.Exists(activeLocal) && !File.Exists(disabledLocal) && transport.Calls == 0,
                "enabling an imported mod moves its stored pack back without contacting a downloader");
            Check(UserScriptService.LoadOrCreate(appData).Mods.SequenceEqual(new[] { local.FileName }),
                "restored packs return to the enabled script at their saved position");

            byte[] beforeManifest = File.ReadAllBytes(manifest), beforeScript = File.ReadAllBytes(script);
            using (File.Open(script, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => manager.SetEnabled(local.Id, false), "a script-write failure rejects disabling a mod");
            Check(File.Exists(activeLocal) && !File.Exists(disabledLocal) && manager.List()[0].IsEnabled
                && beforeManifest.SequenceEqual(File.ReadAllBytes(manifest)) && beforeScript.SequenceEqual(File.ReadAllBytes(script)),
                "failed disable rolls back the pack move, manifest, script and in-memory flag");

            using (File.Open(activeLocal, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => manager.SetEnabled(local.Id, false), "a pack locked against moving cannot be disabled silently");
            Check(File.Exists(activeLocal) && manager.List()[0].IsEnabled, "a failed pack move leaves the mod enabled and present");

            manager.SetEnabled(local.Id, false);
            beforeManifest = File.ReadAllBytes(manifest);
            using (File.Open(script, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => manager.SetEnabled(local.Id, true), "a script-write failure rejects enabling a stored mod");
            Check(!File.Exists(activeLocal) && File.Exists(disabledLocal) && !manager.List()[0].IsEnabled
                && beforeManifest.SequenceEqual(File.ReadAllBytes(manifest)), "failed enable rolls the pack back into disabled_mods");

            File.WriteAllText(activeLocal, "conflicting copy");
            Reject<IOException>(() => manager.SetEnabled(local.Id, true), "duplicate packs in active and disabled storage cause a conflict");
            Check(File.ReadAllText(activeLocal) == "conflicting copy" && File.ReadAllText(disabledLocal) == "original local bytes"
                && !manager.List()[0].IsEnabled, "storage conflicts overwrite neither pack nor the enabled flag");
            File.Delete(activeLocal);
            File.Move(disabledLocal, activeLocal);
            manager.SetEnabled(local.Id, false);
            Check(File.Exists(disabledLocal) && !File.Exists(activeLocal), "reapplying disabled state repairs a pack moved back externally");

            File.Delete(disabledLocal);
            Reject<FileNotFoundException>(() => manager.SetEnabled(local.Id, true), "a missing imported pack cannot be enabled or downloaded");
            Check(!manager.List()[0].IsEnabled && transport.Calls == 0, "missing imported mods stay disabled without any network recovery attempt");
            File.WriteAllText(disabledLocal, "manually restored");
            manager.SetEnabled(local.Id, true);
            Check(File.ReadAllText(activeLocal) == "manually restored", "a manually restored imported pack can be enabled again");

            var downloaded = manager.Download(42);
            ManagedMod first = downloaded[0], second = downloaded[1];
            string activeFirst = manager.GetFilePath(first.Id), activeSecond = manager.GetFilePath(second.Id);
            manager.SetEnabled(first.Id, false);
            string storedFirst = manager.GetFilePath(first.Id);
            Check(!File.Exists(activeFirst) && File.Exists(storedFirst) && File.Exists(activeSecond),
                "disabling one pack in a Workshop item leaves its other managed packs unchanged");
            manager.SetEnabled(first.Id, true);
            Check(transport.Calls == 1 && File.Exists(activeFirst), "a stored Workshop pack is restored locally without downloading again");
            manager.SetEnabled(first.Id, false);
            File.Delete(manager.GetFilePath(first.Id));
            Reject<WorkshopModRestoreRequiredException>(() => manager.SetEnabled(first.Id, true, allowDownload: false),
                "local-only enable reports when recovery must be dispatched to the Steam thread");
            Check(transport.Calls == 1 && !manager.List().Single(mod => mod.Id == first.Id).IsEnabled,
                "local-only recovery never calls native Steam download code");

            File.WriteAllText(activeSecond, "keep sibling bytes");
            manager.Move(first.Id, 0);
            manager.SetEnabled(first.Id, true);
            ManagedMod recovered = manager.List().Single(mod => mod.Id == first.Id);
            Check(transport.Calls == 2 && File.ReadAllText(activeFirst) == "alpha payload" && recovered.IsEnabled
                && recovered.Order == 0 && recovered.DisplayName == first.DisplayName && recovered.FileName == first.FileName,
                "missing Workshop packs re-download with stable IDs, names, order and filenames");
            Check(File.ReadAllText(activeSecond) == "keep sibling bytes" && manager.List().Count == 3
                && UserScriptService.LoadOrCreate(appData).Mods[0] == first.FileName,
                "Workshop recovery neither duplicates entries nor overwrites sibling packs");

            manager.SetEnabled(first.Id, false);
            File.Delete(manager.GetFilePath(first.Id));
            beforeManifest = File.ReadAllBytes(manifest); beforeScript = File.ReadAllBytes(script);
            transport.Fail = true;
            Reject<IOException>(() => manager.SetEnabled(first.Id, true), "failed recovery download reports its transport error");
            transport.Fail = false; transport.Rename = true;
            Reject<InvalidDataException>(() => manager.SetEnabled(first.Id, true), "a renamed or removed original Workshop pack is not replaced with a different pack");
            transport.Rename = false;
            using (var cancellation = new CancellationTokenSource())
            {
                transport.AfterDownload = cancellation.Cancel;
                Reject<OperationCanceledException>(() => manager.SetEnabled(first.Id, true, cancellationToken: cancellation.Token),
                    "cancelled Workshop recovery does not enable or install a partial pack");
                transport.AfterDownload = null;
            }
            Check(beforeManifest.SequenceEqual(File.ReadAllBytes(manifest)) && beforeScript.SequenceEqual(File.ReadAllBytes(script))
                && !File.Exists(activeFirst) && !manager.List().Single(mod => mod.Id == first.Id).IsEnabled
                && !Directory.EnumerateFiles(data, ".tws2-*").Any(), "failed recovery preserves disabled state, script and manifest with no staging debris");
            using (File.Open(script, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => manager.SetEnabled(first.Id, true), "recovery rolls back when the final script write fails");
            Check(!File.Exists(activeFirst) && beforeManifest.SequenceEqual(File.ReadAllBytes(manifest)),
                "failed recovery commit removes the downloaded pack and restores the saved flag");

            manager.SetEnabled(first.Id, true);
            manager.SetEnabled(first.Id, false);
            string image = manager.GetImagePath(first.Id)!;
            manager.Delete(first.Id);
            Check(!File.Exists(storedFirst) && !File.Exists(activeFirst) && !File.Exists(image)
                && manager.List().All(mod => mod.Id != first.Id), "deleting a disabled Workshop mod removes its stored pack, image and manifest entry");
            string untracked = Path.Combine(disabled, "untracked.pack"); File.WriteAllText(untracked, "local bytes");
            ManagedMod imported = manager.Import(untracked);
            Check(File.Exists(manager.GetFilePath(imported.Id)) && !File.Exists(untracked) && imported.WorkshopItemId is null,
                "importing an untracked pack from disabled_mods restores it as a local enabled mod");
            manager.SetEnabled(imported.Id, false);
            string importedDisabled = manager.GetFilePath(imported.Id);
            manager.Delete(imported.Id);
            Check(!File.Exists(importedDisabled), "deleting an imported disabled mod removes its pack from disabled storage");
            manager.SetEnabled(second.Id, false);
            File.Delete(manager.GetFilePath(second.Id));
        }
        using (ModManager withoutDownloader = ModManager.Open(folders, manifest))
            Reject<InvalidOperationException>(() => withoutDownloader.SetEnabled(withoutDownloader.List().Single(mod => mod.WorkshopItemId == 42).Id, true),
                "missing Workshop recovery requires a configured downloader without discarding the entry");
        File.WriteAllText(activeLocal, "existing local bytes");
        ModManifest saved = ModManifestStore.Load(manifest)!;
        ModManifestStore.Save(manifest, saved with { Mods = saved.Mods.Select(mod => mod.Id == local.Id ? mod with { IsEnabled = false } : mod).ToArray() });
        string scriptPath = GameConstants.GetUserScriptPath(appData);
        byte[] beforeOpen = File.ReadAllBytes(manifest), scriptBeforeOpen = File.ReadAllBytes(scriptPath);
        using (File.Open(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject<IOException>(() => { using var ignored = ModManager.Open(folders, manifest); }, "failed startup migration rolls back physical pack relocation");
        Check(File.Exists(activeLocal) && !File.Exists(disabledLocal) && beforeOpen.SequenceEqual(File.ReadAllBytes(manifest))
            && scriptBeforeOpen.SequenceEqual(File.ReadAllBytes(scriptPath)), "startup migration failures preserve original pack locations and metadata");
        using (ModManager migrated = ModManager.Open(folders, manifest))
            Check(File.Exists(disabledLocal) && !File.Exists(activeLocal), "reopening retries physical disabled-pack migration successfully");
        return count;
    }

    private sealed class Transport(string root) : IWorkshopModDownloader
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public bool Rename { get; set; }
        public Action? AfterDownload { get; set; }
        public WorkshopModPackage Download(ulong itemId, string cacheDirectory, Action<string>? report = null, CancellationToken cancellationToken = default)
        {
            Calls++; cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("Simulated recovery failure");
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string a = Path.Combine(directory, Rename ? "renamed.pack" : "alpha.pack"), b = Path.Combine(directory, "beta.pack"), image = Path.Combine(directory, "preview.png");
            File.WriteAllText(a, "alpha payload"); File.WriteAllText(b, "beta payload"); File.WriteAllText(image, "thumbnail");
            AfterDownload?.Invoke();
            return new WorkshopModPackage(directory, [a, b], image, "Workshop title");
        }
    }
}
