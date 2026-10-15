using System.Text.Json.Nodes;
using TWS2CESteamModManager.Core;

internal static class ModNameChecks
{
    public static int Run(string parent)
    {
        int count = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            count++; Console.WriteLine("PASS " + message);
        }
        string root = Path.Combine(parent, "mod-names"), game = Path.Combine(root, "game"), appData = Path.Combine(root, "appdata");
        string data = Path.Combine(game, "data"), source = Path.Combine(root, "imports");
        Directory.CreateDirectory(data); Directory.CreateDirectory(appData); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(game, GameConstants.GameExecutable), "fixture");
        string path = Path.Combine(root, "mods.json");
        var folders = new ManagerManifest { AppDataPath = appData, GameInstallPath = game };
        var oldImport = new ManagedMod { Id = Guid.NewGuid(), FileName = "Old 日本語.pack", OriginalFileName = "Old 日本語.pack", Order = 0 };
        var oldWorkshop = new ManagedMod { Id = Guid.NewGuid(), FileName = "MOD_42_old.pack", OriginalFileName = "old.pack", Order = 1,
            Source = ModSource.Workshop, WorkshopItemId = 42 };
        ModManifestStore.Save(path, new ModManifest { Mods = new[] { oldImport, oldWorkshop } });
        JsonNode json = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (JsonNode? mod in json["mods"]!.AsArray()) mod!.AsObject().Remove("displayName");
        File.WriteAllText(path, json.ToJsonString());
        Check(ModManifestStore.Load(path)!.Mods.All(mod => mod.DisplayName is null), "older manifests without displayName remain readable");
        var downloader = new NamedDownloader(Path.Combine(root, "packages"));
        using (var manager = ModManager.Open(folders, path, downloader))
        {
            Check(manager.List()[0].DisplayName == "Old 日本語", "older imported entries acquire a friendly filename stem");
            Check(manager.List()[1].DisplayName is null, "older Workshop entries keep the name missing until metadata is available");
            string script = GameConstants.GetUserScriptPath(appData);
            byte[] scriptBefore = File.ReadAllBytes(script);
            Check(manager.SetWorkshopDisplayNames(new Dictionary<ulong, string> { [42] = "  Historical campaign 日本語  " }), "Workshop names can be filled by item ID");
            Check(manager.List()[1].DisplayName == "Historical campaign 日本語", "backfilled names are trimmed and persisted");
            Check(scriptBefore.SequenceEqual(File.ReadAllBytes(script)), "display-name updates never change script bytes");
            Check(!manager.SetWorkshopDisplayNames(new Dictionary<ulong, string> { [42] = "Changed upstream title" })
                && manager.List()[1].DisplayName == "Historical campaign 日本語", "metadata refresh preserves an existing saved name");
            string import = Path.Combine(source, "Manual nice name.pack"); File.WriteAllText(import, "manual bytes");
            ManagedMod imported = manager.Import(import);
            Check(imported.DisplayName == "Manual nice name" && imported.FileName == "Manual nice name.pack", "new imports store a name independently of the pack filename");
            IReadOnlyList<ManagedMod> installed = manager.Download(100);
            Check(installed.Count == 2 && installed.All(mod => mod.DisplayName == "New Workshop mod 日本語"), "every pack in a Workshop package retains its item title");
            Check(installed.Select(mod => mod.FileName).SequenceEqual(new[] { "MOD_100_alpha.pack", "MOD_100_beta.pack" }), "titles do not affect installed filenames");
            manager.Move(installed[1].Id, 0);
            manager.SetEnabled(installed[0].Id, false);
            Check(manager.List().Where(mod => mod.WorkshopItemId == 100).All(mod => mod.DisplayName == "New Workshop mod 日本語"), "names survive order and enable changes");
            Check(UserScriptService.LoadOrCreate(appData).Mods.All(name => name.EndsWith(".pack"))
                && !File.ReadAllText(script).Contains("New Workshop mod"), "user.script.txt always uses filenames instead of titles");
        }
        using (var reopened = ModManager.Open(folders, path))
            Check(reopened.List().Any(mod => mod.DisplayName == "New Workshop mod 日本語"), "display names survive reopening");
        Check(File.ReadAllText(path).Contains("\"displayName\""), "mod JSON writes the displayName field");
        return count;
    }

    private sealed class NamedDownloader(string root) : IWorkshopModDownloader
    {
        public WorkshopModPackage Download(ulong itemId, string cacheDirectory, Action<string>? report = null, CancellationToken cancellationToken = default)
        {
            string directory = Path.Combine(root, itemId.ToString()); Directory.CreateDirectory(directory);
            string first = Path.Combine(directory, "alpha.pack"), second = Path.Combine(directory, "beta.pack");
            File.WriteAllText(first, "alpha"); File.WriteAllText(second, "beta");
            return new WorkshopModPackage(directory, new[] { first, second }, Title: "New Workshop mod 日本語");
        }
    }
}
