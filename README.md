# TW:S2CE Steam Mod Manager

A community mod manager for Total War: SHOGUN 2 Complete Edition. Browse Steam
Workshop, download mods, and manage their enabled state and load order.

**Currently in beta.** Some features may not work or may contain bugs. Back up
important files before use.

## How to use

1. Extract the release and run `TWS2CESteamModManager.exe`. Keep Steam running
   to browse and download Workshop mods.
2. Confirm the detected game folders, or select them manually.
3. Use **Browse** to download mods, or **Import** to add local `.pack` files.
4. In **Mods**, enable, disable, reorder, or delete mods. Click **Launch** to play.

You may be able to use mod.io alongside this manager, but this is unverified.
There is no direct mod.io integration; compatible `.pack` files can be imported.

## Known issues

- Many mods for the original SHOGUN 2 are incompatible with Complete Edition.
- Releases are unsigned. I do not have a code-signing certificate and do not plan
  to buy one for this community project. Windows SmartScreen may show a warning.

## How to build

Requires Windows x64 and the .NET 10 SDK. From the project root:

```powershell
dotnet build TWS2CESteamModManager.sln
dotnet run --project src/TWS2CESteamModManager.App
dotnet publish src/TWS2CESteamModManager.App -c Release -p:PublishProfile=PortableWinX64
```

Publish output: `artifacts/publish/single-file/win-x64/`.

## License

[MIT](LICENSE).
