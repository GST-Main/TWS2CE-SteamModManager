using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TWS2CESteamModManager.App.Services;
using TWS2CESteamModManager.App.ViewModels;
using TWS2CESteamModManager.Steam;

namespace TWS2CESteamModManager.App;

public partial class MainWindow : Window
{
    public WorkshopBrowserViewModel ViewModel { get; }
    public MainWindowViewModel Shell { get; }
    private IInputElement? previousFocus;
    private bool closeAfterShutdown, shuttingDown;

    public MainWindow() : this(false) { }
    public MainWindow(bool forceFolderSetup) : this(CreateShell(forceFolderSetup)) { }

    private static MainWindowViewModel CreateShell(bool forceFolderSetup)
    {
        var steam = new SteamWorkshopBrowserService();
        var launcher = new ApplicationLauncher();
        var mods = new ModManagerViewModel(new ModManagementService(steam, forceFolderSetup: forceFolderSetup));
        return new MainWindowViewModel(new WorkshopBrowserViewModel(steam, new ThumbnailLoader(), mods, launcher), mods, launcher);
    }

    public MainWindow(WorkshopBrowserViewModel viewModel) : this(new MainWindowViewModel(viewModel)) { }

    public MainWindow(MainWindowViewModel shell)
    {
        InitializeComponent();
        Shell = shell;
        ViewModel = shell.Browser;
        DataContext = shell;
        Loaded += async (_, _) => await shell.InitializeAsync();
        ViewModel.PropertyChanged += ViewModelChanged;
        Closing += async (_, e) =>
        {
            if (closeAfterShutdown || Shell.Mods is null) return;
            e.Cancel = true;
            if (shuttingDown) return;
            shuttingDown = true;
            await shell.ShutdownAsync();
            closeAfterShutdown = true;
            _ = Dispatcher.BeginInvoke(Close);
        };
        Closed += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModelChanged;
            ViewModel.Dispose();
        };
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkshopBrowserViewModel.Items)) ResultsScroll.ScrollToTop();
        if (e.PropertyName == nameof(WorkshopBrowserViewModel.IsDetailOpen))
        {
            if (ViewModel.IsDetailOpen)
            {
                previousFocus = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (ViewModel.IsDetailOpen) DetailView.FocusCloseButton(); });
            }
            else Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (!ViewModel.IsDetailOpen) previousFocus?.Focus(); });
        }
    }

    private async void WorkshopItem_Click(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (sender is Button { Tag: WorkshopCardViewModel item }) await ViewModel.OpenDetailsAsync(item);
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e) => e.Handled = true;

    private void DetailBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => ViewModel.CloseDetails();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel.IsDetailOpen) { ViewModel.CloseDetails(); e.Handled = true; }
    }
}
