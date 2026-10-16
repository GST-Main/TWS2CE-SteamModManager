using System.Text;
using System.Text.Json;
using TWS2CESteamModManager.Core;

string testRoot = Path.GetFullPath(Path.Combine(".test-results", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(testRoot);
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

string Folder(string relative)
{
    string path = Path.Combine(testRoot, relative);
    Directory.CreateDirectory(path);
    return path;
}

string Game(string parent, string name)
{
    string path = Path.Combine(parent, "steamapps", "common", name);
    Directory.CreateDirectory(Path.Combine(path, "data"));
    File.WriteAllText(Path.Combine(path, GameConstants.GameExecutable), "test fixture");
    return path;
}

static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
static void AppManifest(string library, string directoryName, uint appId = GameConstants.GameAppId)
{
    Directory.CreateDirectory(Path.Combine(library, "steamapps"));
    File.WriteAllText(Path.Combine(library, "steamapps", $"appmanifest_{GameConstants.GameAppId}.acf"),
        $"\"AppState\" {{ \"appid\" \"{appId}\" \"installdir\" {Quote(directoryName)} }}");
}

// Persistence: caller-selected path, safe replacement, malformed input, schema fields.
string manifestPath = Path.Combine(testRoot, "portable", "manifest.json");
Check(ManifestStore.Load(manifestPath) is null, "missing manifest returns null without creating directories");
Check(!Directory.Exists(Path.GetDirectoryName(manifestPath)), "load has no creation side effects");
var manifest = new ManagerManifest { AppDataPath = Folder("日本語 app data"), GameInstallPath = Path.Combine(testRoot, "unplugged drive") };
ManifestStore.Save(manifestPath, manifest);
Check(ManifestStore.Load(manifestPath) == manifest, "manifest round-trip retains Unicode and currently missing folder paths");
using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(manifestPath)))
{
    Check(json.RootElement.EnumerateObject().Select(property => property.Name)
        .Order().SequenceEqual(new[] { "appDataPath", "gameInstallPath", "schemaVersion" }.Order()),
        "manifest only contains schemaVersion and the two paths");
}
var updated = manifest with { GameInstallPath = null };
ManifestStore.Save(manifestPath, updated);
Check(ManifestStore.Load(manifestPath) == updated, "saving replaces an existing manifest");
byte[] originalManifest = File.ReadAllBytes(manifestPath);
Reject<ArgumentException>(() => ManifestStore.Save(manifestPath, updated with { AppDataPath = "relative/path" }),
    "relative folder paths rejected before replacing saved manifest");
Check(originalManifest.SequenceEqual(File.ReadAllBytes(manifestPath)), "invalid save preserves previous manifest bytes");
Reject<InvalidDataException>(() => ManifestStore.Save(manifestPath, updated with { SchemaVersion = 99 }),
    "unsupported schema cannot be saved");
Check(!Directory.EnumerateFiles(Path.GetDirectoryName(manifestPath)!, "*.tmp").Any(), "save leaves no temporary files");
string destinationDirectory = Folder("manifest-destination-is-directory");
File.WriteAllText(Path.Combine(destinationDirectory, "keep.txt"), "preserve");
Reject<IOException>(() => ManifestStore.Save(destinationDirectory, manifest), "manifest save reports a directory/file conflict");
Check(File.ReadAllText(Path.Combine(destinationDirectory, "keep.txt")) == "preserve",
    "failed manifest save preserves the conflicting directory's contents");
Check(!Directory.EnumerateFiles(testRoot, "*.tmp").Any(), "failed save cleans its own temporary file");
string corruptPath = Path.Combine(testRoot, "broken.json");
File.WriteAllText(corruptPath, "{broken");
Reject<InvalidDataException>(() => ManifestStore.Load(corruptPath), "corrupt JSON reported without repair");
Check(File.ReadAllText(corruptPath) == "{broken", "corrupt manifest remains untouched");
File.WriteAllText(corruptPath, "{\"schemaVersion\":99}");
Reject<InvalidDataException>(() => ManifestStore.Load(corruptPath), "unsupported loaded schema rejected");
File.WriteAllText(corruptPath, "null");
Reject<InvalidDataException>(() => ManifestStore.Load(corruptPath), "JSON null is not a valid manifest");

