# Complete Edition backend APIs

This .NET 10 library handles folder setup, manifest persistence, mod management,
and mod-script synchronization independently of a UI. Local mod operations work
without a running Steam client. The console project
references it, and the WPF app uses the same library. The WPF app stores folder
paths together with language and notice preferences in `config.json` through its
`AppConfigStore`; `mods.json` remains separate. The caller-selected `ManifestStore`
API below remains available for tools that only need folder-path persistence.
Do not use `ManifestStore.Save` to edit the WPF config: it would discard the
application preferences stored alongside the folder paths.

## Manifest persistence

The caller chooses the complete JSON file path. To place it beside an executable:

```csharp
using TWS2CESteamModManager.Core;

string manifestPath = Path.Combine(AppContext.BaseDirectory, "manifest.json");
ManagerManifest manifest = ManifestStore.Load(manifestPath) ?? new ManagerManifest();

// Update the paths as folder detection/manual selection completes.
ManifestStore.Save(manifestPath, manifest);
```

The JSON contains only:

```json
{
  "schemaVersion": 1,
  "appDataPath": null,
  "gameInstallPath": null
}
```

Resolved paths are absolute. Null represents an unresolved folder. Paths may
refer to currently missing folders; setup revalidates them separately. Game and
Workshop IDs live in `GameConstants` and mod lists are returned separately.

`Load` returns null for a missing file, reports corrupt JSON/invalid field values
with `InvalidDataException`, and rejects unsupported schema versions. `Save`
validates the manifest, creates missing parent directories, flushes a temporary
file in the same directory, then replaces/moves it into the requested location.
It reports file-access failures to the caller, including when the executable
directory is not writable. It does not select another storage location.

## Detect folders and prepare the script

```csharp
var setup = new GameSetupService();
GameSetupResult result = setup.Prepare(manifest);

// A future UI inspects result.Status and requests the missing folder(s).
// Example after the user selects an app-data folder:
// manifest = GameLocationService.SelectAppDataFolder(result.Manifest, selectedPath);
// Example after the user selects the game installation folder:
// manifest = GameLocationService.SelectGameInstallFolder(result.Manifest, selectedPath);
// result = setup.Prepare(manifest);

ManifestStore.Save(manifestPath, result.Manifest);
IReadOnlyList<string> mods = result.Script?.Mods ?? Array.Empty<string>();
```

`SetupStatus` is `Ready`, `NeedsAppDataFolder`, `NeedsGameFolder`, or
`NeedsBothFolders`. Both missing folders can be reported in a single result.
Selection methods validate and return a new manifest; they do not display dialogs.

Valid saved folder selections take precedence. Missing/invalid saved locations
are detected again. App-data detection uses the Windows roaming app-data known
folder and `The Creative Assembly/Shogun2CompleteEdition`. A missing game app-data
root is not automatically created: the result requests manual selection.

The default installation locator reads Steam registry locations, modern/legacy
`steamapps/libraryfolders.vdf`, and each library's
`steamapps/appmanifest_3846830.acf`. It verifies the manifest's App ID and uses
its `installdir` value rather than assuming the game is on a particular drive.
Candidates and manual selections must contain `shogun2_complete_edition.exe`
and a `data` directory. A stale or unreadable library does not stop other
libraries from being checked.

`GameLocationService.Resolve(manifest)` performs read-only folder detection.
`GameSetupService.Prepare(manifest)` also loads/creates the mod script as soon
as app data is resolved, even if game-folder selection is still needed. Neither
method automatically loads or saves the manifest.

The `IGameInstallationLocator` interface supports additional discovery methods.
For an already initialized Steam session, the console project also provides
`TWS2CESteamModManager.Steam.SteamApiInstallationLocator`. Pass it before the file-based fallback:

```csharp
var locations = new GameLocationService(installationLocators:
[
    new TWS2CESteamModManager.Steam.SteamApiInstallationLocator(),
    new SteamLibraryInstallationLocator()
]);
GameLocationResult detected = locations.Resolve(manifest);
```

The caller owns Steam initialization/shutdown when using that optional adapter.
The default core setup does not initialize Steam or require it to be running.

## Mod script

```csharp
UserScriptDocument script = UserScriptService.LoadOrCreate(appDataPath);
IReadOnlyList<string> mods = script.Mods;

// Or parse supplied text without any file access:
UserScriptParseResult parsed = UserScriptService.Parse("mod example.pack;");
```

The path is always `<appDataPath>/Steam/scripts/user.script.txt`. After validating
that app data exists, `LoadOrCreate` creates missing script directories and an
empty UTF-16 LE file with a BOM. An existing file is read without being rewritten.
UTF-8 and BOM-marked UTF-16/UTF-32 files are detected by the reader. The returned
document includes the original decoded content, detected encoding, BOM status,
whether the file was created, the mod names, and parse diagnostics.

