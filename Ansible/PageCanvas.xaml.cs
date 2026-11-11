using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

namespace Ansible;

[ContentProperty(Name = nameof(PageContent))]
public sealed partial class PageCanvas : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageCanvas), new PropertyMetadata("", HeaderChanged));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageCanvas), new PropertyMetadata("", HeaderChanged));

    private double _bottomInset;

    private readonly List<StickyPin> _pins = [];
    private CompositionPropertySet? _scrollProperties;

    public PageCanvas()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateLayoutBounds();
            UpdateHeader();
            UpdateHeaderScrolledOut();
            // Cached pages leave and rejoin the window; restart the pins rather than trust animations across that.
            foreach (var pin in _pins) pin.Stop();
            UpdatePins();
        };
        ContentHost.BringIntoViewRequested += ContentBringIntoViewRequested;
    }

    /// <summary>
    /// True while the whole header row, heading and actions, has scrolled above the viewport. The window title bar
    /// then shows the page name and the primary action, so the action never appears twice.
    /// </summary>
    public bool IsHeaderScrolledOut { get; private set; }

    public event EventHandler? HeaderScrolledOutChanged;

    /// <summary>Extra bottom padding for floating window content, such as notices, so the last rows can scroll above it.</summary>
    public double BottomInset
    {
        get => _bottomInset;
        set
        {
            if (_bottomInset == value) return;
            _bottomInset = value;
            UpdateLayoutBounds();
        }
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public UIElementCollection HeaderActions => HeaderActionsPanel.Children;

    public UIElement? PageContent
    {
        get => BodyHost.Content as UIElement;
        set => BodyHost.Content = value;
    }

    /// <summary>
    /// The page's only scroller. Page content flows inside it; views must not add nested vertical scrollers. Use
    /// <see cref="Pin"/> to keep content on screen while the page scrolls.
    /// </summary>
    public ScrollViewer Scroller => Viewport;

    /// <summary>
    /// Keeps <paramref name="element"/> on screen while the page scrolls, without a second scroller. See
    /// <see cref="StickyPin"/> for the rules. The element should draw above what scrolls under it (Canvas.ZIndex), and
    /// <paramref name="indicator"/>, when given, fades in only while the element is stuck.
    /// </summary>
    internal StickyPin Pin(FrameworkElement element, FrameworkElement bounds, double top = 0, double bottom = 0, UIElement? indicator = null)
    {
        var pin = new StickyPin(this, element, bounds, top, bottom, indicator);
        _pins.Add(pin);
        // Sizes and positions anywhere above the pin move it, so every layout pass rechecks the pins.
        if (_pins.Count == 1) Viewport.LayoutUpdated += ViewportLayoutUpdated;
        UpdatePins();
        return pin;
    }

    internal void Unpin(StickyPin pin)
    {
        if (!_pins.Remove(pin)) return;
        pin.Stop();
        if (_pins.Count == 0) Viewport.LayoutUpdated -= ViewportLayoutUpdated;
    }

    public double ContentWidth =>Math.Max(0, ContentHost.ActualWidth - ContentHost.Padding.Left - ContentHost.Padding.Right);

    /// <summary>
    /// The header action the window title bar repeats once the header scrolls out. Pages with one declare it
    /// while they are constructed, before navigation shows them.
    /// </summary>
    public PageAction? PrimaryAction { get; set; }

    /// <summary>
    /// The icon of <see cref="PrimaryAction"/>, for the title bar's copy. A page whose primary button shows an icon
    /// declares the same icon here; a text-only action leaves it unset.
    /// </summary>
    public IconSource? PrimaryActionIcon { get; set; }

    private void ViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateLayoutBounds();
        UpdateHeader();
        UpdateHeaderScrolledOut();
    }

    private void ViewportViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => UpdateHeaderScrolledOut();

    private static void HeaderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var canvas = (PageCanvas)sender;
        canvas.UpdateHeader();
        canvas.UpdateHeaderScrolledOut();
    }

    private void HeaderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateHeader();
        UpdateHeaderScrolledOut();
    }

    private void UpdateHeaderScrolledOut()
    {
        var scrolledOut = false;
        // The header row holds the heading and the actions in both the side-by-side and the stacked layout.
        if (IsLoaded && HeaderLayout.Visibility == Visibility.Visible && HeaderLayout.ActualHeight > 0)
        {
            var bottom = HeaderLayout.TransformToVisual(Viewport).TransformPoint(new Point(0, HeaderLayout.ActualHeight)).Y;
            scrolledOut = bottom <= 0;
        }
        if (scrolledOut == IsHeaderScrolledOut) return;
        IsHeaderScrolledOut = scrolledOut;
        HeaderScrolledOutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateHeader()
    {
        var hasTitle = !string.IsNullOrWhiteSpace(Title);
        var hasSubtitle = !string.IsNullOrWhiteSpace(Subtitle);
        var hasHeading = hasTitle || hasSubtitle;
        var hasActions = HeaderActions.Count > 0;
        HeaderLayout.Visibility = hasHeading || hasActions ? Visibility.Visible : Visibility.Collapsed;
        HeadingPanel.Visibility = hasHeading ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;
        SubtitleText.Visibility = hasSubtitle ? Visibility.Visible : Visibility.Collapsed;
        HeaderActionsPanel.Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
        foreach (var action in HeaderActions.OfType<FrameworkElement>())
            action.MaxWidth = ContentWidth;
        var stacked = hasHeading && hasActions && ContentWidth < (double)Application.Current.Resources["CompactPageThreshold"];
        HeaderLayout.RowDefinitions[1].Height = stacked ? GridLength.Auto : new GridLength(0);
        HeaderLayout.RowSpacing = stacked ? 12 : 0;
        HeaderLayout.ColumnSpacing = hasActions && !stacked ? (double)Application.Current.Resources["CardGap"] : 0;
        Grid.SetColumnSpan(HeadingPanel, stacked ? 2 : 1);
        Grid.SetRow(HeaderActionsPanel, stacked ? 1 : 0);
        Grid.SetColumn(HeaderActionsPanel, stacked || !hasHeading ? 0 : 1);
        Grid.SetColumnSpan(HeaderActionsPanel, stacked ? 2 : 1);
        HeaderActionsPanel.HorizontalAlignment = stacked || !hasHeading ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        HeaderActionsPanel.Orientation = ContentWidth < (double)Application.Current.Resources["NarrowActionsThreshold"]
            ? Orientation.Vertical : Orientation.Horizontal;
    }

    private void ViewportLayoutUpdated(object? sender, object args) => UpdatePins();

    private void UpdatePins()
    {
        if (!IsLoaded || _pins.Count == 0) return;
        _scrollProperties ??= ElementCompositionPreview.GetScrollViewerManipulationPropertySet(Viewport);
        // Floating notices cover the bottom inset, so a tall pinned element stops above them.
        var visibleHeight = Viewport.ActualHeight - _bottomInset;
        foreach (var pin in _pins) pin.Update(_scrollProperties, ContentHost, visibleHeight);
    }

    // Focus inside a stuck element asks the scroller to reveal its layout position, which has scrolled away; the
    // scroller would jump back to it. Where the element is drawn fully visible, nothing needs to scroll.
    private void ContentBringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        if (args.Handled || args.TargetElement is not { } target) return;
        var pin = _pins.FirstOrDefault(candidate => candidate.Contains(target));
        if (pin is null || !pin.IsStuck(Viewport.VerticalOffset)) return;
        var drawn = target.TransformToVisual(Viewport).TransformBounds(args.TargetRect);
        if (drawn.Top >= 0 && drawn.Bottom <= Viewport.ActualHeight - _bottomInset) args.Handled = true;
    }

    private void UpdateLayoutBounds()
    {
        var resources = Application.Current.Resources;
        var width = Math.Min(Viewport.ActualWidth, (double)resources["PageContentMaxWidth"]);
        var padding = (Thickness)resources[width < (double)resources["CompactPageThreshold"]
            ? "CompactPagePadding" : "PagePadding"];
        padding.Bottom += _bottomInset;
        ContentHost.Padding = padding;
        // The page flows in one scroller; content fills at least the viewport so footers sit at the bottom.
        ContentHost.MinHeight = Viewport.ActualHeight;
    }
}