// Script parsing: ordered duplicate filenames, quotes, comments, paths and broken declarations.
UserScriptParseResult parsed = UserScriptService.Parse("""
// mod disabled.pack;
# comment
mod Z.pack;
mod "日本語 name.pack"; // note
  MOD A.PACK ;
mod Z.pack;
mod missing_semicolon.pack
mod ../escape.pack;
mod bad.txt;
mod "quoted.pack" garbage;
other_game_setting true;
""");
Check(parsed.Mods.SequenceEqual(new[] { "Z.pack", "日本語 name.pack", "A.PACK", "Z.pack" }),
    "script preserves filename case, Unicode, spaces, order and duplicate entries");
Check(parsed.Diagnostics.Count == 4 && parsed.Diagnostics[0].LineNumber == 7,
    "malformed mod declarations report their source line numbers");
string appData = Folder("script-appdata");
UserScriptDocument script = UserScriptService.LoadOrCreate(appData);
Check(script.WasCreated && script.Mods.Count == 0 && script.HasByteOrderMark && script.Encoding.CodePage == 1200,
    "missing script and parent directories created as empty UTF-16 LE with BOM");
Check(File.ReadAllBytes(script.FilePath).SequenceEqual(Encoding.Unicode.GetPreamble()), "new script contains only the encoding preamble");
Check(!UserScriptService.LoadOrCreate(appData).WasCreated, "subsequent script load does not recreate it");
foreach (Encoding encoding in new Encoding[] { Encoding.Unicode, Encoding.BigEndianUnicode, new UTF8Encoding(true), new UTF8Encoding(false) })
{
    string text = "mod 日本語.pack;\r\nother_setting true;\r\n";
    File.WriteAllText(script.FilePath, text, encoding);
    byte[] before = File.ReadAllBytes(script.FilePath);
    UserScriptDocument loaded = UserScriptService.LoadOrCreate(appData);
    Check(loaded.Mods.SequenceEqual(new[] { "日本語.pack" }) && loaded.Content == text
        && loaded.Encoding.CodePage == encoding.CodePage && before.SequenceEqual(File.ReadAllBytes(script.FilePath)),
        $"existing script read without rewriting ({encoding.WebName}, BOM={encoding.GetPreamble().Length > 0})");
}
string absentAppData = Path.Combine(testRoot, "must-not-create-appdata");
Reject<DirectoryNotFoundException>(() => UserScriptService.LoadOrCreate(absentAppData), "missing app-data root requires selection");
Check(!Directory.Exists(absentAppData), "missing game app-data root is not fabricated");

// Location states and manually selected folder precedence.
string roaming = Folder("roaming");
string defaultAppData = GameConstants.GetDefaultAppDataPath(roaming);
var noDetection = new GameLocationService(roaming, Array.Empty<IGameInstallationLocator>());
Check(noDetection.Resolve().Status == SetupStatus.NeedsBothFolders, "both missing folders reported");
Check(!Directory.Exists(defaultAppData), "location detection does not create app-data folders");
Directory.CreateDirectory(defaultAppData);
Check(noDetection.Resolve().Status == SetupStatus.NeedsGameFolder, "existing default app data detected");
string manualGame = Game(Folder("manual-library"), "Renamed Complete Edition");
ManagerManifest manual = GameLocationService.SelectGameInstallFolder(new ManagerManifest(), manualGame);
Check(noDetection.Resolve(manual).Status == SetupStatus.Ready, "manual installation selection plus default app data is ready");
ManagerManifest manualApp = GameLocationService.SelectAppDataFolder(manual, appData);
Check(noDetection.Resolve(manualApp).Manifest.AppDataPath == appData, "saved manual app-data selection takes precedence");
var missingRoaming = new GameLocationService(Path.Combine(testRoot, "missing-roaming"), Array.Empty<IGameInstallationLocator>());
Check(missingRoaming.Resolve(manual).Status == SetupStatus.NeedsAppDataFolder, "only missing app-data folder reported");
Check(noDetection.Resolve(manual with { AppDataPath = absentAppData }).Manifest.AppDataPath == defaultAppData,
    "stale saved app data falls back to the default existing folder");
Reject<ArgumentException>(() => GameLocationService.SelectAppDataFolder(manual, absentAppData), "invalid manual app-data selection rejected");
Reject<ArgumentException>(() => GameLocationService.SelectGameInstallFolder(manual, appData), "unrelated folder rejected as game installation");
string incompleteGame = Folder("incomplete-game");
File.WriteAllText(Path.Combine(incompleteGame, GameConstants.GameExecutable), "fixture");
Check(!GameLocationService.IsGameInstallation(incompleteGame), "executable without game data directory is insufficient");