Parsing supports one `mod filename.pack;` or `mod "filename with spaces.pack";`
declaration per line, optional whitespace, and trailing `//` or `#` comments.
Blank/comment lines are skipped. Filename spelling, case, order, and duplicates
are retained. Malformed mod declarations and paths in place of filenames produce
line-numbered diagnostics; other game settings remain in `Content` without being
interpreted as mods. Parsing does not check whether pack files are installed.
`UserScriptService.UpdateEnabledMods(appDataPath, enabledFileNames)` also writes
an ordered enabled list. `RenderEnabledMods(document, enabledFileNames)` builds
the new bytes without writing. These preserve the detected encoding/BOM, existing
line endings, comments (including comments from replaced mod lines), and unrelated
settings. Malformed declarations remain available with diagnostics. Quoted names
retain meaningful whitespace. Prefer `ModManager` for managed operations so the
script and the mod manifest stay synchronized together.

## Mod management

Folder settings and the mod list have separate JSON files. Keep the existing
`manifest.json` for `ManagerManifest`, and pass another caller-selected path such
as `mods.json` for the mod list:

```csharp
using TWS2CESteamModManager.Core;

ManagerManifest folders = ManifestStore.Load(manifestPath)
    ?? throw new InvalidOperationException("Set up the game folders first.");
string modsPath = Path.Combine(AppContext.BaseDirectory, "mods.json");

using var manager = ModManager.Open(folders, modsPath);
ManagedMod imported = manager.Import(@"C:\MyMods\example.pack");
manager.SetEnabled(imported.Id, false);
manager.Move(imported.Id, 0);
manager.SetEnabled(imported.Id, true);
IReadOnlyList<ManagedMod> orderedMods = manager.List();
// manager.Delete(imported.Id); // Removes the installed copy, list entry, and script declaration.
```

| API | Behavior |
| --- | --- |
| `ModManager.Open(folders, modsPath, downloader?, downloadCacheDirectory?)` | Opens/creates the mod manifest and synchronizes the script. Both game folders must already be resolved. |
| `List()` | Returns a snapshot in persisted order, including disabled entries. |
| `Import(packFilePath, cancellationToken)` | Keeps the filename, copies into `<game>/data` when needed, and appends enabled. An existing file directly in `data` is registered in place. |
| `Import(packFilePath, imageFilePath, cancellationToken)` | Also copies a selected image with the pack's base filename. A null image path auto-discovers one beside the source pack, as does the original overload. |
| `Download(itemId, report?, cancellationToken)` | Uses the supplied Steam adapter, installs pack files into `<game>/data`, and appends them enabled. Returns one entry per pack. |
| `SetEnabled(id, enabled, report?, cancellationToken, allowDownload)` | Moves packs to/from `data/disabled_mods`, persists the flag, and updates script order. Enabling a missing Workshop pack re-downloads it; a missing imported pack raises `FileNotFoundException`. Optional arguments default to null, no cancellation, and download allowed. |
| `SetWorkshopDisplayNames(titlesByItemId)` | Fills missing Workshop display names by item ID, preserving existing saved names. Returns whether any names were saved. |
| `Move(id, newIndex)` | Moves an entry to a zero-based position and updates the script. Disabled entries retain their list position. |
| `Delete(id)` | Removes the pack from its active/disabled location, associated images, its manifest entry, and any enabled script declaration. An already missing pack can still be removed from the list. |
| `GetFilePath(id)` | Returns the expected pack path in `data` when enabled, or `data/disabled_mods` when disabled. |
| `GetImagePath(id)` | Returns an existing same-base-name image, or null. Works for Workshop and imported mods. |
| `SynchronizeScript()` | Regenerates valid mod declarations from current enabled entries. |
| `ScriptDiagnostics` | Reports malformed mod declarations retained in the script. |

Every mutation saves automatically. New entries are enabled at the end. The
manifest records stable GUIDs, filenames, original filenames, optional display names, source, optional
Workshop item ID, zero-based order, and enabled flags. The script only contains
enabled filenames, following that order.

Disabling physically moves the pack out of the active `data` directory, since
some pack types load even without a script declaration. Enabling moves it back
at its saved list position. Existing disabled entries are relocated automatically
when opening an older manifest; enabled entries found in disabled storage are
restored locally. Missing files do not trigger downloads during startup.

If neither location contains a Workshop pack, enabling downloads the package and
restores only the entry's original pack filename. Its ID, display name, order,
and sibling entries stay intact. An unavailable/renamed original pack, failed
download, or cancellation preserves its previous enabled state. Imported packs never use a
downloader: restore their file into `disabled_mods` before enabling them.
Thumbnail images remain available from either location, and are normally retained
in `data` while the pack is disabled. Delete removes the associated images too.

For UI callers, use `allowDownload: false` for local moves. A missing Workshop
pack then raises `WorkshopModRestoreRequiredException`; retry on the initialized
Steam thread with downloads allowed. The WPF app uses `IWorkshopModRestorer` to do
this, sharing download progress, cancellation, and shutdown handling. Local moves
do not require Steam. Duplicate copies in both locations cause a conflict rather
than overwriting either file.

