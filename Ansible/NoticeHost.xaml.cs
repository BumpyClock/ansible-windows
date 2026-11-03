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
    private readonly Dictionary<AppNotice, DismissTimer> _timers = new(ReferenceEqualityComparer.Instance);
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
        SyncTimers(_notices.Current);
        var visible = _notices.Current
            .Select((notice, order) => (notice, order))
            .Where(item => _area is not { } area || (item.notice.Area & area) != 0)
            .OrderBy(item => item.notice.Severity switch
            {
                AppNoticeSeverity.Error => 0,
                AppNoticeSeverity.Warning => 1,
                AppNoticeSeverity.Information => 2,
                _ => 3,
            })
            .ThenBy(item => item.order)
            .Select(item => item.notice)
            .ToList();
        var persistent = visible.Where(notice => notice.Severity != AppNoticeSeverity.Transient).ToList();
        var shown = _expanded ? persistent : persistent.Take(MaximumPersistentNotices).ToList();
        var hidden = persistent.Count - shown.Count;
        MoreButton.Content = hidden == 1 ? "1 more notice" : $"{hidden} more notices";
        MoreButton.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;

        var desired = shown.Concat(visible.Where(notice => notice.Severity == AppNoticeSeverity.Transient)).ToList();
        var keys = desired.Select(notice => notice.Key).ToHashSet();
        foreach (var key in _bars.Keys.Where(key => !keys.Contains(key)).ToList())
        {
            Bars.Children.Remove(_bars[key]);
            _bars.Remove(key);
        }
        for (var index = 0; index < desired.Count; index++)
        {
            var notice = desired[index];
            // A notice that changes rank leaves and re-enters: its old element has already played its exit motion.
            if (_bars.TryGetValue(notice.Key, out var wrapper) && Bars.Children.IndexOf(wrapper) != index)
            {
                Bars.Children.Remove(wrapper);
                wrapper = null;
            }
            if (wrapper is null)
            {
                _bars[notice.Key] = wrapper = CreateBar();
                Bars.Children.Insert(index, wrapper);
            }
            var bar = (NoticeBar)wrapper.Children[0];
            // Show announces the notice to assistive technology, so an unchanged notice is not shown again.
            if (!ReferenceEquals(bar.Notice, notice)) bar.Show(notice);
        }
        _visibleCount = desired.Count;
        if (_visibleCount == 0) _pointerInside = false;
        _focusInside = ContainsFocus();
        UpdateTimers();
        ReserveSpace();
    }

    private Grid CreateBar()
    {
        var bar = new NoticeBar { Shadow = new ThemeShadow(), Translation = NoticeElevation };
        bar.CloseRequested += (_, notice) => _notices?.Dismiss(notice);
        // The wrapper carries the exit motion so it does not compete with the bar's elevation translation.
        var wrapper = new Grid();
        wrapper.Children.Add(bar);
        wrapper.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape || bar.Notice is not { IsClosable: true } notice) return;
            args.Handled = true;
            _notices?.Dismiss(notice);
        };
        if (_uiSettings.AnimationsEnabled) AddHideAnimation(wrapper);
        return wrapper;
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

    // Timers run for every auto-dismissing notice, shown on this page or not, and pause while the reader is
    // pointing at or focused inside the notices.
    private void SyncTimers(IReadOnlyList<AppNotice> current)
    {
        foreach (var (notice, timer) in _timers.Where(entry => !current.Any(item => ReferenceEquals(item, entry.Key))).ToList())
        {
            timer.Timer.Stop();
            _timers.Remove(notice);
        }
        foreach (var notice in current)
        {
            if (notice.AutoDismissAfter is not { } after || _timers.ContainsKey(notice)) continue;
            var timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _timers.Remove(notice);
                _notices?.Dismiss(notice);
            };
            _timers[notice] = new DismissTimer(timer, after);
        }
    }

    private void UpdateTimers()
    {
        var paused = _pointerInside || _focusInside;
        foreach (var timer in _timers.Values)
        {
            if (paused) timer.Pause();
            else timer.Resume();
        }
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
        UpdateTimers();
    }

    private void HostPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _pointerInside = false;
        UpdateTimers();
    }

    private void HostGotFocus(object sender, RoutedEventArgs args)
    {
        _focusInside = true;
        UpdateTimers();
    }

    private void HostLostFocus(object sender, RoutedEventArgs args)
    {
        _focusInside = ContainsFocus();
        UpdateTimers();
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
