using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
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

    public PageCanvas()
    {
        InitializeComponent();
        Loaded += (_, _) => { UpdateLayoutBounds(); UpdateHeader(); UpdateHeadingScrolledOut(); };
    }

    /// <summary>True while the page title has scrolled above the viewport. The window title bar then shows the page.</summary>
    public bool IsHeadingScrolledOut { get; private set; }

    public event EventHandler? HeadingScrolledOutChanged;

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

    /// <summary>The page's only scroller. Page content flows inside it; views must not add nested vertical scrollers.</summary>
    public ScrollViewer Scroller => Viewport;

    public double ContentWidth => Math.Max(0, ContentHost.ActualWidth - ContentHost.Padding.Left - ContentHost.Padding.Right);

    /// <summary>Returns the page's canvas: its direct content, or the first canvas in its visual tree.</summary>
    public static PageCanvas? Find(DependencyObject? root)
    {
        if (root is PageCanvas canvas) return canvas;
        if (root is UserControl { Content: PageCanvas content }) return content;
        if (root is null) return null;
        var pending = new Queue<DependencyObject>([root]);
        while (pending.TryDequeue(out var current))
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                var child = VisualTreeHelper.GetChild(current, index);
                if (child is PageCanvas found) return found;
                pending.Enqueue(child);
            }
        }
        return null;
    }

    /// <summary>The first header action button styled as the page's primary action, whether or not it is shown.</summary>
    public Button? FindPrimaryAction()
    {
        var primary = Application.Current.Resources["AppPrimaryButtonStyle"];
        var pending = new Queue<DependencyObject>([HeaderActionsPanel]);
        while (pending.TryDequeue(out var current))
        {
            if (current is Button button && ReferenceEquals(button.Style, primary)) return button;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
        }
        return null;
    }

    private void ViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateLayoutBounds();
        UpdateHeader();
        UpdateHeadingScrolledOut();
    }

    private void ViewportViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => UpdateHeadingScrolledOut();

    private static void HeaderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var canvas = (PageCanvas)sender;
        canvas.UpdateHeader();
        canvas.UpdateHeadingScrolledOut();
    }

    private void HeaderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateHeader();
        UpdateHeadingScrolledOut();
    }

    private void UpdateHeadingScrolledOut()
    {
        var scrolledOut = false;
        if (IsLoaded && TitleText.Visibility == Visibility.Visible && TitleText.ActualHeight > 0)
        {
            var bottom = TitleText.TransformToVisual(Viewport).TransformPoint(new Point(0, TitleText.ActualHeight)).Y;
            scrolledOut = bottom <= 0;
        }
        if (scrolledOut == IsHeadingScrolledOut) return;
        IsHeadingScrolledOut = scrolledOut;
        HeadingScrolledOutChanged?.Invoke(this, EventArgs.Empty);
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
