using TWS2CESteamModManager.App.Localization;
using System.IO;
using System.Windows;
using TWS2CESteamModManager.App.Services;

namespace TWS2CESteamModManager.App;

public partial class StartupNoticeWindow : Window
{
    private readonly NoticePreferencesStore preferences;

    public StartupNoticeWindow(NoticePreferencesStore preferences)
    {
        this.preferences = preferences;
        InitializeComponent();
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 48);
        MaxWidth = Math.Max(300, SystemParameters.WorkArea.Width - 48);
        Loaded += (_, _) => DeclineButton.Focus();
    }

    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Agree_Click(object sender, RoutedEventArgs e)
    {
        if (DoNotShowAgain.IsChecked == true)
        {
            try { preferences.RememberAgreement(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                DoNotShowAgain.IsChecked = false;
                PreferenceError.Text = L.Text("notice.preferenceFailed");
                PreferenceError.Visibility = Visibility.Visible;
                return;
            }
        }
        DialogResult = true;
    }
}
