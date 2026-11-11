using Ansible.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Ansible;

/// <summary>How the lesson closed. Finished covers Done and Skip for now; both save completion.</summary>
internal enum WelcomeExit { Finished, ModelsRequested, Interrupted }

/// <summary>
/// The three-step shortcut lesson, shown modally over the main window. Steps 1 and 2 only light the keycaps;
/// step 3 lets the global shortcut dictate into a practice field in the dialog.
/// </summary>
public sealed partial class WelcomeDialog : ContentDialog
{
    /// <summary>The notice key for failures in the welcome lesson, shared with the window that completes it.</summary>
    internal const string NoticeKey = "welcome";
    private const int PracticeStep = 2;
    private readonly DictationSession _session;
    private readonly AppNotices _notices;
    private readonly Func<bool> _shortcutBusy;
    private readonly DispatcherTimer _readinessTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly UISettings _uiSettings = new();
    private UiSessionObserver? _observer;
    private Storyboard? _stepMotion;
    private Storyboard? _celebration;
    private WelcomeExit _exit;
    private bool _showing;
    private bool _open;
    private bool _forceClose;
    private int _step;
    private bool _practiced;

    internal WelcomeDialog(DictationSession session, Func<bool> shortcutBusy, AppNotices notices)
    {
        InitializeComponent();
        _session = session;
        _notices = notices;
        _shortcutBusy = shortcutBusy;
        _readinessTimer.Tick += (_, _) => RenderNavigationReadiness();
        // Buttons and the practice field handle some keys first; the caps still follow every key.
        AddHandler(KeyDownEvent, new KeyEventHandler(KeyPressed), handledEventsToo: true);
        AddHandler(KeyUpEvent, new KeyEventHandler((_, args) => Scene.KeyChanged(args.Key, down: false)), handledEventsToo: true);
    }

    /// <summary>True from the show request until the dialog has closed, so a second request cannot open it twice.</summary>
    internal bool IsShowing => _showing;

    internal bool AcceptsShortcut => _open && _step == PracticeStep && (PracticeBox.FocusState != FocusState.Unfocused || _shortcutBusy());

    /// <summary>Shows the lesson from its first step and returns how it closed.</summary>
    /// <exception cref="InvalidOperationException">The lesson, or another dialog on this XamlRoot, is already open.</exception>
    internal async Task<WelcomeExit> ShowLessonAsync(XamlRoot root)
    {
        if (_showing) throw new InvalidOperationException("The welcome lesson is already open.");
        _showing = true;
        try
        {
            _step = 0;
            _practiced = false;
            _exit = WelcomeExit.Finished;
            _forceClose = false;
            PracticeBox.Text = "";
            Celebration.Visibility = Visibility.Collapsed;
            Scene.Reset();
            this.FollowWindowTheme(root);
            await ShowAsync();
            return _exit;
        }
        finally { _showing = false; }
    }

    /// <summary>Closes the lesson without completing it, for shutdown.</summary>
    internal void Dismiss() => Leave(WelcomeExit.Interrupted);

    // Escape skips, or cancels a running practice dictation. Enter takes the step's next action unless a button
    // or the practice field already used it.
    private void KeyPressed(object sender, KeyRoutedEventArgs args)
    {
        Scene.KeyChanged(args.Key, down: true);
        if (args.Key == Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            Skip();
        }
        else if (args.Key == Windows.System.VirtualKey.Enter && !args.Handled && NextButton.IsEnabled
            && FocusManager.GetFocusedElement(XamlRoot) is not (ButtonBase or TextBox))
        {
            args.Handled = true;
            Next();
        }
    }

    /// <summary>The global shortcut's press or release, so the caps show the chord the hook consumed.</summary>
    internal void ShowShortcutHeld(bool held) => Scene.ChordChanged(held);

    internal IShortcutTextTarget CapturePracticeTarget()
    {
        if (!_open || _step != PracticeStep || PracticeBox.FocusState == FocusState.Unfocused)
            throw new InvalidOperationException("Click the practice field before using your shortcut.");
        return new PracticeTarget(this);
    }

    internal void RefreshAfterShortcut() { if (_open) Render(); }

