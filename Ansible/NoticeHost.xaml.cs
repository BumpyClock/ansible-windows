using System.Diagnostics;
using System.Numerics;
using Ansible.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.ViewManagement;

namespace Ansible;

/// <summary>
/// Renders the window's AppNotices that apply to the current page as floating NoticeBars, most severe first.
/// Owns auto-dismiss timers and reserves bottom padding on the current page so its last rows stay reachable.
/// </summary>
public sealed partial class NoticeHost : UserControl
{
    private const int MaximumPersistentNotices = 3;
    private static readonly Vector3 NoticeElevation = new(0, 0, 32);
    private readonly Dictionary<string, Grid> _bars = [];
    private const string ConfirmationKey = "(confirmation)";
    private NoticeConfirmation? _timed;
    private DismissTimer? _timer;
    private readonly UISettings _uiSettings = new();
    private AppNotices? _notices;
    private PageCanvas? _canvas;
    private NoticeArea? _area;
    private bool _expanded;
    private bool _pointerInside;
    private bool _focusInside;
    private int _visibleCount;

    public NoticeHost()
    {
        InitializeComponent();
        MoreButton.Shadow = new ThemeShadow();
        MoreButton.Translation = new Vector3(0, 0, 16);
        SizeChanged += (_, _) => ReserveSpace();
    }

    public void Attach(AppNotices notices)
    {
        _notices = notices;
        notices.Changed += Render;
        Render();
    }

    /// <summary>
    /// Shows the notices for a navigation page tag and reserves space on that page's canvas. Welcome and unknown
    /// pages show every notice. Navigation collapses an expanded list.
    /// </summary>
    public void ShowPage(string page, PageCanvas? canvas)
    {
        _area = page switch
        {
            "dictation" => NoticeArea.Dictation,
            "settings" => NoticeArea.Settings,
            "models" => NoticeArea.Models,
            "custom-words" => NoticeArea.Dictionary,
            "meetings" => NoticeArea.Meetings,
            _ => null,
        };
        if (!ReferenceEquals(_canvas, canvas))
        {
            if (_canvas is not null) _canvas.BottomInset = 0;
            _canvas = canvas;
        }
        _expanded = false;
        Render();
    }

