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

    private readonly List<StickyPin> _pins = [];
    private CompositionPropertySet? _scrollProperties;
    private (double Left, double Right, double MaxWidth) _column;
    private bool _wide;

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
    /// True once the page title can no longer be read: less than a third of its height, the descenders, is left
    /// in the viewport. The window title bar then shows the page name.
    /// </summary>
    public bool IsTitleScrolledOut { get; private set; }

    /// <summary>
    /// True while the header actions have scrolled fully above the viewport, or the whole header row has when the page
    /// has no header actions. The window title bar then shows its copy of <see cref="PrimaryAction"/>, so the action
    /// never appears twice.
    /// </summary>
    public bool IsActionScrolledOut { get; private set; }

    /// <summary>Raised when <see cref="IsTitleScrolledOut"/> or <see cref="IsActionScrolledOut"/> changes.</summary>
    public event EventHandler? HeaderScrolledOutChanged;

    /// <summary>
    /// The height of the window's notice band at the bottom of the page. The scroll viewport ends this far above the
    /// page's bottom edge, so notices sit in a band under the viewport and never cover page content. The viewport top
    /// and the scroll offset stay where they are.
    /// </summary>
    public double BottomInset
    {
        get => Viewport.Margin.Bottom;
        set
        {
            if (Viewport.Margin.Bottom == value) return;
            // The resize raises ViewportSizeChanged and a layout pass, which refresh the bounds, header and pins.
            Viewport.Margin = new Thickness(0, 0, 0, value);
        }
    }

    /// <summary>
    /// The page's width cap. A page that is one column of rows reads best in a narrower measure, so by default the
    /// header and content together are at most PageReadingMaxWidth wide. A page with side-by-side columns sets
    /// Wide and gets PageContentMaxWidth. Either way the page starts at the page padding and the header actions end
    /// at the content's right edge. A page may change it while it shows.
    /// </summary>
    public bool Wide
    {
        get => _wide;
        set
        {
            if (_wide == value) return;
            _wide = value;
            if (!IsLoaded) return;
            UpdateLayoutBounds();
            UpdateHeader();
        }
    }

    /// <summary>
    /// The page column: its left and right insets inside the page and its width cap. The window lines its notice
    /// band up with it.
    /// </summary>
    public (double Left, double Right, double MaxWidth) Column =>
        (ContentHost.Padding.Left, ContentHost.Padding.Right, MaxContentWidth);

    /// <summary>Raised when <see cref="Column"/> changes.</summary>
    public event EventHandler? ColumnChanged;

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

    /// <summary>The width the header and content have: the space inside the page padding, up to the page's cap.</summary>
    public double ContentWidth => Math.Min(MaxContentWidth,
        Math.Max(0, ContentHost.ActualWidth - ContentHost.Padding.Left - ContentHost.Padding.Right));

    private double MaxContentWidth =>
        (double)Application.Current.Resources[Wide ? "PageContentMaxWidth" : "PageReadingMaxWidth"];

    /// <summary>
    /// The header action the window title bar repeats once the header actions scroll out. Its button belongs in
    /// <see cref="HeaderActions"/>. Pages with one declare it while they are constructed, before navigation shows them.
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

    // Side by side, the title and the actions share the first row and leave together. Stacked, the title leaves first
    // and the actions, under the subtitle, leave last. A page without a title, or without actions, uses the header row
    // instead, which holds the heading and the actions in both layouts.
    private void UpdateHeaderScrolledOut()
    {
        var header = IsAbove(HeaderLayout);
        var title = IsShown(TitleText) ? IsAbove(TitleText, TitleText.ActualHeight / 3) : header;
        var action = IsShown(HeaderActionsPanel) ? IsAbove(HeaderActionsPanel) : header;
        if (title == IsTitleScrolledOut && action == IsActionScrolledOut) return;
        IsTitleScrolledOut = title;
        IsActionScrolledOut = action;
        HeaderScrolledOutChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsShown(FrameworkElement element) =>
        element.Visibility == Visibility.Visible && element.ActualHeight > 0;

    // True when the element's bottom edge is at or above the viewport top, or within `remaining` DIPs below it.
    private bool IsAbove(FrameworkElement element, double remaining = 0) =>
        IsLoaded && IsShown(HeaderLayout) && IsShown(element)
        && element.TransformToVisual(Viewport).TransformPoint(new Point(0, element.ActualHeight)).Y <= remaining;

    private void UpdateHeader()
    {
        var hasTitle = !string.IsNullOrWhiteSpace(Title);
        var hasSubtitle = !string.IsNullOrWhiteSpace(Subtitle);
        var hasHeading = hasTitle || hasSubtitle;
        var hasActions = HeaderActions.Count > 0;
        HeaderLayout.Visibility = hasHeading || hasActions ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;
        SubtitleText.Visibility = hasSubtitle ? Visibility.Visible : Visibility.Collapsed;
        HeaderActionsPanel.Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
        foreach (var action in HeaderActions.OfType<FrameworkElement>())
            action.MaxWidth = ContentWidth;
        // Side by side, the actions sit at the right of the title's row and centre on the title. A narrow page
        // stacks them under the subtitle, left-aligned. Without a heading they lead the row.
        var stacked = hasHeading && hasActions && ContentWidth < (double)Application.Current.Resources["CompactPageThreshold"];
        var beside = hasHeading && hasActions && !stacked;
        HeaderLayout.ColumnSpacing = beside ? (double)Application.Current.Resources["CardGap"] : 0;
        Grid.SetColumnSpan(TitleText, beside ? 1 : 2);
        Grid.SetRow(HeaderActionsPanel, stacked ? 2 : 0);
        Grid.SetColumn(HeaderActionsPanel, beside ? 1 : 0);
        Grid.SetColumnSpan(HeaderActionsPanel, beside ? 1 : 2);
        HeaderActionsPanel.HorizontalAlignment = beside ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        HeaderActionsPanel.Margin = new Thickness(0, stacked ? 12 : 0, 0, 0);
        HeaderActionsPanel.Orientation = ContentWidth < (double)Application.Current.Resources["NarrowActionsThreshold"]
            ? Orientation.Vertical : Orientation.Horizontal;
    }

    private void ViewportLayoutUpdated(object? sender, object args) => UpdatePins();

    private void UpdatePins()
    {
        if (!IsLoaded || _pins.Count == 0) return;
        _scrollProperties ??= ElementCompositionPreview.GetScrollViewerManipulationPropertySet(Viewport);
        // The viewport ends above the notice band, so a tall pinned element stops above the notices too.
        foreach (var pin in _pins) pin.Update(_scrollProperties, ContentHost, Viewport.ActualHeight);
    }

    // Focus inside a stuck element asks the scroller to reveal its layout position, which has scrolled away; the
    // scroller would jump back to it. Where the element is drawn fully visible, nothing needs to scroll.
    private void ContentBringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        if (args.Handled || args.TargetElement is not { } target) return;
        var pin = _pins.FirstOrDefault(candidate => candidate.Contains(target));
        if (pin is null || !pin.IsStuck(Viewport.VerticalOffset)) return;
        var drawn = target.TransformToVisual(Viewport).TransformBounds(args.TargetRect);
        if (drawn.Top >= 0 && drawn.Bottom <= Viewport.ActualHeight) args.Handled = true;
    }

    private void UpdateLayoutBounds()
    {
        var resources = Application.Current.Resources;
        ContentHost.Padding = (Thickness)resources[Viewport.ActualWidth < (double)resources["CompactPageThreshold"]
            ? "CompactPagePadding" : "PagePadding"];
        // The cap is on the column inside the padding, so the page starts at the padding and never centres.
        ContentColumn.MaxWidth = MaxContentWidth;
        // The page flows in one scroller; content fills at least the viewport so footers sit at the bottom.
        ContentHost.MinHeight = Viewport.ActualHeight;
        var column = Column;
        if (column == _column) return;
        _column = column;
        ColumnChanged?.Invoke(this, EventArgs.Empty);
    }
}
