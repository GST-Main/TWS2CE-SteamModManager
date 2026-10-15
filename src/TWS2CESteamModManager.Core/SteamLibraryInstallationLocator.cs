using Microsoft.Win32;
using System.Globalization;
using System.Security;

namespace TWS2CESteamModManager.Core;

public interface IGameInstallationLocator
{
    IReadOnlyList<string> FindInstallations();
}

public sealed class SteamLibraryInstallationLocator(IEnumerable<string>? steamRoots = null) : IGameInstallationLocator
{
    /// <summary>Reads configured libraries and the Complete Edition app manifest; never starts Steam.</summary>
    public IReadOnlyList<string> FindInstallations()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in steamRoots ?? DiscoverSteamRoots())
        {
            if (!TryFullPath(root, out string? normalized)) continue;
            libraries.Add(normalized!);
            string file = Path.Combine(normalized!, "steamapps", "libraryfolders.vdf");
            ValveValue? configuration = ReadMetadata(file)?.Get("libraryfolders");
            if (configuration?.Children is null) continue;
            foreach ((string key, ValveValue entry) in configuration.Children)
            {
                if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                // Older library lists stored the path directly as the numbered value.
                string? path = entry.Text ?? entry.Get("path")?.Text;
                if (path is not null && TryFullPath(path, out string? library)) libraries.Add(library!);
            }
        }

        var result = new List<string>();
        foreach (string library in libraries)
        {
            string manifest = Path.Combine(library, "steamapps", $"appmanifest_{GameConstants.GameAppId}.acf");
            ValveValue? app = ReadMetadata(manifest)?.Get("AppState");
            if (app?.Get("appid")?.Text != GameConstants.GameAppId.ToString(CultureInfo.InvariantCulture)) continue;
            string? name = app.Get("installdir")?.Text;
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.Contains('/') || name.Contains('\\')) continue;
            string candidate = Path.Combine(library, "steamapps", "common", name);
            if (GameLocationService.IsGameInstallation(candidate)) result.Add(Path.GetFullPath(candidate));
        }
        return result.AsReadOnly();
    }

    public static IReadOnlyList<string> DiscoverSteamRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach ((string key, string value) in new[]
            {
                (@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
                (@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"),
                (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
            })
            {
                try
                {
                    if (Registry.GetValue(key, value, null) is string path && !string.IsNullOrWhiteSpace(path))
                        roots.Add(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { }
            }
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (programFiles.Length > 0) roots.Add(Path.Combine(programFiles, "Steam"));
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ValveValue? ReadMetadata(string path)
    {
        try { return ValveKeyValues.Parse(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            // A stale/inaccessible library should not prevent checking other libraries
            // or offering the manual folder-selection fallback.
            return null;
        }
    }

    private static bool TryFullPath(string path, out string? result)
    {
        result = null;
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(path);
            if (Path.IsPathFullyQualified(expanded)) result = Path.GetFullPath(expanded);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        return result is not null;
    }
}
