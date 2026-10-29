using Ansible.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class WelcomePage : Page
{
    private readonly DictationSession _session;
    private readonly Action _completed;
    private readonly Action _modelsRequested;
    private readonly Func<bool> _shortcutBusy;
    private readonly DispatcherTimer _readinessTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private UiSessionObserver? _observer;
    private int _step;
    private bool _practiced;

    internal WelcomePage(DictationSession session, Action completed, Action modelsRequested, Func<bool> shortcutBusy)
    {
        InitializeComponent();
        _session = session;
        _completed = completed;
        _modelsRequested = modelsRequested;
        _shortcutBusy = shortcutBusy;
        Loaded += (_, _) =>
        {
            _observer = new UiSessionObserver(session, DispatcherQueue, _ => Render());
            _readinessTimer.Start();
            Render();
        };
        Unloaded += (_, _) => { _observer?.Dispose(); _observer = null; _readinessTimer.Stop(); };
        _readinessTimer.Tick += (_, _) => RenderNavigationReadiness();
    }

    internal void RestartLesson()
    {
        _step = 0;
        _practiced = false;
        PracticeBox.Text = "";
    }

    internal bool AcceptsShortcut => _step == 2 && (PracticeBox.FocusState != FocusState.Unfocused || _shortcutBusy());

    internal IShortcutTextTarget CapturePracticeTarget()
    {
        if (!IsLoaded || _step != 2 || PracticeBox.FocusState == FocusState.Unfocused)
            throw new InvalidOperationException("Click the practice field before using your shortcut.");
        return new PracticeTarget(this);
    }

    private void Render()
    {
        var state = _session.State;
        var shortcut = state.Settings.Shortcut;
        StepLabel.Text = $"{_step + 1} of 3";
        Heading.Text = _step switch { 0 => "Less typing. More you.", 1 => "One shortcut. Your words.", _ => "Give your keyboard a break." };
        Description.Text = _step switch
        {
            0 => "Turn your voice into text, right where you're writing.",
            1 => "A small habit that keeps your hands out of the way.",
            _ => "Try it here before you take it into another app."
        };
        IntroPanel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShortcutPanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PracticePanel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step switch { 0 => "Get started", 1 => "Try it", _ => "Go to Home" };
        RenderNavigationReadiness();
        ModelsButton.Visibility = !state.CanStart && state.IsIdle ? Visibility.Visible : Visibility.Collapsed;
        CancelPracticeButton.Visibility = state.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        Illustration.SetShortcut(shortcut);
        ShortcutInstruction.Text = state.Settings.PushToTalk ? "Hold. Speak. Release." : "Press. Speak. Press again.";
        ShortcutDetail.Text = state.Settings.PushToTalk
            ? $"Hold {shortcut.DisplayText} until listening starts, then speak. Release to finish. Your words arrive after recognition completes."
            : shortcut.IsModifierOnly
                ? $"Briefly hold {shortcut.DisplayText} to start. Repeat to finish. Your words arrive after recognition completes."
                : $"Press {shortcut.DisplayText} to start. Press it again to finish. Your words arrive after recognition completes.";
        var previousKeys = Keycaps.Tag as string;
        if (previousKeys != shortcut.DisplayText)
        {
            Keycaps.Tag = shortcut.DisplayText;
            Keycaps.Children.Clear();
            foreach (var label in shortcut.DisplayText.Split(" + "))
                Keycaps.Children.Add(new Border
                {
                    Style = (Style)Application.Current.Resources["KeycapStyle"],
                    Padding = new Thickness(18, 12, 18, 12),
                    Child = new TextBlock { Text = label, FontSize = 20 }
                });
        }
        PracticeStatus.Text = state.IsLiveOperation
            ? state.CanFinish ? "Listening. Speak naturally, then finish with your shortcut." : "Finishing your words..."
            : _practiced ? "That's your voice, in writing. You're ready."
            : state.CanStart ? ShortcutDetail.Text
            : "Set up a microphone-capable speech model before trying dictation. You can also skip this lesson.";
        var notice = state.Notice;
        var showNotice = notice.ShouldDisplayOn(NoticeArea.Dictation) || notice.ShouldDisplayOn(NoticeArea.Settings);
        Notice.Title = notice.Title;
        Notice.Message = notice.Message;
        Notice.Severity = notice.Kind == NoticeKind.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        Notice.IsOpen = showNotice;
        Notice.Visibility = showNotice ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void RefreshAfterShortcut() => Render();

    private void RenderNavigationReadiness()
    {
        var idle = _session.State.IsIdle && !_shortcutBusy();
        NextButton.IsEnabled = SkipButton.IsEnabled = BackButton.IsEnabled = ModelsButton.IsEnabled = idle;
    }

    private void NextClicked(object sender, RoutedEventArgs args)
    {
        if (_step == 2) { _completed(); return; }
        _step++;
        Render();
        if (_step == 2) PracticeBox.Focus(FocusState.Programmatic);
    }

    private void BackClicked(object sender, RoutedEventArgs args) { _step--; Render(); }
    private void SkipClicked(object sender, RoutedEventArgs args) => _completed();
    private void ModelsClicked(object sender, RoutedEventArgs args) => _modelsRequested();

    private async void CancelPracticeClicked(object sender, RoutedEventArgs args)
    {
        try { await _session.CancelAsync(); }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    private sealed class PracticeTarget(WelcomePage page) : IShortcutTextTarget
    {
        public CaretBounds? Caret => null;

        public Task<TextDeliveryOutcome> InsertAsync(string text, Func<bool> isCurrent, CancellationToken cancellationToken) =>
            OnDispatcherAsync(page.DispatcherQueue, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isCurrent() || !page.IsLoaded || page._step != 2)
                    return new TextDeliveryOutcome(TextDeliveryStatus.Rejected, "The practice field is no longer open.");
                page.PracticeBox.Text = text;
                page._practiced = true;
                page.Render();
                return new TextDeliveryOutcome(TextDeliveryStatus.Verified, "Inserted into the practice field.");
            });
    }

    internal static Task<T> OnDispatcherAsync<T>(DispatcherQueue dispatcher, Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception error) { completion.SetException(error); }
        })) completion.SetException(new InvalidOperationException("The practice window is no longer available."));
        return completion.Task;
    }
}

internal sealed class WelcomeShortcutTargetInspector(
    DispatcherQueue dispatcher, Func<WelcomePage?> currentWelcome, IShortcutTargetInspector desktop) : IShortcutTargetInspector
{
    public async Task<IShortcutTextTarget> InspectAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var practice = await WelcomePage.OnDispatcherAsync(dispatcher, () => currentWelcome()?.CapturePracticeTarget());
        return practice ?? await desktop.InspectAsync(settings, cancellationToken).ConfigureAwait(false);
    }
}
