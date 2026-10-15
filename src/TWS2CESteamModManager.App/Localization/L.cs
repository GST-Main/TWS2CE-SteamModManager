using System.Windows.Markup;

namespace TWS2CESteamModManager.App.Localization;

public static class L
{
    public static LocalizationService Current { get; private set; } = new();
    public static void Use(LocalizationService service) => Current = service ?? throw new ArgumentNullException(nameof(service));
    public static string Text(string key, params object?[] arguments) => Current.Text(key, arguments);
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L.Text(key);
}
