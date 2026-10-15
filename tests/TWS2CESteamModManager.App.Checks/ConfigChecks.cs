using System.Globalization;
using System.IO;
using System.Text.Json;
using TWS2CESteamModManager.App.Localization;
using TWS2CESteamModManager.App.Services;
using TWS2CESteamModManager.Core;

internal static class ConfigChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++; Console.WriteLine("PASS " + message);
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { Check(true, message); return; }
            catch (IOException) { Check(true, message); return; }
            catch (UnauthorizedAccessException) { Check(true, message); return; }
            throw new InvalidOperationException(message);
        }

        string root = Path.GetFullPath(Path.Combine(".test-results", "config-" + Guid.NewGuid().ToString("N")));
        string emptyRoot = Path.Combine(root, "empty");
        var empty = new AppConfigStore(Path.Combine(emptyRoot, "config.json"));
        Check(empty.Load() == new AppConfig() && !Directory.Exists(emptyRoot), "reading missing config returns defaults without creating files");

        string migratedRoot = Path.Combine(root, "legacy"); Directory.CreateDirectory(migratedRoot);
        string path = Path.Combine(migratedRoot, "config.json");
        string oldFolders = Path.Combine(migratedRoot, "manifest.json"), oldLanguage = Path.Combine(migratedRoot, "settings.json"), oldNotice = Path.Combine(migratedRoot, "notice.json");
        var folders = new ManagerManifest { AppDataPath = Path.Combine(root, "app-data"), GameInstallPath = Path.Combine(root, "game") };
        ManifestStore.Save(oldFolders, folders);
        File.WriteAllText(oldLanguage, "{\"language\":\"ko\"}");
        File.WriteAllText(oldNotice, "{\"acceptedNoticeVersion\":1}");
        string modsPath = Path.Combine(migratedRoot, "mods.json");
        File.WriteAllText(modsPath, "{\"mods\":[]}");
        byte[] modsBefore = File.ReadAllBytes(modsPath);
        Directory.CreateDirectory(Path.Combine(migratedRoot, "Languages"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Languages", "ko.json"), Path.Combine(migratedRoot, "Languages", "ko.json"));
        var localized = new LocalizationService(migratedRoot, CultureInfo.GetCultureInfo("en-US"));
        var store = new AppConfigStore(path);
        Check(localized.ActiveLanguage.Id == "ko" && store.Load().GetFolders() == folders && !new NoticePreferencesStore(path).ShouldShow(),
            "startup migrates folders, language and remembered agreement together before displaying UI");
        Check(Directory.GetFiles(migratedRoot).Select(Path.GetFileName).Order().SequenceEqual(new[] { "config.json", "mods.json" }),
            "successful migration replaces the three legacy files with only config.json and mods.json");
        Check(modsBefore.SequenceEqual(File.ReadAllBytes(modsPath)), "configuration migration leaves mods.json byte-for-byte unchanged");

        localized.SaveSelection("en");
        Check(store.Load().Language == "en" && store.Load().GetFolders() == folders && store.Load().AcceptedNoticeVersion == 1,
            "saving language preserves folders and notice agreement");
        store.Update(current => current with { AcceptedNoticeVersion = null });
        new NoticePreferencesStore(path).RememberAgreement();
        Check(store.Load().Language == "en" && store.Load().GetFolders() == folders && store.Load().AcceptedNoticeVersion == 1,
            "remembering agreement preserves language and folders");
        store.Update(current => current with { AppDataPath = Path.Combine(root, "moved-app-data") });
        Check(store.Load().Language == "en" && store.Load().AcceptedNoticeVersion == 1 && store.Load().GameInstallPath == folders.GameInstallPath,
            "saving folder paths preserves other settings");

        File.WriteAllText(oldLanguage, "{broken");
        Check(store.Load().Language == "en", "an existing config takes priority over any leftover legacy files");
        File.Delete(oldLanguage);
        using (JsonDocument future = JsonDocument.Parse("{\"theme\":\"dark\"}"))
            store.Update(current => current with { AdditionalSettings = new() { ["futureSetting"] = future.RootElement.Clone() } });
        Parallel.Invoke(
            () => { for (int i = 0; i < 20; i++) new AppConfigStore(path).Update(current => current with { Language = "ko" }); },
            () => { for (int i = 0; i < 20; i++) new NoticePreferencesStore(path).RememberAgreement(); },
            () => { for (int i = 0; i < 20; i++) new AppConfigStore(path).Update(current => current with { GameInstallPath = Path.Combine(root, "moved-game") }); });
        AppConfig parallel = store.Load();
        Check(parallel.Language == "ko" && parallel.AcceptedNoticeVersion == 1 && parallel.GameInstallPath == Path.Combine(root, "moved-game"),
            "concurrent updates through independent stores retain all setting changes");
        Check(parallel.AdditionalSettings!["futureSetting"].GetProperty("theme").GetString() == "dark"
            && !Directory.GetFiles(migratedRoot).Any(file => file.EndsWith(".tmp")), "updates retain unknown settings and leave no temporary files");

        byte[] beforeInvalid = File.ReadAllBytes(path);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => store.Update(current => current with { Language = "en" }), "a locked config reports an atomic-save failure");
        Check(beforeInvalid.SequenceEqual(File.ReadAllBytes(path)) && !Directory.GetFiles(migratedRoot).Any(file => file.EndsWith(".tmp")),
            "failed config replacement preserves existing settings and removes its temporary file");
        Reject(() => store.Update(current => current with { SchemaVersion = 99 }), "unsupported config schemas are rejected on save");
        Reject(() => store.Update(current => current with { GameInstallPath = "relative" }), "relative folder paths are rejected on save");
        Check(beforeInvalid.SequenceEqual(File.ReadAllBytes(path)), "invalid updates preserve the saved config");
        File.WriteAllText(path, "{broken");
        Reject(() => new NoticePreferencesStore(path).RememberAgreement(), "saving agreement cannot overwrite a corrupt shared config");
        Check(new NoticePreferencesStore(path).ShouldShow() && File.ReadAllText(path) == "{broken", "corrupt config shows the notice without silently replacing settings");
        File.WriteAllText(path, "{\"schemaVersion\":99}");
        Reject(() => store.Load(), "unsupported loaded config schemas are rejected");

        string badRoot = Path.Combine(root, "bad-legacy"); Directory.CreateDirectory(badRoot);
        ManifestStore.Save(Path.Combine(badRoot, "manifest.json"), folders);
        File.WriteAllText(Path.Combine(badRoot, "settings.json"), "{broken");
        File.WriteAllText(Path.Combine(badRoot, "notice.json"), "{\"acceptedNoticeVersion\":1}");
        Reject(() => new AppConfigStore(Path.Combine(badRoot, "config.json")).Load(), "malformed legacy data blocks partial migration");
        Check(!File.Exists(Path.Combine(badRoot, "config.json")) && Directory.GetFiles(badRoot).Length == 3,
            "failed migration preserves every legacy source and creates no partial config");

        string lockedRoot = Path.Combine(root, "locked-legacy"); Directory.CreateDirectory(lockedRoot);
        ManifestStore.Save(Path.Combine(lockedRoot, "manifest.json"), folders);
        string lockedNotice = Path.Combine(lockedRoot, "notice.json");
        File.WriteAllText(lockedNotice, "{\"acceptedNoticeVersion\":1}");
        using (File.Open(lockedNotice, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject(() => new AppConfigStore(Path.Combine(lockedRoot, "config.json")).Load(), "unreadable legacy data cannot be discarded during migration");
        Check(!File.Exists(Path.Combine(lockedRoot, "config.json")) && Directory.GetFiles(lockedRoot).Length == 2,
            "a migration read failure leaves all source files intact");

        string foldersOnly = Path.Combine(root, "folders-only");
        Directory.CreateDirectory(Path.Combine(foldersOnly, "Languages"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Languages", "ko.json"), Path.Combine(foldersOnly, "Languages", "ko.json"));
        new AppConfigStore(Path.Combine(foldersOnly, "config.json")).Update(current => current with { AppDataPath = folders.AppDataPath });
        Check(new LocalizationService(foldersOnly, CultureInfo.GetCultureInfo("ko-KR")).ActiveLanguage.Id == "ko",
            "config with no explicit language still detects the Windows UI language");
        return checks;
    }
}
