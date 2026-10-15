namespace TWS2CESteamModManager.Core;

public enum SetupStatus { Ready, NeedsAppDataFolder, NeedsGameFolder, NeedsBothFolders }

public sealed record GameLocationResult(ManagerManifest Manifest, SetupStatus Status, IReadOnlyList<string> Diagnostics);

public sealed class GameLocationService
{
    private readonly string? defaultAppDataPath;
    private readonly IGameInstallationLocator[] installationLocators;

    public GameLocationService(string? roamingAppDataPath = null,
        IEnumerable<IGameInstallationLocator>? installationLocators = null)
    {
        string roaming = roamingAppDataPath ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        defaultAppDataPath = string.IsNullOrWhiteSpace(roaming) ? null : GameConstants.GetDefaultAppDataPath(roaming);
        this.installationLocators = (installationLocators ?? [new SteamLibraryInstallationLocator()]).ToArray();
    }

    /// <summary>Read-only detection. Saved, valid selections take precedence over auto-detection.</summary>
    public GameLocationResult Resolve(ManagerManifest? saved = null)
    {
        var diagnostics = new List<string>();
        string? appData = ExistingDirectory(saved?.AppDataPath) ?? ExistingDirectory(defaultAppDataPath);
        string? game = ExistingGame(saved?.GameInstallPath);
        if (game is null)
        {
            foreach (IGameInstallationLocator locator in installationLocators)
            {
                try
                {
                    game = locator.FindInstallations().Select(ExistingGame).FirstOrDefault(path => path is not null);
                    if (game is not null) break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    diagnostics.Add($"Installation detection failed: {exception.Message}");
                }
            }
        }
        if (appData is null) diagnostics.Add("Select an existing SHOGUN 2 Complete Edition app-data folder.");
        if (game is null) diagnostics.Add("Select a game installation folder containing shogun2_complete_edition.exe and data.");

        SetupStatus status = (appData is not null, game is not null) switch
        {
            (true, true) => SetupStatus.Ready,
            (false, true) => SetupStatus.NeedsAppDataFolder,
            (true, false) => SetupStatus.NeedsGameFolder,
            _ => SetupStatus.NeedsBothFolders
        };
        ManagerManifest manifest = (saved ?? new ManagerManifest()) with { AppDataPath = appData, GameInstallPath = game };
        return new GameLocationResult(manifest, status, diagnostics.AsReadOnly());
    }

    public static ManagerManifest SelectAppDataFolder(ManagerManifest manifest, string path)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string folder = ExistingDirectory(path)
            ?? throw new ArgumentException("Select an existing app-data folder.", nameof(path));
        return manifest with { AppDataPath = folder };
    }

    public static ManagerManifest SelectGameInstallFolder(ManagerManifest manifest, string path)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string folder = ExistingGame(path)
            ?? throw new ArgumentException("Select the game folder containing shogun2_complete_edition.exe and data.", nameof(path));
        return manifest with { GameInstallPath = folder };
    }

    public static bool IsGameInstallation(string path) => ExistingGame(path) is not null;

    private static string? ExistingGame(string? path)
    {
        string? folder = ExistingDirectory(path);
        return folder is not null && File.Exists(Path.Combine(folder, GameConstants.GameExecutable))
            && Directory.Exists(Path.Combine(folder, "data")) ? folder : null;
    }

    private static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(path);
            return Path.IsPathFullyQualified(expanded) && Directory.Exists(expanded) ? Path.GetFullPath(expanded) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
