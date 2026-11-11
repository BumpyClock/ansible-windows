using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Ansible;

/// <summary>
/// Lays cards out in equal-width columns, left to right and then down. The column count is the most columns of at
/// least MinColumnWidth that fit, never more than the number of cards. The default MinColumnWidth is infinite,
/// which stacks the cards in one column. Each row is as tall as its tallest card; a card's own VerticalAlignment
/// decides whether it fills that height (Stretch) or keeps its natural height (Top).
/// </summary>
public sealed partial class CardFlowPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(CardFlowPanel), new PropertyMetadata(double.PositiveInfinity, OnLayoutChanged));
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(CardFlowPanel), new PropertyMetadata(0.0, OnLayoutChanged));
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(CardFlowPanel), new PropertyMetadata(0.0, OnLayoutChanged));

    private int _columns = 1;

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double ColumnSpacing { get => (double)GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => (double)GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

    private static void OnLayoutChanged(DependencyObject panel, DependencyPropertyChangedEventArgs args) =>
        ((CardFlowPanel)panel).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        _columns = ColumnsFor(availableSize.Width);
        var columnWidth = ColumnWidth(availableSize.Width, _columns);
        foreach (var child in Children) child.Measure(new Size(columnWidth, double.PositiveInfinity));
        var rows = RowHeights();
        var width = double.IsInfinity(availableSize.Width)
            ? Children.Select(child => child.DesiredSize.Width).DefaultIfEmpty(0).Max()
            : availableSize.Width;
        return new Size(width, rows.Sum() + RowSpacing * Math.Max(0, rows.Count - 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = ColumnWidth(finalSize.Width, _columns);
        var rows = RowHeights();
        var y = 0.0;
        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < _columns && row * _columns + column < Children.Count; column++)
                Children[row * _columns + column].Arrange(
                    new Rect(column * (columnWidth + ColumnSpacing), y, columnWidth, rows[row]));
            y += rows[row] + RowSpacing;
        }
        return finalSize;
    }

    private int ColumnsFor(double width)
    {
        if (Children.Count == 0 || double.IsInfinity(width) || double.IsInfinity(MinColumnWidth)) return 1;
        return Math.Clamp((int)((width + ColumnSpacing) / (MinColumnWidth + ColumnSpacing)), 1, Children.Count);
    }

    private double ColumnWidth(double width, int columns) =>
        double.IsInfinity(width) ? double.PositiveInfinity : (width - ColumnSpacing * (columns - 1)) / columns;

    private List<double> RowHeights()
    {
        var rows = new List<double>();
        for (var index = 0; index < Children.Count; index++)
        {
            if (index % _columns == 0) rows.Add(0);
            rows[^1] = Math.Max(rows[^1], Children[index].DesiredSize.Height);
        }
        return rows;
    }
}
