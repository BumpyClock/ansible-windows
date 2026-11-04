using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

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

        while (ActionsPanel.Children.Count > 1) ActionsPanel.Children.RemoveAt(0);
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
            ActionsPanel.Children.Insert(index, button);
        }
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
