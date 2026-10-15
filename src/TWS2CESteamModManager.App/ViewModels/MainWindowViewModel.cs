using TWS2CESteamModManager.App.Localization;
using System.ComponentModel;
using TWS2CESteamModManager.App.Services;

namespace TWS2CESteamModManager.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private bool isModsSelected;
    private bool closing;
    private string launchError = "";
    private readonly IApplicationLauncher launcher;
    private readonly LocalizationService localization = L.Current;
    private LanguageOption selectedLanguage;
    private string languageMessage;
    public MainWindowViewModel(WorkshopBrowserViewModel browser, ModManagerViewModel? mods = null, IApplicationLauncher? launcher = null)
    {
        Browser = browser;
        Mods = mods;
        this.launcher = launcher ?? new ApplicationLauncher();
        selectedLanguage = localization.ActiveLanguage;
        languageMessage = localization.WarningText;
        isModsSelected = mods is not null;
        ShowModsCommand = new(() => IsModsSelected = true);
        ShowBrowseCommand = new(() => IsModsSelected = false);
        LaunchCommand = new(LaunchGame, () => CanLaunch);
        if (mods is not null) mods.PropertyChanged += ModsChanged;
    }
    public WorkshopBrowserViewModel Browser { get; }
    public ModManagerViewModel? Mods { get; }
    public DelegateCommand CloseDetailsCommand => Browser.CloseDetailsCommand;
    public DelegateCommand ShowModsCommand { get; }
    public DelegateCommand ShowBrowseCommand { get; }
    public DelegateCommand LaunchCommand { get; }
    public bool CanLaunch => !closing && Mods is { CanManage: true };
    public string LaunchHint => Mods is { IsBusy: true } ? L.Text("tip.waitForMods")
        : CanLaunch ? L.Text("tip.launchGame") : L.Text("tip.setupBeforeLaunch");
    public string LaunchError { get => launchError; private set { launchError = value; Notify(); Notify(nameof(TopMessage)); } }
    public IReadOnlyList<LanguageOption> Languages => localization.Languages;
    public string LanguageMessage => languageMessage;
    public string TopMessage => LaunchError.Length > 0 ? LaunchError : LanguageMessage;
    public LanguageOption SelectedLanguage
    {
        get => selectedLanguage;
        set
        {
            if (value is null || value.Id == selectedLanguage.Id || closing) return;
            try
            {
                localization.SaveSelection(value.Id);
                selectedLanguage = value;
                languageMessage = value.Id == localization.ActiveLanguage.Id ? localization.WarningText : L.Text("msg.restartLanguage");
            }
            catch (Exception exception) { languageMessage = L.Text("msg.languageSaveFailed", exception.Message); }
            Notify(); Notify(nameof(LanguageMessage)); Notify(nameof(TopMessage));
        }
    }

    private void LaunchGame()
    {
        LaunchError = "";
        try { launcher.LaunchGame(Mods!.GameInstallPath); }
        catch (Exception exception) { LaunchError = L.Text("msg.launchGameFailed", exception.Message); }
    }

    private void ModsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModManagerViewModel.CanManage) or nameof(ModManagerViewModel.GameInstallPath))
            RefreshLaunch();
    }

    private void RefreshLaunch()
    {
        Notify(nameof(CanLaunch));
        Notify(nameof(LaunchHint));
        LaunchCommand.Refresh();
    }
    public bool IsModsSelected
    {
        get => isModsSelected;
        set { Browser.CloseDetails(); isModsSelected = value; Notify(); Notify(nameof(IsBrowseSelected)); }
    }
    public bool IsBrowseSelected => !IsModsSelected;
    public Task OpenInstalledDetailsAsync(InstalledModViewModel mod)
    {
        if (Mods is not { CanManage: true } || Mods.Items.FirstOrDefault(item => item.Id == mod.Id) is not { } current)
            return Task.CompletedTask;
        Mods.SelectedMod = current;
        return Browser.OpenInstalledDetailsAsync(current);
    }
    public Task InitializeAsync() => Task.WhenAll(Browser.InitializeAsync(), Mods?.InitializeAsync() ?? Task.CompletedTask);
    public async Task ShutdownAsync()
    {
        closing = true;
        RefreshLaunch();
        if (Mods is not null) Mods.PropertyChanged -= ModsChanged;
        if (Mods is not null) await Mods.ShutdownAsync();
        Browser.Dispose();
    }
}
