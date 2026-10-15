using System.Windows;
using System.Windows.Controls;

namespace TWS2CESteamModManager.App.Controls;

public sealed class WorkshopTilePanel : Panel
{
    private const double Gap = 18, MinimumWidth = 220;
    private int columns = 1;
    private double itemWidth;
    private readonly List<double> rowHeights = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 1100 : Math.Max(1, availableSize.Width);
        columns = Math.Max(1, (int)((width + Gap) / (MinimumWidth + Gap)));
        itemWidth = Math.Max(1, (width - (columns - 1) * Gap) / columns);
        rowHeights.Clear();
        for (int index = 0; index < InternalChildren.Count; index++)
        {
            UIElement child = InternalChildren[index];
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            int row = index / columns;
            if (row == rowHeights.Count) rowHeights.Add(0);
            rowHeights[row] = Math.Max(rowHeights[row], child.DesiredSize.Height);
        }
        return new Size(width, rowHeights.Sum() + Math.Max(0, rowHeights.Count - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double top = 0;
        for (int row = 0; row < rowHeights.Count; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                if (index < InternalChildren.Count)
                    InternalChildren[index].Arrange(new Rect(column * (itemWidth + Gap), top, itemWidth, rowHeights[row]));
            }
            top += rowHeights[row] + Gap;
        }
        return finalSize;
    }
}
