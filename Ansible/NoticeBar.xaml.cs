using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Ansible;

public sealed partial class NoticeBar : UserControl
{
    private NoticeContent? _content;
    private bool _isConfirmation;

    public NoticeBar() => InitializeComponent();

    /// <summary>Raised when the user closes the notice, or chooses Undo on a confirmation.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Shows a notice. Content that looks the same as the current notice only replaces its actions, so posting
    /// on every state change neither rebuilds the buttons nor repeats the screen reader announcement.
    /// </summary>
    public void Show(NoticeContent content) => Render(content, isConfirmation: false);

    /// <summary>Shows a confirmation: no icon, regular weight, and an Undo action that also closes it.</summary>
    public void ShowConfirmation(NoticeConfirmation confirmation) => Render(
        new NoticeContent(NoticeSeverity.Information, confirmation.Message,
            Actions: confirmation.Undo is null ? null : [confirmation.Undo]),
        isConfirmation: true);

    private void Render(NoticeContent content, bool isConfirmation)
    {
        var unchanged = isConfirmation == _isConfirmation && content.LooksLike(_content);
        _content = content;
        _isConfirmation = isConfirmation;
        if (unchanged) return;

        IconHost.Visibility = isConfirmation ? Visibility.Collapsed : Visibility.Visible;
        IconGlyph.Glyph = content.Severity switch
        {
            NoticeSeverity.Error => "",
            NoticeSeverity.Warning => "",
            _ => "",
        };
        InfoDisc.Visibility = content.Severity is NoticeSeverity.Information ? Visibility.Visible : Visibility.Collapsed;
        WarningDisc.Visibility = content.Severity is NoticeSeverity.Warning ? Visibility.Visible : Visibility.Collapsed;
        ErrorDisc.Visibility = content.Severity is NoticeSeverity.Error ? Visibility.Visible : Visibility.Collapsed;

        var hasMessage = !string.IsNullOrWhiteSpace(content.Message);
        TitleRun.Text = hasMessage ? content.Title + "  " : content.Title;
        MessageRun.Text = hasMessage ? content.Message : "";
        TitleRun.FontWeight = isConfirmation ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;

        ActionButtons.Children.Clear();
        var actions = content.Actions ?? [];
        for (var index = 0; index < actions.Count; index++)
        {
            var button = new Button { Content = actions[index].Label };
            var position = index;
            // The newest post's delegate runs: it captured the state the notice now describes.
            button.Click += (_, _) =>
            {
                _content?.Actions?[position].Invoke();
                if (_isConfirmation) CloseRequested?.Invoke(this, EventArgs.Empty);
            };
            ActionButtons.Children.Add(button);
        }
        ActionButtons.Visibility = actions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = content.IsClosable ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(CloseButton, isConfirmation ? "Dismiss message" : $"Dismiss {content.Title}");

        AutomationProperties.SetName(this, hasMessage ? $"{content.Title} {content.Message}" : content.Title);
        AutomationProperties.SetLiveSetting(this, content.Severity == NoticeSeverity.Error
            ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void CloseClicked(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Lays a notice out as the stock InfoBar does. Its four children are, in order: the icon, the message, the action
/// buttons, and the close button. A collapsed child takes no space. While the message fits in
/// <see cref="InlineMessageMaxHeight"/> beside the actions, everything shares one row, centred. A longer message
/// gets the row to itself: the actions move under it, aligned with its text, and the close button stays at the top
/// right. The choice follows the message and the width, so a short confirmation keeps its Undo beside it in a narrow
/// window and a long message stacks in a wide one.
/// </summary>
public sealed partial class NoticeBarPanel : Panel
{
    private const double IconGap = 12;
    private const double ActionsGap = 12;
    private const double CloseGap = 8;
    private const double StackedRowGap = 8;
    // Buttons on the last row need more room under them than the bar's padding gives a line of text.
    private const double StackedBottomInset = 4;
    private bool _stacked;

    /// <summary>The tallest message that still sits beside the actions. The bar sets two lines of Body text.</summary>
    public double InlineMessageMaxHeight { get; set; } = double.PositiveInfinity;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 4)
            throw new InvalidOperationException("A notice panel takes an icon, a message, actions, and a close button.");
        UIElement icon = Children[0], message = Children[1], actions = Children[2], close = Children[3];
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        icon.Measure(unbounded);
        actions.Measure(unbounded);
        close.Measure(unbounded);
        var lead = Reserved(icon, IconGap);
        var beside = Reserved(actions, ActionsGap);
        var trail = Reserved(close, CloseGap);

        message.Measure(new Size(Math.Max(0, availableSize.Width - lead - beside - trail), double.PositiveInfinity));
        _stacked = beside > 0 && message.DesiredSize.Height > InlineMessageMaxHeight;
        if (_stacked) message.Measure(new Size(Math.Max(0, availableSize.Width - lead - trail), double.PositiveInfinity));

        var firstRow = FirstRowHeight(icon, message, close);
        var width = lead + message.DesiredSize.Width + trail;
        var height = Math.Max(firstRow, actions.DesiredSize.Height);
        if (_stacked)
        {
            width = Math.Max(width, lead + actions.DesiredSize.Width);
            height = firstRow + StackedRowGap + actions.DesiredSize.Height + StackedBottomInset;
        }
        else width += beside;
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UIElement icon = Children[0], message = Children[1], actions = Children[2], close = Children[3];
        var lead = Reserved(icon, IconGap);
        var beside = _stacked ? 0 : Reserved(actions, ActionsGap);
        var trail = Reserved(close, CloseGap);
        var firstRow = _stacked ? FirstRowHeight(icon, message, close) : finalSize.Height;

        var messageSize = message.DesiredSize;
        var messageTop = Math.Max(0, (firstRow - messageSize.Height) / 2);
        message.Arrange(new Rect(lead, messageTop, Math.Max(0, finalSize.Width - lead - beside - trail), messageSize.Height));
        // The icon is as tall as one line of the message and shares its first line.
        var iconSize = icon.DesiredSize;
        icon.Arrange(new Rect(0, messageTop + (Math.Min(messageSize.Height, iconSize.Height) - iconSize.Height) / 2,
            iconSize.Width, iconSize.Height));

        var closeSize = close.DesiredSize;
        close.Arrange(new Rect(Math.Max(0, finalSize.Width - closeSize.Width),
            _stacked ? 0 : Math.Max(0, (firstRow - closeSize.Height) / 2), closeSize.Width, closeSize.Height));

        var actionsSize = actions.DesiredSize;
        actions.Arrange(_stacked
            ? new Rect(lead, firstRow + StackedRowGap, Math.Min(actionsSize.Width, Math.Max(0, finalSize.Width - lead)), actionsSize.Height)
            : new Rect(Math.Max(0, finalSize.Width - trail - actionsSize.Width), Math.Max(0, (firstRow - actionsSize.Height) / 2),
                actionsSize.Width, actionsSize.Height));
        return finalSize;
    }

    // A collapsed child measures to nothing and takes its gap with it.
    private static double Reserved(UIElement child, double gap) =>
        child.DesiredSize.Width > 0 ? child.DesiredSize.Width + gap : 0;

    private static double FirstRowHeight(UIElement icon, UIElement message, UIElement close) =>
        Math.Max(Math.Max(icon.DesiredSize.Height, message.DesiredSize.Height), close.DesiredSize.Height);
}
