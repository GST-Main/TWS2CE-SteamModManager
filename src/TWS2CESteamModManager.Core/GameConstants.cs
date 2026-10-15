namespace TWS2CESteamModManager.Core;

public static class GameConstants
{
    public const uint GameAppId = 3846830;
    public const uint WorkshopAppId = 34330;
    public const string GameExecutable = "shogun2_complete_edition.exe";

    public static string GetDefaultAppDataPath(string roamingAppDataPath) =>
        Path.Combine(roamingAppDataPath, "The Creative Assembly", "Shogun2CompleteEdition");

    public static string GetUserScriptPath(string appDataPath) =>
        Path.Combine(appDataPath, "Steam", "scripts", "user.script.txt");
}
