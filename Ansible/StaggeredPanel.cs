using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Ansible;

// Masonry layout: equal-width columns, each child placed under the shortest column so cards of
// different heights pack without the empty bands a row-based Grid leaves. Children are laid out in
// collection order, so visual order follows keyboard order.
public sealed partial class StaggeredPanel : Panel
{
    private int _columns = 1;

    public int Columns
    {
        get => _columns;
        set
        {
            var columns = Math.Max(1, value);
            if (columns == _columns) { return; }
            _columns = columns;
            InvalidateMeasure();
        }
    }

    public double ColumnSpacing { get; set; }
    public double RowSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var columnWidth = ColumnWidth(width);
        var heights = new double[_columns];
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) { continue; }
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            var column = Shortest(heights);
            heights[column] += (heights[column] > 0 ? RowSpacing : 0) + child.DesiredSize.Height;
        }
        return new Size(width, heights.Max());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = ColumnWidth(finalSize.Width);
        var heights = new double[_columns];
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) { continue; }
            var column = Shortest(heights);
            var top = heights[column] + (heights[column] > 0 ? RowSpacing : 0);
            child.Arrange(new Rect(column * (columnWidth + ColumnSpacing), top, columnWidth, child.DesiredSize.Height));
            heights[column] = top + child.DesiredSize.Height;
        }
        return new Size(finalSize.Width, heights.Max());
    }

    private double ColumnWidth(double width) =>
        Math.Max(0, (width - ColumnSpacing * (_columns - 1)) / _columns);

    private static int Shortest(double[] heights)
    {
        var shortest = 0;
        for (var index = 1; index < heights.Length; index++)
        {
            if (heights[index] < heights[shortest]) { shortest = index; }
        }
        return shortest;
    }
}
