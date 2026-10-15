using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TWS2CESteamModManager.App;
using TWS2CESteamModManager.App.Localization;
using TWS2CESteamModManager.App.Services;
using TWS2CESteamModManager.App.ViewModels;
using TWS2CESteamModManager.Core;
using TWS2CESteamModManager.Steam;

internal static class LocalizationChecks
{
    public static async Task<int> RunAsync(Func<MainWindow, string, double, double, Task> render)
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks++;
            Console.WriteLine("PASS " + description);
        }
        string root = Path.GetFullPath(Path.Combine(".test-results", "localization-" + Guid.NewGuid().ToString("N")));
        var empty = new LocalizationService(root);
        Check(empty.Text("tab.mods") == "Mods" && empty.Languages.Count == 1 && !Directory.Exists(root),
            "embedded English works without language files or file-system side effects");
        Check(empty.Text("label.page", 1, 3) == "Page 1 of 3", "built-in messages format their arguments");
        var updatedAt = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        Check(empty.Text("label.updated", updatedAt) == "Updated 15 Oct 2026", "English dates retain their existing format");
        string shippedRoot = Path.Combine(root, "shipped"), shippedLanguages = Path.Combine(shippedRoot, "Languages");
        Directory.CreateDirectory(shippedLanguages);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Languages", "ko.json"), Path.Combine(shippedLanguages, "ko.json"));
        var autoKorean = new LocalizationService(shippedRoot, CultureInfo.GetCultureInfo("ko-KR"));
        Check(autoKorean.ActiveLanguage.Id == "ko" && autoKorean.WarningText.Length == 0 && !File.Exists(Path.Combine(shippedRoot, "config.json")),
            "first launch detects Korean UI culture through catalog metadata without saving a preference");
        Check(new LocalizationService(shippedRoot, CultureInfo.GetCultureInfo("ko")).ActiveLanguage.Id == "ko",
            "neutral Korean UI culture can select the regional ko-KR catalog");
        Check(new LocalizationService(shippedRoot, CultureInfo.GetCultureInfo("fr-FR")).ActiveLanguage.Id == "en",
            "an unsupported Windows UI language falls back to English");
        autoKorean.SaveSelection("en");
        Check(new LocalizationService(shippedRoot, CultureInfo.GetCultureInfo("ko-KR")).ActiveLanguage.Id == "en",
            "a saved English choice overrides Korean language detection");
        new LocalizationService(shippedRoot).SaveSelection("ko");
        var shippedKorean = new LocalizationService(shippedRoot, CultureInfo.GetCultureInfo("en-US"));
        Check(shippedKorean.ActiveLanguage.Culture.Name == "ko-KR"
            && shippedKorean.Text("label.updated", updatedAt) == "업데이트: 2026년 10월 15일",
            "the copied Korean catalog uses South Korean culture and year-month-day dates");
        using (Stream stream = typeof(LocalizationService).Assembly.GetManifestResourceStream("Languages.en.json")!)
        {
            var english = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            Check(english.Count > 150 && english.Keys.All(key => Regex.IsMatch(key, @"^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)+$")),
                "the complete English template uses dotted translation keys");
            Check(english.Values.All(value => CompositeFormat.Parse(value) is not null), "all built-in message formats are valid");
        }

        string languages = Path.Combine(root, "Languages"); Directory.CreateDirectory(languages);
        var korean = new Dictionary<string, string>
        {
            ["language.name"] = "한국어 (사용자 번역)", ["language.culture"] = "ko",
            ["tab.mods"] = "모드", ["tab.browse"] = "창작마당", ["action.launchGame"] = "게임 실행",
            ["label.language"] = "언어", ["label.installedMods"] = "설치된 모드", ["label.gameFolders"] = "게임 폴더",
            ["label.appDataFolder"] = "앱 데이터 폴더", ["label.gameInstallation"] = "게임 설치 폴더",
            ["action.browseFolder"] = "찾아보기…", ["action.saveFolders"] = "폴더 저장 후 모드 불러오기",
            ["action.import"] = "모드 가져오기", ["action.moveUp"] = "위로", ["action.moveDown"] = "아래로",
            ["action.delete"] = "삭제", ["action.folders"] = "폴더 설정", ["action.reloadMods"] = "다시 불러오기",
            ["action.launchSteam"] = "Steam 실행", ["action.retry"] = "다시 시도",
            ["msg.selectFolders"] = "모드를 관리하려면 게임 폴더를 선택하세요.",
            ["msg.confirmFolders"] = "감지된 경로를 확인하거나 기존 폴더를 선택하세요.",
            ["msg.steamRequired"] = "창작마당을 탐색하려면 Steam이 실행 중이어야 합니다",
            ["msg.launchSteamThenRetry"] = "Steam을 실행하고 로그인한 뒤 다시 시도하세요.",
            ["msg.invalidAppDataFolder"] = "기존 앱 데이터 폴더를 선택하세요.",
            ["label.page"] = "전체 {1:N0}쪽 중 {0:N0}쪽", ["label.modSummary"] = "모드 {0:N0}개 · 활성 {1:N0}개",
            ["label.imported"] = "가져옴", ["label.workshop"] = "창작마당", ["label.enabled"] = "활성화",
            ["label.missingFile"] = "파일 없음", ["label.noWorkshopId"] = "없음 (로컬 모드)",
            ["msg.localDescription"] = "직접 가져온 모드입니다. 연결된 창작마당 설명이 없습니다.",
            ["notice.title"] = "모드 관리자 사용 전 확인", ["notice.subtitle"] = "모드 호환성과 위험 안내",
            ["notice.doNotShowAgain"] = "다시 표시하지 않기", ["notice.agree"] = "동의합니다", ["notice.decline"] = "동의하지 않습니다"
        };
        string koreanPath = Path.Combine(languages, "ko.json");
        File.WriteAllText(koreanPath, JsonSerializer.Serialize(korean), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(Path.Combine(languages, "broken.json"), "{broken");
        File.WriteAllText(Path.Combine(languages, "duplicate.json"), "{\"tab.mods\":\"A\",\"tab.mods\":\"B\"}");
        File.WriteAllText(Path.Combine(languages, "nested.json"), "{\"tab\":{\"mods\":\"A\"}}");
        File.WriteAllText(Path.Combine(languages, "null.json"), "null");
        File.WriteAllText(Path.Combine(languages, "oversize.json"), new string(' ', 1024 * 1024 + 1));
        Check(new LocalizationService(root, CultureInfo.GetCultureInfo("ko-KR")).ActiveLanguage.Id == "ko",
            "a regional Windows UI language can use a neutral Korean catalog");
        File.WriteAllText(Path.Combine(languages, "custom-korean.json"), "{\"language.name\":\"Regional Korean\",\"language.culture\":\"ko-KR\"}");
        Check(new LocalizationService(root, CultureInfo.GetCultureInfo("ko-KR")).ActiveLanguage.Id == "custom-korean",
            "an exact regional culture takes priority over a neutral catalog regardless of filename");
        File.Delete(Path.Combine(languages, "custom-korean.json"));
        var discovered = new LocalizationService(root, CultureInfo.GetCultureInfo("en-US"));
        Check(discovered.Languages.Count == 2 && discovered.Languages.Any(language => language.DisplayName == korean["language.name"]),
            "valid UTF-8 BOM language files are discovered while malformed catalogs are skipped");
        Check(discovered.WarningText.Contains("broken.json") && discovered.WarningText.Contains("duplicate.json")
            && discovered.WarningText.Contains("nested.json") && discovered.WarningText.Contains("oversize.json")
            && discovered.WarningText.Contains("null.json"), "invalid language files produce recoverable warnings");
        discovered.SaveSelection("ko");
        Check(discovered.ActiveLanguage.Id == "en" && discovered.Text("tab.mods") == "Mods", "saving a language does not mix languages during the current session");
        var selected = new LocalizationService(root);
        Check(selected.ActiveLanguage.Id == "ko" && selected.Text("tab.mods") == "모드", "the saved user language loads on restart");
        Check(selected.Text("action.download") == "Download", "missing translations fall back to English per key");
        Check(selected.Text("label.page", 2, 7) == "전체 7쪽 중 2쪽", "translations can reorder numbered placeholders");

        korean["label.page"] = "잘못된 {0";
        korean["label.modSummary"] = "모드 {0:N0}개";
        korean["action.download"] = "";
        File.WriteAllText(koreanPath, JsonSerializer.Serialize(korean));
        var badFormat = new LocalizationService(root);
        Check(badFormat.Text("label.page", 2, 7) == "Page 2 of 7"
            && badFormat.Text("label.modSummary", 3, 2) == "3 mods · 2 enabled"
            && badFormat.Text("action.download") == "Download", "invalid formats, removed placeholders and empty values fall back safely");
        Check(badFormat.WarningText.Contains("label.page") && badFormat.WarningText.Contains("label.modSummary"), "bad translations identify the affected keys");
        korean["label.page"] = "전체 {1:N0}쪽 중 {0:N0}쪽";
        korean["label.modSummary"] = "모드 {0:N0}개 · 활성 {1:N0}개";

        File.WriteAllText(Path.Combine(root, "config.json"), "{\"language\":\"missing\"}");
        var unavailable = new LocalizationService(root);
        Check(unavailable.ActiveLanguage.Id == "en" && unavailable.WarningText.Contains("missing"), "an unavailable saved language uses English with an explanation");
        File.WriteAllText(Path.Combine(root, "config.json"), "{broken");
        Check(new LocalizationService(root).ActiveLanguage.Id == "en" && File.ReadAllText(Path.Combine(root, "config.json")) == "{broken",
            "corrupt preferences fall back without overwriting the original file");
        File.WriteAllText(Path.Combine(root, "config.json"), "{\"language\":\"en\"}");
        File.WriteAllText(Path.Combine(languages, "en.json"), "{\"tab.mods\":\"Custom Mods\"}");
        var editedEnglish = new LocalizationService(root);
        Check(editedEnglish.Text("tab.mods") == "Custom Mods" && editedEnglish.Text("tab.browse") == "Browse", "editing external English overrides only supplied entries");
        try { editedEnglish.SaveSelection("../outside"); throw new InvalidOperationException("Invalid language ID accepted."); }
        catch (ArgumentException) { Check(File.ReadAllText(Path.Combine(root, "config.json")).Contains("en"), "language selection cannot escape the catalog directory"); }
        Check(!Directory.GetFiles(root).Any(file => file.EndsWith(".tmp")), "language preference saves leave no temporary files");

        var previous = L.Current;
        CultureInfo previousCulture = CultureInfo.CurrentCulture, previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo? previousDefault = CultureInfo.DefaultThreadCurrentCulture, previousDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            string uiRoot = Path.Combine(root, "ui"), uiLanguages = Path.Combine(uiRoot, "Languages");
            Directory.CreateDirectory(uiLanguages);
            File.WriteAllText(Path.Combine(uiLanguages, "ko.json"), JsonSerializer.Serialize(korean));
            var englishSession = new LocalizationService(uiRoot, CultureInfo.GetCultureInfo("en-US"));
            L.Use(englishSession);
            var selectionShell = new MainWindowViewModel(new WorkshopBrowserViewModel(new StoppedSteam(), new NoImages()));
            selectionShell.SelectedLanguage = selectionShell.Languages.Single(language => language.Id == "ko");
            Check(selectionShell.SelectedLanguage.Id == "ko" && selectionShell.LanguageMessage.Contains("Restart")
                && L.Text("tab.mods") == "Mods", "the language selector saves the preference and explains the required restart");
            await selectionShell.ShutdownAsync();

            L.Use(new LocalizationService(uiRoot));
            L.Current.ApplyCulture();
            Check(CultureInfo.CurrentCulture.Name == "ko" && CultureInfo.DefaultThreadCurrentUICulture!.Name == "ko", "selected culture applies to UI and background formatting");
            var mods = new ModManagerViewModel(new ModManagementService(new NoInstaller(), Path.Combine(uiRoot, "storage"), forceFolderSetup: true));
            var browser = new WorkshopBrowserViewModel(new StoppedSteam(), new NoImages(), mods);
            var shell = new MainWindowViewModel(browser, mods);
            await shell.InitializeAsync();
            var window = new MainWindow(shell);
            Check((string)((Button)window.FindName("ModsTab")).Content == "모드"
                && (string)((Button)window.FindName("BrowseTab")).Content == "창작마당"
                && (string)((Button)window.FindName("LaunchGameButton")).Content == "게임 실행", "XAML labels use the external catalog");
            await render(window, "localization-mods", 1080, 700);
            Check(((ComboBox)window.FindName("LanguageCombo")).SelectedItem is LanguageOption { Id: "ko" }, "the language selector reflects the loaded preference");
            Check(Descendants<TextBlock>((ComboBox)window.FindName("LanguageCombo")).Any(text => text.Text == korean["language.name"]),
                "the themed selector displays the language name rather than the record type");
            await mods.ApplyFoldersAsync();
            Check(mods.Error == korean["msg.invalidAppDataFolder"], "folder validation errors are translated");
            shell.IsModsSelected = false;
            Check(browser.ErrorHeading == korean["msg.steamRequired"] && browser.Error == korean["msg.launchSteamThenRetry"], "Steam recovery messages use the external catalog");
            await render(window, "localization-workshop", 1080, 700);
            var local = new InstalledModViewModel(new InstalledModSnapshot(new ManagedMod
            { Id = Guid.NewGuid(), FileName = "creator.pack", OriginalFileName = "creator.pack", DisplayName = "Creator title", Source = ModSource.Imported }, false, null), (_, _) => Task.CompletedTask);
            Check(local.Name == "Creator title" && local.FileName == "creator.pack" && local.Source == "가져옴" && local.Status == "파일 없음",
                "mod metadata stays intact while app-owned source and status labels translate");
            await browser.OpenInstalledDetailsAsync(local);
            Check(browser.Detail!.Description == korean["msg.localDescription"], "local item detail descriptions are translated");
            browser.CloseDetails();
            var notice = new StartupNoticeWindow(new NoticePreferencesStore(Path.Combine(uiRoot, "config.json")));
            Check(notice.Title == korean["notice.title"] && (string)((Button)notice.FindName("AgreeButton")).Content == "동의합니다",
                "the selected language is available to the startup notice");
            await NoticeChecks.RenderAsync(notice, "localization-notice");
            Descendants<TextBlock>((DependencyObject)notice.Content).First(text => text.Text == L.Text("notice.risks")).Text =
                string.Join(" ", Enumerable.Repeat(L.Text("notice.risks"), 20));
            var noticeRoot = (FrameworkElement)notice.Content;
            noticeRoot.Measure(new Size(660, 450)); noticeRoot.Arrange(new Rect(0, 0, 660, 450)); noticeRoot.UpdateLayout();
            var noticeScroll = (ScrollViewer)notice.FindName("NoticeScroll");
            var agreeButton = (Button)notice.FindName("AgreeButton");
            Check(noticeScroll.ExtentHeight > noticeScroll.ViewportHeight
                && agreeButton.TranslatePoint(new Point(), noticeRoot).Y + agreeButton.ActualHeight <= 450,
                "long translated notices scroll while agreement buttons remain visible");
            notice.Close();
            await shell.ShutdownAsync(); window.Close();

            string blockedRoot = Path.Combine(root, "blocked");
            Directory.CreateDirectory(Path.Combine(blockedRoot, "Languages"));
            File.Copy(Path.Combine(uiLanguages, "ko.json"), Path.Combine(blockedRoot, "Languages", "ko.json"));
            Directory.CreateDirectory(Path.Combine(blockedRoot, "config.json"));
            L.Use(new LocalizationService(blockedRoot, CultureInfo.GetCultureInfo("en-US")));
            var blockedShell = new MainWindowViewModel(new WorkshopBrowserViewModel(new StoppedSteam(), new NoImages()));
            blockedShell.SelectedLanguage = blockedShell.Languages.Single(language => language.Id == "ko");
            Check(blockedShell.SelectedLanguage.Id == "en" && blockedShell.LanguageMessage.Contains("Couldn't save"), "a failed language save retains the selection and reports the error");
            await blockedShell.ShutdownAsync();
        }
        finally
        {
            L.Use(previous);
            CultureInfo.CurrentCulture = previousCulture; CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = previousDefault; CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUi;
        }
        return checks;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class NoImages : IThumbnailLoader
    {
        public Task<ImageSource?> LoadAsync(string? url, CancellationToken cancellationToken) => Task.FromResult<ImageSource?>(null);
    }
    private sealed class StoppedSteam : IWorkshopBrowserService
    {
        public Task<IReadOnlyList<WorkshopTag>> FetchTagsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkshopTag>>([]);
        public Task<WorkshopPage> FetchItemsAsync(WorkshopOptions options, CancellationToken cancellationToken = default) => Task.FromException<WorkshopPage>(new SteamNotRunningException());
        public Task<WorkshopItemDetails> FetchDetailsAsync(ulong itemId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Not expected for local details.");
        public void Dispose() { }
    }
    private sealed class NoInstaller : IWorkshopModInstaller
    {
        public Task<IReadOnlyList<ManagedMod>> InstallAsync(ModManager manager, ulong itemId, Action<string>? report = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Not expected during localization checks.");
    }
}
