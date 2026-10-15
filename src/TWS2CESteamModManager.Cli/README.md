# Console and developer guide

For application usage, beta limitations, and build instructions, see the
[root README](../../README.md). For mod-management APIs, see the
[backend API guide](../TWS2CESteamModManager.Core/README.md).

## Steam connectivity probe

```powershell
dotnet run --project src/TWS2CESteamModManager.Cli
```

Or open `TWS2CESteamModManager.sln` in a Visual Studio version that
supports .NET 10 and has the .NET desktop development workload installed. Select
`TWS2CESteamModManager.App` as the startup project and press F5.

Expected output includes your Steam display name, App ID `34330`, and
`Steam initialization succeeded.` A failed check exits with code 1 and prints
troubleshooting steps. The probe shuts down its Steam API session on exit.

## List available Workshop categories

```powershell
dotnet run --project src/TWS2CESteamModManager.Cli -- tags
dotnet run --project src/TWS2CESteamModManager.Cli -- tags --all
```

`tags` fetches the current category names and item counts from the public
[English SHOGUN 2 Workshop page](https://steamcommunity.com/app/34330/workshop/?l=english).
It shows configured categories with a positive public item count, in the order
configured by the Workshop. `--all` includes configured categories with zero
current matches. It does not include undeclared, author-created tags that may
appear on individual items.

Use a returned name with the existing filter, for example:

```powershell
dotnet run --project src/TWS2CESteamModManager.Cli -- workshop --tag historical
```

The category command reads structured JSON embedded in the public page, including
the declared tag list and tag-count facets. It does not sample Workshop items or
use a hardcoded category list. Counts reflect the anonymous public page and can
differ from signed-in views or native SDK results because of content preferences
and caching. Categories can overlap, so their counts should not be added together.

The HTTP request has a 30-second timeout and supports Ctrl+C. A failed request or
unexpected page format exits with code 1 and a diagnostic message. This website
data format is separate from the documented Steam SDK and may need maintenance
when Steam changes the page. No extra packages are required.

## Fetch Workshop items

Keep Steam running, then run:

```powershell
dotnet run --project src/TWS2CESteamModManager.Cli -- workshop
dotnet run --project src/TWS2CESteamModManager.Cli -- workshop --page 2
dotnet run --project src/TWS2CESteamModManager.Cli -- workshop --search "Darthmod" --sort subscribed
dotnet run --project src/TWS2CESteamModManager.Cli -- workshop --sort newest
dotnet run --project src/TWS2CESteamModManager.Cli -- workshop --tag map
dotnet run --project src/TWS2CESteamModManager.Cli -- workshop --tag map --tag multiplayer --sort subscribed
dotnet run --project src/TWS2CESteamModManager.Cli -- workshop --tag mod --search "Darthmod" --page 1
dotnet run --project src/TWS2CESteamModManager.Cli -- --help
```

The `--` separates .NET CLI arguments from this application's arguments.
Without a command, the app still performs only the connectivity check.

The `workshop` command queries the public catalog represented by
[SHOGUN 2's Workshop](https://steamcommunity.com/app/34330/workshop/).
It returns up to 50 items per page, starting at page 1, and prints the total
matching count. Each item shows its title, Workshop ID, author Steam ID,
last update time in UTC, tags, votes, and item page link. Unicode titles are
supported. Search uses Steam's server-side text matching rules; sorting defaults to `updated` and also
accepts `popular` (past week), `subscribed` (most subscribed first), `rated`
(top rated), or `newest`.

Use `--tag CATEGORY` to filter by a Workshop category such as `map`, `historical`,
`multiplayer`, `mod`, `ui`, `graphical`, `overhaul`, `units`, `campaign`, `battle`,
or `compilation`. Repeat `--tag` to require **all** selected categories. For
example, `--tag map --tag multiplayer` returns multiplayer maps. Category names
are trimmed and normalized to lowercase; duplicate selections are ignored.
Each flag takes one category, so use separate flags rather than a comma-separated
list. Unknown category names are sent to Steam and normally produce no matches.

Category filters combine with search, sorting, and paging. They are applied
before Steam returns results, so page contents and the total matching count
reflect the selected categories. Omitting `--tag` queries all categories.

The `workshop` command fetches item metadata.
It lists individual Workshop items rather than collection containers. Its ordering
can differ from the Workshop homepage's featured sections.

Requests time out after 30 seconds, and Ctrl+C cancels the current request.
Steam query handles and call-result registrations are released before Steam
shuts down. Invalid command arguments exit with code 2; Steam/query failures
exit with code 1; successful requests, including empty results, exit with code 0.

## Download an item

Pass the item's numeric ID or its Steam Community item-page URL:

```powershell
dotnet run --project src/TWS2CESteamModManager.Cli -- download ITEM_ID --dry-run
dotnet run --project src/TWS2CESteamModManager.Cli -- download ITEM_ID
dotnet run --project src/TWS2CESteamModManager.Cli -- download "https://steamcommunity.com/sharedfiles/filedetails/?id=ITEM_ID"
dotnet run --project src/TWS2CESteamModManager.Cli -- download ITEM_ID --output "D:\MyMods" --timeout 1200
```

Replace `ITEM_ID` with the number in an item's page URL. `--dry-run` shows the
title, format, reported size, and destination using only a metadata query.

The default destination is `downloads/<item ID>/` relative to the directory
where you launch the command. `--output` selects a different parent folder;
the item ID is still appended. Existing destinations are preserved: choose
another parent folder to download a fresh copy. Downloads are not installed or
enabled in the game and do not subscribe you to the item or its dependencies.

The command verifies the consumer App ID before transferring content and rejects
collections, other non-mod/map item types, banned items, and inaccessible items.
Legacy single-file content is downloaded through `UGCDownloadToLocation` using
the primary file handle. Folder-based content uses `DownloadItem` at normal
priority, waits for the matching completion callback, and copies the completed
content from Steam's cache into the destination. This path can update Steam's
cached copy of an item.

Downloads also attempt to fetch the item's primary thumbnail. The result's
`ThumbnailPath` identifies the image in the completed download folder. HTTP/image
failures are reported without discarding downloaded mod content; user cancellation
still cancels the operation. The managed download API installs the image beside
each pack using the same base filename, such as `MOD_123_example.jpg`.

Progress is printed roughly once a second while waiting. The default download
timeout is 600 seconds; `--timeout` accepts 1 to 86400 seconds. Item metadata
queries have a separate 30-second timeout. Ctrl+C stops waiting/copying; Steam
may continue a native transfer in the background. On failure or cancellation,
an incomplete staging folder may remain, and its path is printed. Only a
completed transfer is moved into the final item folder. Symbolic links and
junctions in output paths or copied content are rejected.


## Steam integration notes

The manager's **Launch** button validates the configured game folder, then opens
`steam://run/3846830` to start Complete Edition through Steam. This allows Steam
to supply the game's launch context. Workshop access still uses App ID `34330`.
See Valve's [Steam launch mechanism](https://partner.steamgames.com/doc/api/steam_api#SteamAPI_RestartAppIfNecessary).

The WPF app copies `steam_appid.txt` beside the executable for both development
and published packages. It contains `34330` and is explicitly excluded from
single-file bundling so Steam can read the physical file when the manager is
launched directly. Steam must be running and signed in to an account with access
to that App ID. This tool runs in SHOGUN 2's app context; Steam may temporarily
show the game as running. Initializing successfully does not demonstrate that
all Workshop operations are available to a third-party manager.

When Steam is not running, Browse displays **Steam must be running to browse
Workshop** with **Launch Steam** and **Retry** actions. Launch Steam opens the
client; sign in and select Retry to load the Workshop. Other query or initialization
failures retain their specific error details.

`WorkshopCatalog.Fetch` uses `CreateQueryAllUGCRequest` with consumer App ID
34330 and no creator-app filter, then pumps `SteamAPI.RunCallbacks()` while
waiting for the asynchronous result. It returns item/page records independently
of console formatting. Item records also carry the description returned by Steam
and a preview-image URL; Steam may truncate descriptions and tags. Author names
are resolved through Steam friends information, with a Steam ID fallback.

Low-level callers own Steam initialization and shutdown. The WPF app uses
`SteamWorkshopBrowserService`, which owns a dedicated worker thread for native
queries and callback pumping. Results return asynchronously to the UI thread;
category HTTP requests and thumbnail loading also run asynchronously.
`IWorkshopModInstaller.InstallAsync(manager, itemId, report, cancellationToken)`
executes the managed download on the same initialized worker. The WPF session
uses this method with `SteamWorkshopModDownloader`; local file operations run
on background tasks and UI state comes from snapshots, so a download never
holds a mod-manager lock on the UI thread.

`IWorkshopBrowserService.FetchDetailsAsync(itemId, cancellationToken)` returns
`WorkshopItemDetails`, including `Item` metadata, the full `Description`, and
image-only `Screenshots` (primary preview followed by additional image previews).
Video/model previews are excluded. The low-level equivalent is
`WorkshopDetails.Fetch` on an initialized Steam thread. The description is Steam
BBCode; the WPF popup converts common tags to selectable plain text and retains
link destinations. See Valve's [long-description and preview query options](https://partner.steamgames.com/doc/api/ISteamUGC#SetReturnLongDescription).

## Verification

```powershell
dotnet run --project tests/TWS2CESteamModManager.Core.Checks
dotnet run --project tests/TWS2CESteamModManager.App.Checks
# Optional: Steam signed in, live read-only metadata and thumbnail checks
dotnet run --project tests/TWS2CESteamModManager.App.Checks -- --live
```

Browser checks cover search submission, single-category filtering, sort changes,
paging, stale results, errors and retries, popup cancellation, gallery navigation,
startup/setup, mod toggling/reordering/import/deletion, installed labels,
transfer cancellation/retry/shutdown, image handling, and disposal. They also render the
actual WPF content at two window sizes into `visual-checks/` under the checks
project's build output. Integration checks use isolated game folders and a fake
content transport. `--live` adds current Steam metadata/image renders and verifies
the real Steam worker installs an isolated fake package; it does not install real
Workshop content into your game. If a running app locks normal build output, use
`--artifacts-path .test-results/verification-build` with `dotnet run`.

## Dependencies

Steamworks.NET **2025.164.1**, Windows x64, from the
[official standalone release](https://github.com/rlabrecque/Steamworks.NET/releases/tag/2025.164.1).

Original archive: `Steamworks.NET-Standalone_2025.164.1.zip`

SHA-256: `9412348CC404563BE5A43A28347CFEDA3C679EE044A14D87A507ED2D796A537D`

The wrapper license is included at `src/TWS2CESteamModManager.Steam/lib/LICENSE.txt` and copied
to the output as `Steamworks.NET.LICENSE.txt`. The native `steam_api64.dll` is
Valve's Steamworks runtime, supplied in the same release.

References:

- [Steamworks.NET standalone setup](https://github.com/rlabrecque/Steamworks.NET/blob/master/Standalone/README.md)
- [Valve: Steam API initialization](https://partner.steamgames.com/doc/api/steam_api#SteamAPI_Init)
- [Valve: Workshop interface](https://partner.steamgames.com/doc/api/ISteamUGC)
