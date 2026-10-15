using System.Net;
using System.Text.RegularExpressions;

namespace TWS2CESteamModManager.App.Services;

public static class SteamDescription
{
    // Workshop descriptions are BBCode, never executable HTML or WPF markup.
    public static string ToPlainText(string value)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        TimeSpan timeout = TimeSpan.FromSeconds(1);
        string text = Regex.Replace(value, @"\[url=([^\]\r\n]+)\](.*?)\[/url\]", "$2 ($1)", options | RegexOptions.Singleline, timeout);
        text = Regex.Replace(text, @"\[\*\]", "\n• ", options, timeout);
        text = Regex.Replace(text, @"\[/?(?:h[1-6]|list|olist|quote|table|tr)(?:=[^\]\r\n]*)?\]", "\n", options, timeout);
        text = Regex.Replace(text, @"\[/?(?:b|i|u|s|strike|spoiler|url|img|previewimg|code|noparse|td|th)(?:=[^\]\r\n]*)?\]", "", options, timeout);
        return WebUtility.HtmlDecode(text).Trim();
    }
}
