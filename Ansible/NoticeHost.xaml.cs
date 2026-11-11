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

public sealed partial class NoticeHost : UserControl
{
    private readonly Dictionary<string, Grid> _bars = [];
    private const string ConfirmationKey = "(confirmation)";
    private NoticeConfirmation? _timed;
    private DismissTimer? _timer;
    private readonly UISettings _uiSettings = new();
    private AppNotices? _notices;
    private NoticeArea? _area;
    private bool _expanded;
    private bool _pointerInside;
    private bool _focusInside;
    private int _visibleCount;
    private double _coveredHeight;

    public NoticeHost()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateCoveredHeight();
    }

    /// <summary>
    /// The height of the notice band at the bottom of the content, including the host's bottom margin; 0 while none
    /// show.
    /// </summary>
    public double CoveredHeight => _coveredHeight;

    public event EventHandler? CoveredHeightChanged;

    public void AlignWith(double left, double right, double maxWidth)
    {
        var margin = (Thickness)Application.Current.Resources["NoticeHostMargin"];
        Layout.Margin = new Thickness(left, margin.Top, right, margin.Bottom);
        BarColumn.MaxWidth = maxWidth;
    }

    public void Attach(AppNotices notices)
    {
        _notices = notices;
        notices.Changed += Render;
        Render();
    }

    /// <summary>
    /// Shows the notices for a page area. A null area shows every notice. Navigation collapses an expanded list.
    /// </summary>
    public void ShowPage(NoticeArea? area)
    {
        _area = area;
        _expanded = false;
        Render();
    }

    private void Render()
    {
        if (_notices is null) return;
        SyncTimer(_notices.Confirmation);
        var (shown, hidden) = _notices.Arrange(_area, _expanded);
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
            if (!_bars.TryGetValue(key, out var wrapper))
            {
                _bars[key] = wrapper = CreateBar(key);
                Bars.Children.Insert(index, wrapper);
            }
            else if (Bars.Children.IndexOf(wrapper) is var current && current != index)
                Bars.Children.Move((uint)current, (uint)index);
            var bar = (NoticeBar)wrapper.Children[0];
            if (index < shown.Count) bar.Show(shown[index].Content);
            else bar.ShowConfirmation(_notices.Confirmation!);
        }
        _visibleCount = keys.Count;
        if (_visibleCount == 0) _pointerInside = false;
        _focusInside = ContainsFocus();
        UpdateTimer();
        UpdateCoveredHeight();
    }

    private Grid CreateBar(string key)
    {
        var bar = new NoticeBar();
        bar.CloseRequested += (_, _) => Close(key);
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

    private void UpdateCoveredHeight()
    {
        var height = _visibleCount > 0 ? ActualHeight : 0;
        if (height == _coveredHeight) return;
        _coveredHeight = height;
        CoveredHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoreClicked(object sender, RoutedEventArgs args)
    {
        _expanded = true;
        Render();
    }

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
