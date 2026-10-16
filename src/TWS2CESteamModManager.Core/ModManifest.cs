namespace TWS2CESteamModManager.Core;

public enum ModSource { Imported, Workshop }

public sealed record ManagedMod
{
    public Guid Id { get; init; }
    public string FileName { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string? DisplayName { get; init; }
    public ModSource Source { get; init; }
    public ulong? WorkshopItemId { get; init; }
    public int Order { get; init; }
    public bool IsEnabled { get; init; } = true;
}

public sealed record ModManifest
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<ManagedMod> Mods { get; init; } = Array.Empty<ManagedMod>();
}

public sealed record WorkshopModPackage(string Directory, IReadOnlyList<string> PackFiles, string? ThumbnailPath = null, string? Title = null);

/// <summary>Downloads into a cache/staging location. The manager installs the returned pack files.</summary>
public interface IWorkshopModDownloader
{
    WorkshopModPackage Download(ulong itemId, string cacheDirectory,
        Action<string>? report = null, CancellationToken cancellationToken = default);
}

public sealed class ModStateConflictException(string message) : IOException(message);

/// <summary>A local-only enable needs to be retried on the caller's initialized Steam thread.</summary>
public sealed class WorkshopModRestoreRequiredException(Guid modId, ulong itemId)
    : IOException("The Workshop mod pack is missing and must be downloaded again.")
{
    public Guid ModId { get; } = modId;
    public ulong ItemId { get; } = itemId;
}
