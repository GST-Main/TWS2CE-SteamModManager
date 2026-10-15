using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TWS2CESteamModManager.Core;

namespace TWS2CESteamModManager.App.Services;

public sealed record AppConfig
{
    public int SchemaVersion { get; init; } = 1;
    public string? AppDataPath { get; init; }
    public string? GameInstallPath { get; init; }
    public string? Language { get; init; }
    public int? AcceptedNoticeVersion { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; init; }

    public ManagerManifest GetFolders() => new() { AppDataPath = AppDataPath, GameInstallPath = GameInstallPath };
}

/// <summary>Atomic, read-modify-write persistence shared by all application settings.</summary>
public sealed class AppConfigStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly string path;
    private readonly object gate;

    public AppConfigStore(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(AppContext.BaseDirectory, "config.json"));
        gate = Gates.GetOrAdd(this.path, _ => new object());
    }

    public AppConfig Load()
    {
        lock (gate) return LoadOrMigrate();
    }

    public void Update(Func<AppConfig, AppConfig> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (gate) Write(update(LoadOrMigrate()));
    }

    private AppConfig LoadOrMigrate()
    {
        AppConfig? existing = Read<AppConfig>(path);
        if (existing is not null) { Validate(existing); return existing; }

        // Custom caller-selected paths do not import unrelated files beside them.
        if (!Path.GetFileName(path).Equals("config.json", StringComparison.OrdinalIgnoreCase)) return new();
        string directory = Path.GetDirectoryName(path)!;
        string foldersPath = Path.Combine(directory, "manifest.json");
        string languagePath = Path.Combine(directory, "settings.json");
        string noticePath = Path.Combine(directory, "notice.json");
        ManagerManifest? folders;
        try { folders = ManifestStore.Load(foldersPath); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid folder paths in manifest.json.", error); }
        LegacyLanguage? language = Read<LegacyLanguage>(languagePath);
        LegacyNotice? notice = Read<LegacyNotice>(noticePath);
        if (folders is null && language is null && notice is null) return new();

        var migrated = new AppConfig
        {
            AppDataPath = folders?.AppDataPath, GameInstallPath = folders?.GameInstallPath,
            Language = language?.Language, AcceptedNoticeVersion = notice?.AcceptedNoticeVersion
        };
        Write(migrated);
        // Delete sources only after every legacy file was read and config committed.
        // Any undeletable leftovers are ignored on later loads.
        foreach (string legacy in new[] { foldersPath, languagePath, noticePath })
            try { File.Delete(legacy); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return migrated;
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, JsonOptions)
                ?? throw new InvalidDataException($"Configuration must contain a JSON object: {Path.GetFileName(path)}.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw new InvalidDataException($"Invalid configuration: {Path.GetFileName(path)}.", error); }
    }

    private static void Validate(AppConfig config)
    {
        if (config.SchemaVersion != 1) throw new InvalidDataException($"Unsupported configuration schema version: {config.SchemaVersion}.");
        foreach (string? folder in new[] { config.AppDataPath, config.GameInstallPath })
            if (folder is not null && (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)))
                throw new InvalidDataException("Configuration folder paths must be absolute paths or null.");
    }

    private void Write(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Validate(config);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".config.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, config, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record LegacyLanguage(string? Language);
    private sealed record LegacyNotice(int? AcceptedNoticeVersion);
}
