namespace TWS2CESteamModManager.Core;

public sealed record ManagerManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string? AppDataPath { get; init; }
    public string? GameInstallPath { get; init; }
}
