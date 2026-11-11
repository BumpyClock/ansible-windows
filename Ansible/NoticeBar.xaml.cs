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
        TitleText.Text = content.Title;
        TitleText.Visibility = string.IsNullOrWhiteSpace(content.Title) ? Visibility.Collapsed : Visibility.Visible;
        TitleText.FontWeight = isConfirmation ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        MessageText.Text = hasMessage ? content.Message : "";
        MessageText.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;

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
/// Lays a notice out as the stock InfoBar does. Its five children are, in order: the icon, the title, the message,
/// the action buttons, and the close button. A collapsed child takes no space. The text never wraps raggedly:
/// <list type="number">
/// <item>When the title and message fit on one line beside the actions, everything shares one row, centred.</item>
/// <item>Otherwise, when each fits on a line of its own beside the actions, the message sits under the title and
/// the actions stay beside them.</item>
/// <item>Otherwise the actions move under the text, aligned with it, and the close button stays at the top right.</item>
/// <item>When the text still does not fit on one line, the message starts its own line under the title and wraps
/// under its own start.</item>
/// </list>
/// The choice follows the text and the width, so a short confirmation keeps its Undo beside it in a narrow window and
/// a long message stacks in a wide one.
/// </summary>
public sealed partial class NoticeBarPanel : Panel
{
    private const double IconGap = 12;
    private const double TitleGap = 8;
    private const double ActionsGap = 12;
    private const double CloseGap = 8;
    private const double StackedRowGap = 8;
    // Buttons on the last row need more room under them than the bar's padding gives a line of text.
    private const double StackedBottomInset = 4;
    private bool _stacked;
    private bool _oneLine;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 5)
            throw new InvalidOperationException("A notice panel takes an icon, a title, a message, actions, and a close button.");
        UIElement icon = Children[0], title = Children[1], message = Children[2], actions = Children[3], close = Children[4];
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        icon.Measure(unbounded);
        actions.Measure(unbounded);
        close.Measure(unbounded);
        // Unbounded, each text measures to its width on one line.
        title.Measure(unbounded);
        message.Measure(unbounded);
        var lead = Reserved(icon, IconGap);
        var beside = Reserved(actions, ActionsGap);
        var trail = Reserved(close, CloseGap);

        var line = title.DesiredSize.Width + TextGap(title, message) + message.DesiredSize.Width;
        var besideWidth = Math.Max(0, availableSize.Width - lead - beside - trail);
        var twoLinesBeside = Math.Max(title.DesiredSize.Width, message.DesiredSize.Width) <= besideWidth &&
            title.DesiredSize.Width > 0 && message.DesiredSize.Width > 0;
        _stacked = beside > 0 && line > besideWidth && !twoLinesBeside;
        var textWidth = _stacked ? Math.Max(0, availableSize.Width - lead - trail) : besideWidth;
        _oneLine = line <= textWidth;
        if (!_oneLine)
        {
            title.Measure(new Size(textWidth, double.PositiveInfinity));
            message.Measure(new Size(textWidth, double.PositiveInfinity));
        }

        var firstRow = FirstRowHeight(icon, title, message, close);
        var width = lead + (_oneLine ? line : Math.Max(title.DesiredSize.Width, message.DesiredSize.Width)) + trail;
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
        UIElement icon = Children[0], title = Children[1], message = Children[2], actions = Children[3], close = Children[4];
        var lead = Reserved(icon, IconGap);
        var beside = _stacked ? 0 : Reserved(actions, ActionsGap);
        var trail = Reserved(close, CloseGap);
        var firstRow = _stacked ? FirstRowHeight(icon, title, message, close) : finalSize.Height;
        var textWidth = Math.Max(0, finalSize.Width - lead - beside - trail);

        Size titleSize = title.DesiredSize, messageSize = message.DesiredSize;
        var textHeight = TextHeight(title, message);
        var textTop = Math.Max(0, (firstRow - textHeight) / 2);
        if (_oneLine)
        {
            // The message follows the title on its line.
            var messageLeft = lead + titleSize.Width + TextGap(title, message);
            title.Arrange(new Rect(lead, textTop, titleSize.Width, titleSize.Height));
            message.Arrange(new Rect(messageLeft, textTop, Math.Max(messageSize.Width, lead + textWidth - messageLeft), messageSize.Height));
        }
        else
        {
            // The message starts its own line under the title, so both wrap at the same left edge.
            title.Arrange(new Rect(lead, textTop, textWidth, titleSize.Height));
            message.Arrange(new Rect(lead, textTop + titleSize.Height, textWidth, messageSize.Height));
        }
        // The icon is as tall as one line of text and shares the first line.
        var iconSize = icon.DesiredSize;
        icon.Arrange(new Rect(0, textTop + (Math.Min(textHeight, iconSize.Height) - iconSize.Height) / 2,
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

    private static double TextGap(UIElement title, UIElement message) =>
        title.DesiredSize.Width > 0 && message.DesiredSize.Width > 0 ? TitleGap : 0;

    private double TextHeight(UIElement title, UIElement message) => _oneLine
        ? Math.Max(title.DesiredSize.Height, message.DesiredSize.Height)
        : title.DesiredSize.Height + message.DesiredSize.Height;

    private double FirstRowHeight(UIElement icon, UIElement title, UIElement message, UIElement close) =>
        Math.Max(Math.Max(icon.DesiredSize.Height, TextHeight(title, message)), close.DesiredSize.Height);
}
