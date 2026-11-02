using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class NoticeBar : UserControl
{
    private AppNotice? _notice;

    public NoticeBar() => InitializeComponent();

    public AppNotice? Notice => _notice;

    /// <summary>Raised when the user closes the notice or invokes an action that dismisses it.</summary>
    public event EventHandler<AppNotice>? CloseRequested;

    public void Show(AppNotice notice)
    {
        _notice = notice;
        var transient = notice.Severity == AppNoticeSeverity.Transient;
        IconHost.Visibility = transient ? Visibility.Collapsed : Visibility.Visible;
        IconGlyph.Glyph = notice.Severity switch
        {
            AppNoticeSeverity.Error => "",
            AppNoticeSeverity.Warning => "",
            _ => "",
        };
        InfoDisc.Visibility = notice.Severity is AppNoticeSeverity.Information ? Visibility.Visible : Visibility.Collapsed;
        WarningDisc.Visibility = notice.Severity is AppNoticeSeverity.Warning ? Visibility.Visible : Visibility.Collapsed;
        ErrorDisc.Visibility = notice.Severity is AppNoticeSeverity.Error ? Visibility.Visible : Visibility.Collapsed;

        var hasMessage = !string.IsNullOrWhiteSpace(notice.Message);
        TitleRun.Text = hasMessage ? notice.Title + "  " : notice.Title;
        MessageRun.Text = hasMessage ? notice.Message : "";
        TitleRun.FontWeight = transient ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;

        while (ActionsPanel.Children.Count > 1) ActionsPanel.Children.RemoveAt(0);
        var index = 0;
        foreach (var action in notice.Actions ?? [])
        {
            var button = new Button { Content = action.Label };
            button.Click += (_, _) =>
            {
                action.Invoke();
                if (action.DismissesNotice && _notice is { } current) CloseRequested?.Invoke(this, current);
            };
            ActionsPanel.Children.Insert(index++, button);
        }
        CloseButton.Visibility = notice.IsClosable ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(CloseButton, transient ? "Dismiss message" : $"Dismiss {notice.Title}");

        AutomationProperties.SetName(this, hasMessage ? $"{notice.Title} {notice.Message}" : notice.Title);
        AutomationProperties.SetLiveSetting(this, notice.Severity == AppNoticeSeverity.Error
            ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void CloseClicked(object sender, RoutedEventArgs args)
    {
        if (_notice is { } notice) CloseRequested?.Invoke(this, notice);
    }
}
