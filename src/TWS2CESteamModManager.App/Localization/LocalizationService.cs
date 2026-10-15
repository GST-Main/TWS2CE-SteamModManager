using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TWS2CESteamModManager.App.Services;

namespace TWS2CESteamModManager.App.Localization;

public sealed record LanguageOption(string Id, string DisplayName, CultureInfo Culture);

/// <summary>Loads editable, flat JSON catalogs with an embedded English fallback.</summary>
public sealed class LocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> BuiltIn = LoadBuiltIn();
    private readonly Dictionary<string, Dictionary<string, string>> catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Key, object?[] Arguments)> warnings = new();
    private readonly AppConfigStore config;

    public LocalizationService(string? directory = null, CultureInfo? uiCulture = null)
    {
        string root = Path.GetFullPath(directory ?? AppContext.BaseDirectory);
        config = new AppConfigStore(Path.Combine(root, "config.json"));
        catalogs["en"] = new Dictionary<string, string>(BuiltIn, StringComparer.Ordinal);
        string languagesDirectory = Path.Combine(root, "Languages");
        try
        {
            if (Directory.Exists(languagesDirectory))
                foreach (string file in Directory.EnumerateFiles(languagesDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                    LoadCatalog(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { warnings.Add(("msg.languageFileInvalid", ["Languages"])); }

        Languages = catalogs.Select(pair => new LanguageOption(pair.Key,
                pair.Value.GetValueOrDefault("language.name", pair.Key), GetCulture(pair.Value.GetValueOrDefault("language.culture", pair.Key))))
            .OrderBy(language => language.Id != "en").ThenBy(language => language.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        string selected = LoadPreference() ?? DetectLanguage(uiCulture ?? CultureInfo.CurrentUICulture).Id;
        ActiveLanguage = Languages.FirstOrDefault(language => string.Equals(language.Id, selected, StringComparison.OrdinalIgnoreCase))
            ?? Languages.First(language => language.Id == "en");
        if (!string.Equals(ActiveLanguage.Id, selected, StringComparison.OrdinalIgnoreCase))
            warnings.Add(("msg.languageUnavailable", [selected]));
    }

    public IReadOnlyList<LanguageOption> Languages { get; }
    public LanguageOption ActiveLanguage { get; }
    public string WarningText => string.Join(" ", warnings.Select(warning => Text(warning.Key, warning.Arguments)));

    public string Text(string key, params object?[] arguments)
    {
        string fallback = catalogs["en"].GetValueOrDefault(key) ?? BuiltIn.GetValueOrDefault(key) ?? $"[{key}]";
        string value = catalogs[ActiveLanguage.Id].GetValueOrDefault(key) ?? fallback;
        if (arguments.Length == 0) return value;
        try { return string.Format(ActiveLanguage.Culture, value, arguments); }
        catch (FormatException)
        {
            try { return string.Format(ActiveLanguage.Culture, fallback, arguments); }
            catch (FormatException) { return fallback; }
        }
    }

    public void ApplyCulture()
    {
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = ActiveLanguage.Culture;
        CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = ActiveLanguage.Culture;
    }

    /// <summary>Persists the choice for the next launch; the active catalog stays stable.</summary>
    public void SaveSelection(string id)
    {
        LanguageOption selection = Languages.FirstOrDefault(language => string.Equals(language.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unknown language.", nameof(id));
        config.Update(current => current with { Language = selection.Id });
    }

    private LanguageOption DetectLanguage(CultureInfo uiCulture)
    {
        var cultures = new List<CultureInfo>();
        for (CultureInfo culture = uiCulture; culture.Name.Length > 0; culture = culture.Parent)
            cultures.Add(culture);

        // Prefer the exact culture, then its parents (for example, ko-KR -> ko).
        foreach (CultureInfo culture in cultures)
        {
            LanguageOption? match = Languages.FirstOrDefault(language => language.Culture.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        // A regional catalog also serves its neutral language (ko -> ko-KR).
        foreach (CultureInfo culture in cultures)
            foreach (LanguageOption language in Languages.OrderBy(language => language.Id, StringComparer.OrdinalIgnoreCase))
                for (CultureInfo parent = language.Culture.Parent; parent.Name.Length > 0; parent = parent.Parent)
                    if (parent.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)) return language;

        return Languages.First(language => language.Id == "en");
    }

    private string? LoadPreference()
    {
        try
        {
            return config.Load().Language;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        { warnings.Add(("msg.languageSettingsInvalid", [])); return "en"; }
    }

    private void LoadCatalog(string file)
    {
        string id = Path.GetFileNameWithoutExtension(file);
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$")) return;
        try
        {
            using JsonDocument document = ReadJson(file);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a JSON object.");
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate translation key.");
                if (property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Translations must be strings.");
                string value = property.Value.GetString()!;
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (BuiltIn.TryGetValue(property.Name, out string? original) && !ValidFormat(original, value))
                { warnings.Add(("msg.languageEntryInvalid", [property.Name, Path.GetFileName(file)])); continue; }
                entries[property.Name] = value;
            }
            if (id.Equals("en", StringComparison.OrdinalIgnoreCase))
                foreach (var entry in entries) catalogs["en"][entry.Key] = entry.Value;
            else catalogs[id] = entries;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { warnings.Add(("msg.languageFileInvalid", [Path.GetFileName(file)])); }
    }

    private static bool ValidFormat(string original, string translated)
    {
        try
        {
            if (CompositeFormat.Parse(original).MinimumArgumentCount == 0) return true;
            CompositeFormat.Parse(translated);
            static IEnumerable<string> Arguments(string text) => Regex.Matches(text, @"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})")
                .Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal);
            return Arguments(original).SequenceEqual(Arguments(translated));
        }
        catch (FormatException) { return false; }
    }

    private static JsonDocument ReadJson(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Language files must be at most 1 MiB.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    private static CultureInfo GetCulture(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("en"); }
    }

    private static IReadOnlyDictionary<string, string> LoadBuiltIn()
    {
        using Stream stream = typeof(LocalizationService).Assembly.GetManifestResourceStream("Languages.en.json")
            ?? throw new InvalidOperationException("Embedded English translations are missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