`ManagedMod.DisplayName` serializes as `displayName`. It is an optional field in
schema version 1, so manifests created before names were added still load.
Imports (including older imported records opened by the manager) use the original
filename without `.pack` as their display name. A `WorkshopModPackage` can supply
its optional `Title`; the Steam adapter passes the Workshop title through to every
pack in that package. Older Workshop entries keep a null name until metadata is
available. Display names do not rename files or enter the game script.

The WPF session fills missing Workshop names asynchronously using item IDs and
`SetWorkshopDisplayNames`. Failed lookups retain the filename fallback and do not
block local mod operations. Existing saved names are preserved.

Imported entries have source `imported` and no Workshop ID, including manually
imported filenames that happen to start with `MOD_`. Workshop entries have source
`workshop` and filenames `MOD_<item ID>_<original filename>`. A multi-pack Workshop
item creates multiple entries sharing its item ID, sorted by filename when first
added. Items without pack files are rejected. File contents are copied as supplied;
pack contents and dependencies are not interpreted or rewritten.

Opening a manager adopts unknown filenames already declared in `user.script.txt`
as local imports, preserving their relative order. It does not scan all pack files
in `data` or automatically manage base-game packs. A missing referenced pack can
remain listed. For already known entries, saved manifest order and enabled flags
are authoritative when opening; unknown script entries are appended enabled.
Duplicate references become a single managed entry.

Filename collisions, duplicate imports, and already-managed Workshop item IDs are
rejected instead of overwriting existing packs. `Download` installs new items;
`SetEnabled` can recover a missing pack of an already-managed item. Updating an
existing pack in place is not supported.
The caller can inspect `ModManifestStore.Load(path)` or explicitly persist a
`ModManifest` using `ModManifestStore.Save(path, manifest)`. Manager operations
should be used when file/script synchronization is needed.

### Steam download adapter

The shared Steam project provides `TWS2CESteamModManager.Steam.SteamWorkshopModDownloader`, which calls
the existing `WorkshopDownloader` and returns downloaded pack files to the core
manager. The caller owns an initialized Steam API session and executes native
downloads on its Steam thread:

```csharp
// Inside an initialized Steam session, with resolved folders and a Workshop itemId:
using var manager = ModManager.Open(folders, modsPath,
    downloader: new TWS2CESteamModManager.Steam.SteamWorkshopModDownloader());
IReadOnlyList<ManagedMod> installed = manager.Download(itemId, Console.WriteLine);
```

This uses the existing Workshop App ID `34330`, legacy single-file and modern
folder download paths, progress reporting, timeout, and cancellation. Completed
payloads are cached under `.mod-downloads` beside the mod manifest by default;
`downloadCacheDirectory` overrides that parent location. Cache payloads are retained
separately from installed copies. Pack files are installed into the game.
The optional primary Workshop thumbnail is also installed into `data` beside each
pack, with its base filename and detected image extension. For example,
`MOD_123_example.pack` has `MOD_123_example.jpg`. Multi-pack downloads copy the same
thumbnail beside each pack. Thumbnail network failures are reported but do not
discard downloaded mod content; cancellation still aborts.

Manual imports discover `.png`, `.jpg`, `.jpeg`, `.gif`, or `.bmp` beside the source
pack, or accept an explicit `imageFilePath`. Images already beside in-place imports
are retained. The public `ModImages.Find(packPath)` helper uses the same convention.
Image paths are derived from filenames, so the mod-manifest schema is unchanged.
Image files are excluded from `user.script.txt`. Pack and image additions/deletions
share rollback handling; existing image destinations are never overwritten.
The original console `download` command remains a standalone cache download;
use `ModManager.Download` for installation and registration with the mod manager.

### Persistence and conflicts

Each manager holds an exclusive sidecar lock for its mod manifest until disposed
and serializes operations on that instance. Operations copy into private staging
files, install/move/delete only validated packs and their image sidecars within
`data` and `data/disabled_mods`, replace JSON
and script files atomically, and roll back file/metadata changes on reported
failures. Deleted files are temporarily renamed until both metadata writes succeed.
Read-only files and permission errors are surfaced to the caller. An incomplete
rollback reports an aggregate error and prevents further operations on that instance.

Changes to the mod manifest or script by another application are detected before
committing a mutation and produce `ModStateConflictException`. Dispose/reopen the
manager to load the current files. Linked pack files and linked `data`/`disabled_mods` directories
are rejected. No directory-recursive deletion is used by the mod manager.

## Verification

No test package downloads are required:

```powershell
dotnet run --project tests/TWS2CESteamModManager.Core.Checks
```

The checks use isolated fixtures under `.test-results/` in the current directory.
They cover local imports, simulated Steam downloads, multi-pack installation,
enable/order persistence, script preservation, deletion, and I/O rollback. They
do not download a real Workshop item or install anything into the user's game.
To additionally inspect local detection and parse an existing local script
without changing game files:

```powershell
dotnet run --project tests/TWS2CESteamModManager.Core.Checks -- --inspect-local
```
