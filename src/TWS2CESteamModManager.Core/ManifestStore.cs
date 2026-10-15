using System.Text.Json;

namespace TWS2CESteamModManager.Core;

public static class ManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>Returns null when the requested file does not exist. Does not create or repair it.</summary>
    public static ManagerManifest? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = File.OpenRead(Path.GetFullPath(path));
            ManagerManifest manifest = JsonSerializer.Deserialize<ManagerManifest>(stream, JsonOptions)
                ?? throw new InvalidDataException("The manifest must contain a JSON object.");
            Validate(manifest);
            return manifest;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException($"Invalid manager manifest: {exception.Message}", exception);
        }
    }

    /// <summary>Writes to the given file path, using a temporary file in the same directory.</summary>
    public static void Save(string path, ManagerManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, manifest, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(destination))
                File.Replace(temporary, destination, destinationBackupFileName: null);
            else
                File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void Validate(ManagerManifest manifest)
    {
        if (manifest.SchemaVersion != ManagerManifest.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported manifest schema version: {manifest.SchemaVersion}.");
        foreach (string? path in new[] { manifest.AppDataPath, manifest.GameInstallPath })
        {
            if (path is not null && (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
                throw new ArgumentException("Manifest folder paths must be absolute paths or null.");
            if (path is not null) _ = Path.GetFullPath(path);
        }
        // Folder existence is checked by setup, not persistence: a moved game or
        // disconnected drive should not make an otherwise valid manifest unreadable.
    }
}
