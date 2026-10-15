using System.Windows;
using System.Windows.Controls;

namespace TWS2CESteamModManager.App.Controls;

/// <summary>Sizes a screenshot area to 16:9 at the available width.</summary>
public sealed class WidescreenPreview : Decorator
{
    private const double AspectRatio = 16.0 / 9.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var size = new Size(width, width / AspectRatio);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = Math.Min(finalSize.Width, finalSize.Height * AspectRatio);
        double height = width / AspectRatio;
        Child?.Arrange(new Rect((finalSize.Width - width) / 2, (finalSize.Height - height) / 2, width, height));
        return finalSize;
    }
}
