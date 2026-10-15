using System.Windows;
using System.Windows.Controls;

namespace TWS2CESteamModManager.App.Controls;

/// <summary>Sizes a thumbnail to the available card width during layout.</summary>
public sealed class SquarePreview : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double edge = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var square = new Size(edge, edge);
        Child?.Measure(square);
        return square;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double edge = Math.Min(finalSize.Width, finalSize.Height);
        Child?.Arrange(new Rect(0, 0, edge, edge));
        return finalSize;
    }
}
