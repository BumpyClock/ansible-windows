using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Ansible;

/// <summary>
/// Lays out a page header. Its three children are, in order: the title, the subtitle, and the actions. The actions
/// sit beside the title when both fit on one line, and under the subtitle when they do not. A collapsed child
/// takes no space.
/// </summary>
public sealed partial class PageHeaderPanel : Panel
{
    private const double StackedActionsGap = 12;
    private bool _beside;

    /// <summary>The space between the title and the actions beside it.</summary>
    public double ColumnGap { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 3)
            throw new InvalidOperationException("A page header takes a title, a subtitle, and actions.");
        UIElement title = Children[0], subtitle = Children[1], actions = Children[2];
        var column = new Size(availableSize.Width, double.PositiveInfinity);
        actions.Measure(column);
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _beside = title.DesiredSize.Width + Gap(title, actions) + actions.DesiredSize.Width <= availableSize.Width;
        if (!_beside) title.Measure(column);
        subtitle.Measure(column);

        var height = _beside
            ? Math.Max(title.DesiredSize.Height, actions.DesiredSize.Height) + subtitle.DesiredSize.Height
            : title.DesiredSize.Height + subtitle.DesiredSize.Height + StackedActions(actions);
        var width = _beside
            ? title.DesiredSize.Width + Gap(title, actions) + actions.DesiredSize.Width
            : Math.Max(title.DesiredSize.Width, actions.DesiredSize.Width);
        return new Size(Math.Min(availableSize.Width, Math.Max(width, subtitle.DesiredSize.Width)), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UIElement title = Children[0], subtitle = Children[1], actions = Children[2];
        Size titleSize = title.DesiredSize, actionsSize = actions.DesiredSize;
        var titleRow = _beside ? Math.Max(titleSize.Height, actionsSize.Height) : titleSize.Height;
        var titleWidth = _beside ? Math.Max(0, finalSize.Width - Gap(title, actions) - actionsSize.Width) : finalSize.Width;
        title.Arrange(new Rect(0, 0, titleWidth, titleSize.Height));
        subtitle.Arrange(new Rect(0, titleRow, finalSize.Width, subtitle.DesiredSize.Height));
        actions.Arrange(_beside
            ? new Rect(finalSize.Width - actionsSize.Width, (titleRow - actionsSize.Height) / 2, actionsSize.Width, actionsSize.Height)
            : new Rect(0, titleRow + subtitle.DesiredSize.Height + StackedActionsGap, actionsSize.Width, actionsSize.Height));
        return finalSize;
    }

    private double Gap(UIElement title, UIElement actions) =>
        title.DesiredSize.Width > 0 && actions.DesiredSize.Width > 0 ? ColumnGap : 0;

    private static double StackedActions(UIElement actions) =>
        actions.DesiredSize.Height > 0 ? StackedActionsGap + actions.DesiredSize.Height : 0;
}