    private void LessonOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // Shutdown can ask to close between ShowAsync and Opened.
        if (_forceClose) { Hide(); return; }
        _open = true;
        _observer = new UiSessionObserver(_session, DispatcherQueue, _ => Render(), Scene.ShowLevel);
        _readinessTimer.Start();
        RenderSteps(_step, animate: false);
        NextButton.Focus(FocusState.Programmatic);
    }

    // Every close goes through Leave. Its callers refuse while a practice dictation runs; only shutdown forces it.
    private void LessonClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_forceClose) args.Cancel = true;
    }

    private void LessonClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _open = false;
        _observer?.Dispose();
        _observer = null;
        _readinessTimer.Stop();
        Scene.Reset();
    }

    private bool IsIdle => _session.State.IsIdle && !_shortcutBusy();

    private void Render()
    {
        var state = _session.State;
        var settings = state.Settings;
        var shortcut = settings.Shortcut;
        Heading.Text = _step switch { 0 => "Less typing. More you.", 1 => "One shortcut. Your words.", _ => "Give your keyboard a break." };
        Description.Text = _step switch
        {
            0 => "Turn your voice into text, right where you're writing.",
            1 => "A small habit that keeps your hands out of the way.",
            _ => "Try it here before you take it into another app."
        };
        IntroPanel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShortcutPanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PracticePanel.Visibility = _step == PracticeStep ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == PracticeStep ? "Done" : "Next";
        // Done already finishes the lesson, so the last step offers no second way out.
        SkipButton.Visibility = _step == PracticeStep ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderNavigationReadiness();
        ModelsButton.Visibility = !state.CanStart && state.IsIdle ? Visibility.Visible : Visibility.Collapsed;
        CancelPracticeButton.Visibility = state.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        ShortcutInstruction.Text = settings.PushToTalk ? "Hold. Speak. Release." : "Press. Speak. Press again.";
        ShortcutDetail.Text = settings.PushToTalk
            ? $"Hold {shortcut.DisplayText} until listening starts, then speak. Release to finish. Your words arrive after recognition completes."
            : shortcut.IsModifierOnly
                ? $"Briefly hold {shortcut.DisplayText} to start, then speak. Repeat to finish. Your words arrive after recognition completes."
                : $"Press {shortcut.DisplayText} to start, then speak. Press it again to finish. Your words arrive after recognition completes.";
        PracticeStatus.Text = state.IsLiveOperation
            ? state.CanFinish ? "Listening. Speak naturally, then finish with your shortcut." : "Finishing your words..."
            : _practiced ? "That's your voice, in writing. You're ready."
            : state.CanStart ? ShortcutDetail.Text
            : "Set up a microphone-capable speech model before trying dictation. You can also skip this lesson.";
        var verb = settings.PushToTalk ? "Hold" : "Press";
        var practicing = _step == PracticeStep && state.IsLiveOperation;
        Scene.Render(shortcut, _step switch
        {
            0 => $"Try it: {verb.ToLowerInvariant()} {shortcut.DisplayText}",
            1 => settings.PushToTalk ? $"Hold {shortcut.DisplayText}. Speak. Release." : $"Press {shortcut.DisplayText}. Speak. Press again.",
            _ when practicing => FloatingPreviewPresentation.From(state).Status,
            _ when _practiced => "Inserted into the practice field",
            _ => $"Click the practice field, then {verb.ToLowerInvariant()} {shortcut.DisplayText}"
        }, !practicing ? ScenePill.Resting : state.CanFinish ? ScenePill.Listening : ScenePill.Working);
    }

    private void RenderNavigationReadiness()
    {
        NextButton.IsEnabled = BackButton.IsEnabled = SkipButton.IsEnabled = ModelsButton.IsEnabled = IsIdle;
    }

    private void NextClicked(object sender, RoutedEventArgs args) => Next();
    private void BackClicked(object sender, RoutedEventArgs args) { if (IsIdle && _step > 0) GoTo(_step - 1); }
    private void SkipClicked(object sender, RoutedEventArgs args) => Skip();

    private void Next()
    {
        if (!IsIdle) return;
        if (_step == PracticeStep) Leave(WelcomeExit.Finished);
        else GoTo(_step + 1);
    }

    private void Skip()
    {
        if (IsIdle) Leave(WelcomeExit.Finished);
        else if (_session.State.CanCancel) CancelPractice();
    }

    private void GoTo(int step)
    {
        var previous = _step;
        _step = step;
        RenderSteps(previous, _uiSettings.AnimationsEnabled);
        // Back and Skip can collapse under the pointer; keep focus inside the lesson.
        if (_step == PracticeStep) PracticeBox.Focus(FocusState.Programmatic);
        else NextButton.Focus(FocusState.Programmatic);
    }

    // Base values always hold the end state, so stopping a motion early, or reduced motion, still lands there.
    private void RenderSteps(int previous, bool animate)
    {
        Render();
        _stepMotion?.Stop();
        _stepMotion = null;
        var story = new Storyboard();
        for (var index = 0; index < StepDots.Children.Count; index++)
        {
            var dot = (Grid)StepDots.Children[index];
            var current = (UIElement)dot.Children[1];
            dot.Width = index == _step ? 22 : 6;
            current.Opacity = index == _step ? 1 : 0;
            story.Children.Add(Animate(dot, "Width", dot.Width, 320, from: index == previous ? 22 : 6, enableDependent: true));
            story.Children.Add(Animate(current, "Opacity", current.Opacity, 200, from: index == previous ? 1 : 0));
        }
        AutomationProperties.SetName(StepDots, $"Step {_step + 1} of 3");
        if (!animate || previous == _step) return;
        // The new step slides in from the side it comes from while fading up.
        story.Children.Add(Animate(StepShift, "X", 0, 360, from: 28 * Math.Sign(_step - previous)));
        story.Children.Add(Animate(StepBody, "Opacity", 1, 220, from: 0));
        _stepMotion = story;
        story.Begin();
    }

    private static DoubleAnimation Animate(DependencyObject target, string property, double to, int milliseconds,
        double? from = null, bool enableDependent = false)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromMilliseconds(milliseconds), EnableDependentAnimation = enableDependent,
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    // Runs only from a real insertion into the practice field.
    private void Celebrate()
    {
        Celebration.Visibility = Visibility.Visible;
        Scene.ShowInserted();
        _celebration?.Stop();
        _celebration = null;
        if (!_uiSettings.AnimationsEnabled) return;
        var story = new Storyboard();
        var pop = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 };
        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var grow = Animate(CheckScale, axis, 1, 520, from: 0);
            grow.EasingFunction = pop;
            story.Children.Add(grow);
            story.Children.Add(Animate(BurstScale, axis, 2.2, 560, from: 1));
        }
        story.Children.Add(Animate(Burst, "Opacity", 0, 560, from: 0.9));
        _celebration = story;
        story.Begin();
    }

    private void Leave(WelcomeExit exit)
    {
        if (!_showing || _forceClose) return;
        _exit = exit;
        _forceClose = true;
        Hide();
    }

    private void ModelsClicked(object sender, RoutedEventArgs args) => Leave(WelcomeExit.ModelsRequested);
    private void CancelPracticeClicked(object sender, RoutedEventArgs args) => CancelPractice();

    private async void CancelPractice()
    {
        try { await _session.CancelAsync(); }
        catch (Exception error) { _notices.SetError(NoticeKey, error); }
    }

    private sealed class PracticeTarget(WelcomeDialog lesson) : IShortcutTextTarget
    {
        public CaretBounds? Caret => null;

        public Task<TextDeliveryOutcome> InsertAsync(BundlePayload payload, Func<bool> isCurrent, CancellationToken cancellationToken) =>
            OnDispatcherAsync(lesson.DispatcherQueue, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isCurrent() || !lesson._open || lesson._step != PracticeStep)
                    return new TextDeliveryOutcome(TextDeliveryStatus.Rejected, "The practice field is no longer open.");
                lesson.PracticeBox.Text = payload.Text;
                lesson._practiced = true;
                lesson.Render();
                lesson.Celebrate();
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

/// <summary>
/// A ContentDialog opens in the popup layer, which follows the application theme and ignores a theme requested on
/// the window's content root. Every dialog calls this before ShowAsync so it opens in its window's theme.
/// </summary>
internal static class DialogTheme
{
    /// <summary>
    /// Attaches the dialog to the window and mirrors the theme requested on the window's content root. Default stays
    /// Default, so the dialog keeps following a Windows theme change while it is open.
    /// </summary>
    public static void FollowWindowTheme(this ContentDialog dialog, XamlRoot root)
    {
        dialog.XamlRoot = root;
        dialog.RequestedTheme = root.Content is FrameworkElement content ? content.RequestedTheme : ElementTheme.Default;
    }
}

internal sealed class WelcomeShortcutTargetInspector(
    DispatcherQueue dispatcher, Func<WelcomeDialog?> currentWelcome, IShortcutTargetInspector desktop) : IShortcutTargetInspector
{
    public async Task<IShortcutTextTarget> InspectAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var practice = await WelcomeDialog.OnDispatcherAsync(dispatcher, () => currentWelcome()?.CapturePracticeTarget());
        return practice ?? await desktop.InspectAsync(settings, cancellationToken).ConfigureAwait(false);
    }
}
