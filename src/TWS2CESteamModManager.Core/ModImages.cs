namespace TWS2CESteamModManager.Core;

/// <summary>Optional images use the pack's base filename, beside the pack.</summary>
public static class ModImages
{
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" });

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string? Find(string packPath) => FindAll(packPath).FirstOrDefault();

    public static IReadOnlyList<string> FindAll(string packPath)
    {
        string fullPath = Path.GetFullPath(packPath);
        string directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        string name = Path.GetFileNameWithoutExtension(fullPath);
        return Directory.EnumerateFiles(directory).Where(path => IsSupported(path)
            && Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Array.FindIndex(Extensions.ToArray(), extension => extension.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
