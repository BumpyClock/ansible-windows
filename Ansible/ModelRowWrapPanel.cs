using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Ansible;

/// <summary>
/// Lays children out left to right and wraps to a new line when the next child does not fit, like CSS flex-wrap.
/// Each child is centred on its line, so a text name and taller chips share a line cleanly.
/// </summary>
public sealed partial class ModelRowWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);
        return Flow(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Flow(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Flow(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, extent = 0;
        var line = new List<(UIElement Child, double X)>();
        void EndLine()
        {
            if (arrange)
                foreach (var (child, left) in line)
                    child.Arrange(new Rect(left, y + (lineHeight - child.DesiredSize.Height) / 2,
                        child.DesiredSize.Width, child.DesiredSize.Height));
            line.Clear();
        }
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0) continue;
            if (line.Count > 0 && x + HorizontalSpacing + size.Width > width)
            {
                EndLine();
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            var left = line.Count == 0 ? 0 : x + HorizontalSpacing;
            line.Add((child, left));
            x = left + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
            extent = Math.Max(extent, x);
        }
        EndLine();
        return new Size(extent, y + lineHeight);
    }
}