    private void Render()
    {
        if (_notices is null) return;
        SyncTimer(_notices.Confirmation);
        var visible = _notices.Current
            .Select((notice, order) => (notice, order))
            .Where(item => _area is not { } area || (item.notice.Area & area) != 0)
            .OrderByDescending(item => item.notice.Content.Severity)
            .ThenBy(item => item.order)
            .Select(item => item.notice)
            .ToList();
        var shown = _expanded ? visible : visible.Take(MaximumPersistentNotices).ToList();
        var hidden = visible.Count - shown.Count;
        MoreButton.Content = hidden == 1 ? "1 more notice" : $"{hidden} more notices";
        MoreButton.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;

        var keys = shown.Select(notice => notice.Key).ToList();
        if (_notices.Confirmation is not null) keys.Add(ConfirmationKey);
        foreach (var key in _bars.Keys.Where(key => !keys.Contains(key)).ToList())
        {
            Bars.Children.Remove(_bars[key]);
            _bars.Remove(key);
        }
        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index];
            // A notice that changes rank leaves and re-enters: its old element has already played its exit motion.
            if (_bars.TryGetValue(key, out var wrapper) && Bars.Children.IndexOf(wrapper) != index)
            {
                Bars.Children.Remove(wrapper);
                wrapper = null;
            }
            if (wrapper is null)
            {
                _bars[key] = wrapper = CreateBar(key);
                Bars.Children.Insert(index, wrapper);
            }
            var bar = (NoticeBar)wrapper.Children[0];
            if (index < shown.Count) bar.Show(shown[index].Content);
            else bar.ShowConfirmation(_notices.Confirmation!);
        }
        _visibleCount = keys.Count;
        if (_visibleCount == 0) _pointerInside = false;
        _focusInside = ContainsFocus();
        UpdateTimer();
        ReserveSpace();
    }

    private Grid CreateBar(string key)
    {
        var bar = new NoticeBar { Shadow = new ThemeShadow(), Translation = NoticeElevation };
        bar.CloseRequested += (_, _) => Close(key);
        // The wrapper carries the exit motion so it does not compete with the bar's elevation translation.
        var wrapper = new Grid();
        wrapper.Children.Add(bar);
        wrapper.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            Close(key);
        };
        if (_uiSettings.AnimationsEnabled) AddHideAnimation(wrapper);
        return wrapper;
    }

    private void Close(string key)
    {
        if (_notices is null) return;
        if (key != ConfirmationKey)
        {
            if (_notices.Current.FirstOrDefault(notice => notice.Key == key) is { Content.IsClosable: true })
                _notices.Dismiss(key);
        }
        else if (_notices.Confirmation is { } confirmation) _notices.EndConfirmation(confirmation);
    }

    // Removal plays a short fade and drop. Skipped when Windows animations are off.
    private static void AddHideAnimation(UIElement element)
    {
        var resources = Application.Current.Resources;
        var duration = TimeSpan.FromMilliseconds((double)resources["MotionFastMilliseconds"]);
        var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(1, 0);
        fade.Duration = duration;
        var drop = compositor.CreateVector3KeyFrameAnimation();
        drop.Target = "Translation";
        drop.InsertKeyFrame(1, new Vector3(0, 8, 0));
        drop.Duration = duration;
        var hide = compositor.CreateAnimationGroup();
        hide.Add(fade);
        hide.Add(drop);
        ElementCompositionPreview.SetImplicitHideAnimation(element, hide);
    }

    private void ReserveSpace()
    {
        if (_canvas is not null) _canvas.BottomInset = _visibleCount > 0 ? ActualHeight : 0;
    }

    private void MoreClicked(object sender, RoutedEventArgs args)
    {
        _expanded = true;
        Render();
    }

    // The confirmation timer pauses while the reader is pointing at or focused inside the notices.
    private void SyncTimer(NoticeConfirmation? confirmation)
    {
        if (ReferenceEquals(_timed, confirmation)) return;
        _timer?.Timer.Stop();
        _timer = null;
        _timed = confirmation;
        if (confirmation is null) return;
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _notices?.EndConfirmation(confirmation);
        };
        _timer = new DismissTimer(timer, confirmation.Duration);
    }

    private void UpdateTimer()
    {
        if (_timer is null) return;
        if (_pointerInside || _focusInside) _timer.Pause();
        else _timer.Resume();
    }

    private bool ContainsFocus()
    {
        if (XamlRoot is null) return false;
        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, this)) return true;
        }
        return false;
    }

    private void HostPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _pointerInside = true;
        UpdateTimer();
    }

    private void HostPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _pointerInside = false;
        UpdateTimer();
    }

    private void HostGotFocus(object sender, RoutedEventArgs args)
    {
        _focusInside = true;
        UpdateTimer();
    }

    private void HostLostFocus(object sender, RoutedEventArgs args)
    {
        _focusInside = ContainsFocus();
        UpdateTimer();
    }

    private sealed class DismissTimer(DispatcherQueueTimer timer, TimeSpan remaining)
    {
        private TimeSpan _remaining = remaining;
        private long _startedAt;

        public DispatcherQueueTimer Timer => timer;

        public void Resume()
        {
            if (timer.IsRunning) return;
            _startedAt = Stopwatch.GetTimestamp();
            timer.Interval = _remaining > TimeSpan.Zero ? _remaining : TimeSpan.FromMilliseconds(1);
            timer.Start();
        }

        public void Pause()
        {
            if (!timer.IsRunning) return;
            timer.Stop();
            _remaining -= Stopwatch.GetElapsedTime(_startedAt);
        }
    }
}
