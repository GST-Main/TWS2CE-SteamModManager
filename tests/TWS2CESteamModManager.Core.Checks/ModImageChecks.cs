using TWS2CESteamModManager.Core;

internal static class ModImageChecks
{
    public static int Run(string parent)
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            count++;
            Console.WriteLine("PASS " + message);
        }
        void Reject<T>(Action action, string message) where T : Exception
        {
            bool rejected = false;
            try { action(); } catch (T) { rejected = true; }
            Check(rejected, message);
        }
        string root = Path.Combine(parent, "mod-images");
        string game = Path.Combine(root, "game"), data = Path.Combine(game, "data"), appData = Path.Combine(root, "appdata");
        string sources = Path.Combine(root, "sources");
        Directory.CreateDirectory(data); Directory.CreateDirectory(appData); Directory.CreateDirectory(sources);
        File.WriteAllText(Path.Combine(game, GameConstants.GameExecutable), "fixture");
        string manifestPath = Path.Combine(root, "mods.json");
        var folders = new ManagerManifest { AppDataPath = appData, GameInstallPath = game };
        var transport = new ImageDownloader(Path.Combine(root, "package"));
        using var manager = ModManager.Open(folders, manifestPath, transport);
        string pack = Path.Combine(sources, "manual.pack");
        string image = Path.Combine(sources, "manual.PNG");
        File.WriteAllText(pack, "manual payload"); File.WriteAllText(image, "image bytes");
        ManagedMod imported = manager.Import(pack);
        Check(manager.GetImagePath(imported.Id) == Path.Combine(data, "manual.png"), "manual import discovers and copies a same-name image");
        Check(File.ReadAllText(manager.GetImagePath(imported.Id)!) == "image bytes" && File.Exists(image), "import preserves source image bytes");
        Check(imported.WorkshopItemId is null, "manual images do not imply Workshop association");
        string selectedPack = Path.Combine(sources, "selected.pack"), selectedImage = Path.Combine(sources, "different.jpg");
        File.WriteAllText(selectedPack, "selected payload"); File.WriteAllText(selectedImage, "selected image");
        ManagedMod selected = manager.Import(selectedPack, selectedImage);
        Check(Path.GetFileName(manager.GetImagePath(selected.Id)) == "selected.jpg", "explicit image selection adopts the pack base filename");
        string inPlace = Path.Combine(data, "existing.pack");
        File.WriteAllText(inPlace, "existing"); File.WriteAllText(Path.ChangeExtension(inPlace, ".gif"), "existing image");
        ManagedMod existing = manager.Import(inPlace);
        Check(manager.GetImagePath(existing.Id) == Path.ChangeExtension(inPlace, ".gif"), "in-place import retains its sidecar image");
        IReadOnlyList<ManagedMod> downloaded = manager.Download(123);
        Check(downloaded.Count == 2 && downloaded.All(mod => Path.GetFileName(manager.GetImagePath(mod.Id)) == Path.ChangeExtension(mod.FileName, ".jpg")),
            "Workshop thumbnail is installed beside each renamed pack");
        Check(downloaded.All(mod => File.ReadAllText(manager.GetImagePath(mod.Id)!) == "Workshop image"), "Workshop sidecars retain thumbnail bytes");
        Check(UserScriptService.LoadOrCreate(appData).Mods.All(name => name.EndsWith(".pack")), "image sidecars never enter user.script.txt");
        manager.SetEnabled(downloaded[0].Id, false);
        Check(File.Exists(manager.GetImagePath(downloaded[0].Id)), "disabling a mod retains its image");

        string rollbackPack = Path.Combine(sources, "rollback.pack");
        File.WriteAllText(rollbackPack, "rollback"); File.WriteAllText(Path.ChangeExtension(rollbackPack, ".png"), "rollback image");
        using (var locked = new FileStream(GameConstants.GetUserScriptPath(appData), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Reject<IOException>(() => manager.Import(rollbackPack), "script failure rejects pack and image import");
        Check(!File.Exists(Path.Combine(data, "rollback.pack")) && !File.Exists(Path.Combine(data, "rollback.png")), "failed import rolls back both pack and image");
        string collisionPack = Path.Combine(sources, "collision.pack");
        File.WriteAllText(collisionPack, "collision"); File.WriteAllText(Path.ChangeExtension(collisionPack, ".jpg"), "new image");
        File.WriteAllText(Path.Combine(data, "collision.jpg"), "keep image");
        Reject<IOException>(() => manager.Import(collisionPack), "image destination collision is rejected");
        Check(!File.Exists(Path.Combine(data, "collision.pack")) && File.ReadAllText(Path.Combine(data, "collision.jpg")) == "keep image", "image collision preserves existing files");
        transport.OutsideImage = true;
        Reject<InvalidDataException>(() => manager.Download(456), "downloader image cannot escape its package");
        Check(manager.List().All(mod => mod.WorkshopItemId != 456), "invalid package image installs no mod");

        string managedPack = manager.GetFilePath(selected.Id), managedImage = manager.GetImagePath(selected.Id)!;
        string extraImage = Path.ChangeExtension(managedPack, ".png");
        File.WriteAllText(extraImage, "second sidecar");
        byte[] beforeManifest = File.ReadAllBytes(manifestPath), beforeScript = File.ReadAllBytes(GameConstants.GetUserScriptPath(appData));
        File.SetAttributes(managedImage, FileAttributes.ReadOnly);
        try { Reject<UnauthorizedAccessException>(() => manager.Delete(selected.Id), "read-only sidecar causes a recoverable deletion failure"); }
        finally { File.SetAttributes(managedImage, FileAttributes.Normal); }
        Check(File.ReadAllText(managedPack) == "selected payload" && File.ReadAllText(managedImage) == "selected image", "sidecar failure restores pack and image");
        Check(File.ReadAllText(extraImage) == "second sidecar", "later sidecar failure restores an image already deleted during cleanup");
        Check(beforeManifest.SequenceEqual(File.ReadAllBytes(manifestPath)) && beforeScript.SequenceEqual(File.ReadAllBytes(GameConstants.GetUserScriptPath(appData))), "sidecar deletion failure restores manifest and script");
        manager.Delete(selected.Id);
        Check(!File.Exists(managedPack) && !File.Exists(managedImage) && !File.Exists(extraImage), "delete removes pack and its images together");
        Check(!Directory.EnumerateFiles(data, ".tws2-*").Any(), "image operations leave no transaction staging files");
        string importedImage = manager.GetImagePath(imported.Id)!;
        File.Delete(importedImage);
        Check(manager.GetImagePath(imported.Id) is null, "missing optional image resolves to null");
        return count;
    }

    private sealed class ImageDownloader(string root) : IWorkshopModDownloader
    {
        public bool OutsideImage { get; set; }
        public WorkshopModPackage Download(ulong itemId, string cacheDirectory, Action<string>? report = null, CancellationToken cancellationToken = default)
        {
            string directory = Path.Combine(root, itemId.ToString()); Directory.CreateDirectory(directory);
            string first = Path.Combine(directory, "first.pack"), second = Path.Combine(directory, "second.pack");
            string image = OutsideImage ? Path.Combine(root, "outside.jpg") : Path.Combine(directory, "preview.jpg");
            File.WriteAllText(first, "first payload"); File.WriteAllText(second, "second payload"); File.WriteAllText(image, "Workshop image");
            return new WorkshopModPackage(directory, new[] { first, second }, image);
        }
    }
}
