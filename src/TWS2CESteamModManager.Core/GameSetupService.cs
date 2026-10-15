namespace TWS2CESteamModManager.Core;

public sealed record GameSetupResult(GameLocationResult Locations, UserScriptDocument? Script)
{
    public SetupStatus Status => Locations.Status;
    public ManagerManifest Manifest => Locations.Manifest;
}

public sealed class GameSetupService(GameLocationService? locations = null)
{
    private readonly GameLocationService locations = locations ?? new GameLocationService();

    /// <summary>Resolves folders and loads/creates the script when app data is available.
    /// Manifest persistence remains an explicit operation at a caller-provided path.</summary>
    public GameSetupResult Prepare(ManagerManifest? manifest = null)
    {
        GameLocationResult result = locations.Resolve(manifest);
        UserScriptDocument? script = result.Manifest.AppDataPath is { } appData
            ? UserScriptService.LoadOrCreate(appData) : null;
        return new GameSetupResult(result, script);
    }
}