// Realistic Steam metadata: secondary libraries, escaped paths, comments, legacy syntax.
string primary = Folder("primary-steam");
string secondary = Folder("secondary {Steam} library");
string secondaryGame = Game(secondary, "Shogun 2 Complete Edition");
AppManifest(secondary, "Shogun 2 Complete Edition");
Directory.CreateDirectory(Path.Combine(primary, "steamapps"));
string libraryList = Path.Combine(primary, "steamapps", "libraryfolders.vdf");
File.WriteAllText(libraryList, $"// Steam libraries\n\"libraryfolders\" {{ \"0\" {{ \"path\" {Quote(primary)} }} \"1\" {{ \"path\" {Quote(secondary)} \"apps\" {{ \"3846830\" \"123\" }} }} }}");
var locator = new SteamLibraryInstallationLocator([primary]);
Check(locator.FindInstallations().SequenceEqual(new[] { secondaryGame }), "modern VDF finds Complete Edition in a secondary library");
var automatic = new GameLocationService(roaming, [locator]);
Check(automatic.Resolve().Manifest.GameInstallPath == secondaryGame, "setup automatically resolves actual library installation");
Check(automatic.Resolve(manualApp).Manifest.GameInstallPath == manualGame, "valid saved manual game overrides automatic detection");
Check(automatic.Resolve(manualApp with { GameInstallPath = absentAppData }).Manifest.GameInstallPath == secondaryGame,
    "stale game path is detected again");
File.WriteAllText(libraryList, $"libraryfolders {{ 1 {Quote(secondary)} }}");
Check(locator.FindInstallations().SequenceEqual(new[] { secondaryGame }), "legacy library list syntax supported");
AppManifest(secondary, "Shogun 2 Complete Edition", GameConstants.WorkshopAppId);
Check(locator.FindInstallations().Count == 0, "original SHOGUN 2 app ID cannot match Complete Edition");
AppManifest(secondary, "../escape");
Check(locator.FindInstallations().Count == 0, "manifest cannot escape the Steam common folder");
File.WriteAllText(Path.Combine(secondary, "steamapps", "appmanifest_3846830.acf"), "\"AppState\" { broken");
Check(locator.FindInstallations().Count == 0, "malformed app manifest falls back to manual selection");
AppManifest(secondary, "Shogun 2 Complete Edition");
string primaryGame = Game(primary, "Primary Installation");
AppManifest(primary, "Primary Installation");
File.WriteAllText(libraryList, "\"libraryfolders\" { unclosed");
Check(locator.FindInstallations().SequenceEqual(new[] { primaryGame }), "broken library list does not hide a valid primary installation");

GameSetupResult prepared = new GameSetupService(new GameLocationService(roaming, [locator])).Prepare();
Check(prepared.Status == SetupStatus.Ready && prepared.Script is { WasCreated: true, Mods.Count: 0 },
    "setup resolves folders and creates only the missing script");
GameSetupResult unresolved = new GameSetupService(missingRoaming).Prepare();
Check(unresolved.Status == SetupStatus.NeedsBothFolders && unresolved.Script is null,
    "setup with missing app data returns selection state without creating a script");

count += ModManagerChecks.Run(testRoot);
count += ModImageChecks.Run(testRoot);
count += ModNameChecks.Run(testRoot);
count += DisabledModChecks.Run(testRoot);
Console.WriteLine($"\n{count} backend checks passed. Fixtures: {testRoot}");
if (args.Contains("--inspect-local"))
{
    // Read-only integration inspection: never call Prepare on the user's folders.
    GameLocationResult local = new GameLocationService().Resolve();
    Console.WriteLine($"Local detection: {local.Status}");
    Console.WriteLine($"App data: {local.Manifest.AppDataPath}");
    Console.WriteLine($"Game: {local.Manifest.GameInstallPath}");
    if (local.Manifest.AppDataPath is { } localAppData)
    {
        string localScript = GameConstants.GetUserScriptPath(localAppData);
        if (File.Exists(localScript))
        {
            UserScriptParseResult localMods = UserScriptService.Parse(File.ReadAllText(localScript));
            Console.WriteLine($"Existing local script (read only): {localMods.Mods.Count} mods, {localMods.Diagnostics.Count} diagnostics");
        }
    }
}
