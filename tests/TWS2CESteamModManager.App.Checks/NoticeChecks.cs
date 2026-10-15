using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TWS2CESteamModManager.App;
using TWS2CESteamModManager.App.Services;

internal static class NoticeChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks++;
            Console.WriteLine("PASS " + description);
        }

        string directory = Path.GetFullPath(Path.Combine(".test-results", "notice-" + Guid.NewGuid().ToString("N")));
        string path = Path.Combine(directory, "config.json");
        var preferences = new NoticePreferencesStore(path);
        Check(preferences.ShouldShow() && !Directory.Exists(directory), "missing notice preferences show the notice without creating files");

        var preview = new StartupNoticeWindow(preferences);
        await RenderAsync(preview);
        preview.Close();

        var declined = new StartupNoticeWindow(preferences);
        bool? decision = Decide(declined, () =>
        {
            Find<CheckBox>(declined, "DoNotShowAgain").IsChecked = true;
            Click(declined, "DeclineButton");
        });
        Check(decision == false && preferences.ShouldShow() && !File.Exists(path), "declining never remembers agreement even with the checkbox selected");

        var closed = new StartupNoticeWindow(preferences);
        Check(Decide(closed, closed.Close) != true && !File.Exists(path), "closing the notice does not grant or save agreement");

        var agreed = new StartupNoticeWindow(preferences);
        Check(Decide(agreed, () => Click(agreed, "AgreeButton")) == true && preferences.ShouldShow() && !File.Exists(path),
            "agreement without the checkbox continues and shows the notice next time");

        var remembered = new StartupNoticeWindow(preferences);
        decision = Decide(remembered, () =>
        {
            Find<CheckBox>(remembered, "DoNotShowAgain").IsChecked = true;
            Click(remembered, "AgreeButton");
        });
        Check(decision == true && !new NoticePreferencesStore(path).ShouldShow(), "explicit agreement with the checkbox survives reopening");
        Check(Directory.GetFiles(directory).Select(Path.GetFileName).SequenceEqual(new[] { "config.json" }), "saving notice preferences leaves no temporary files");

        File.WriteAllText(path, "{broken");
        Check(preferences.ShouldShow() && File.ReadAllText(path) == "{broken", "corrupt preferences show the notice without silently rewriting them");
        File.WriteAllText(path, "null");
        Check(preferences.ShouldShow(), "JSON null cannot imply agreement");
        File.WriteAllText(path, "{\"acceptedNoticeVersion\":0}");
        Check(preferences.ShouldShow(), "agreement to an older notice does not suppress the current version");
        File.WriteAllText(path, "{\"acceptedNoticeVersion\":999}");
        Check(preferences.ShouldShow(), "an unknown notice version cannot suppress the current notice");

        string blocked = Path.Combine(directory, "blocked.json");
        Directory.CreateDirectory(blocked);
        var failed = new StartupNoticeWindow(new NoticePreferencesStore(blocked));
        decision = Decide(failed, () =>
        {
            Find<CheckBox>(failed, "DoNotShowAgain").IsChecked = true;
            Click(failed, "AgreeButton");
            Check(failed.IsVisible && failed.DialogResult is null
                && Find<TextBlock>(failed, "PreferenceError").Visibility == Visibility.Visible
                && Find<CheckBox>(failed, "DoNotShowAgain").IsChecked == false,
                "a failed preference save explains the error and keeps the notice open");
            Click(failed, "AgreeButton");
        });
        Check(decision == true && new NoticePreferencesStore(blocked).ShouldShow(), "the user can agree without saving after a preference write failure");
        File.WriteAllText(path, "{broken");
        var corrupt = new StartupNoticeWindow(preferences);
        decision = Decide(corrupt, () =>
        {
            Find<CheckBox>(corrupt, "DoNotShowAgain").IsChecked = true;
            Click(corrupt, "AgreeButton");
            Check(corrupt.IsVisible && corrupt.DialogResult is null && Find<CheckBox>(corrupt, "DoNotShowAgain").IsChecked == false,
                "corrupt shared config prevents remembering agreement without crashing the notice");
            Click(corrupt, "AgreeButton");
        });
        Check(decision == true && File.ReadAllText(path) == "{broken", "agreement can continue without overwriting corrupt shared settings");
        return checks;
    }

    private static T Find<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Click(Window window, string name)
    {
        Button button = Find<Button>(window, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static bool? Decide(Window window, Action interaction)
    {
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try { interaction(); }
            catch (Exception error) { failure = error; window.Close(); }
        });
        bool? result = window.ShowDialog();
        if (failure is not null) throw new InvalidOperationException("Notice interaction failed.", failure);
        return result;
    }

    internal static async Task RenderAsync(StartupNoticeWindow window, string name = "startup-notice")
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, double.PositiveInfinity));
        var size = new Size(window.Width, Math.Ceiling(content.DesiredSize.Height));
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = Path.Combine(AppContext.BaseDirectory, "visual-checks");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("Rendered " + path);
    }
}
