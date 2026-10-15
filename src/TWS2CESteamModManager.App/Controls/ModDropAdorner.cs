using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TWS2CESteamModManager.App.Controls;

internal sealed class ModDropAdorner(UIElement element) : Adorner(element)
{
    public double PositionY { get; set; }
    protected override void OnRender(DrawingContext drawingContext)
    {
        var brush = new SolidColorBrush(Color.FromRgb(217, 183, 123));
        drawingContext.DrawLine(new Pen(brush, 2), new Point(0, PositionY), new Point(AdornedElement.RenderSize.Width, PositionY));
    }
}
