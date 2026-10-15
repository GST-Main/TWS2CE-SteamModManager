using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TWS2CESteamModManager.Core;

public static class ModManifestStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<ModSource>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static ModManifest? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[]? bytes = AtomicFile.ReadOptional(Path.GetFullPath(path));
        return bytes is null ? null : Deserialize(bytes);
    }

    public static void Save(string path, ModManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AtomicFile.Write(path, Serialize(manifest));
    }

    internal static ModManifest Deserialize(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("mods", out JsonElement mods)
                || mods.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("A mod manifest must contain a mods array; do not use the folder-settings manifest here.");
            ModManifest result = JsonSerializer.Deserialize<ModManifest>(bytes, Options)!;
            Validate(result);
            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid mod manifest JSON.", exception);
        }
    }

    internal static byte[] Serialize(ModManifest manifest)
    {
        Validate(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest, Options);
    }

    private static void Validate(ModManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != ModManifest.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported mod manifest schema: {manifest.SchemaVersion}.");
        if (manifest.Mods is null) throw new InvalidDataException("The mod list cannot be null.");
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ManagedMod[] ordered = manifest.Mods.OrderBy(mod => mod?.Order).ToArray();
        for (int index = 0; index < ordered.Length; index++)
        {
            ManagedMod? mod = ordered[index];
            if (mod is null || mod.Id == Guid.Empty || !ids.Add(mod.Id)
                || !UserScriptService.IsPackFileName(mod.FileName)
                || !UserScriptService.IsPackFileName(mod.OriginalFileName)
                || !names.Add(mod.FileName) || mod.Order != index)
                throw new InvalidDataException("Mod entries require unique IDs/pack filenames and contiguous zero-based order values.");
            if (mod.Source == ModSource.Imported)
            {
                if (mod.WorkshopItemId is not null || mod.FileName != mod.OriginalFileName)
                    throw new InvalidDataException("Imported mods must retain their filename and have no Workshop association.");
            }
            else if (mod.Source == ModSource.Workshop)
            {
                if (mod.WorkshopItemId is not { } itemId || itemId is 0 or ulong.MaxValue
                    || mod.FileName != $"MOD_{itemId.ToString(CultureInfo.InvariantCulture)}_{mod.OriginalFileName}")
                    throw new InvalidDataException("Workshop mod filename does not match its item ID and original filename.");
            }
            else throw new InvalidDataException("Unknown mod source.");
            if (mod.DisplayName is { } displayName && (displayName.Length > 1024 || displayName.Any(char.IsControl)))
                throw new InvalidDataException("A mod display name must be a single line of at most 1024 characters.");
        }
    }
}
