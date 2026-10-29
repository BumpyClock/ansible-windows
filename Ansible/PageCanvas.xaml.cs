using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Ansible;

[ContentProperty(Name = nameof(PageContent))]
public sealed partial class PageCanvas : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageCanvas), new PropertyMetadata("", HeaderChanged));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageCanvas), new PropertyMetadata("", HeaderChanged));
    private bool _scrollable = true;

    public PageCanvas()
    {
        InitializeComponent();
        Loaded += (_, _) => { UpdateLayoutBounds(); UpdateHeader(); };
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public UIElementCollection HeaderActions => HeaderActionsPanel.Children;
    public double HeaderHeight => HeaderLayout.ActualHeight + HeaderLayout.Margin.Top + HeaderLayout.Margin.Bottom;

    public UIElement? PageContent
    {
        get => BodyHost.Content as UIElement;
        set => BodyHost.Content = value;
    }

    public bool Scrollable
    {
        get => _scrollable;
        set { _scrollable = value; UpdateLayoutBounds(); }
    }

    public Thickness ContentPadding => ContentHost.Padding;
    public double ContentWidth => Math.Max(0, ContentHost.ActualWidth - ContentPadding.Left - ContentPadding.Right);
    public double ViewportHeight => Viewport.ActualHeight;

    private void ViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateLayoutBounds();
        UpdateHeader();
    }

    private static void HeaderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((PageCanvas)sender).UpdateHeader();

    private void HeaderSizeChanged(object sender, SizeChangedEventArgs args) => UpdateHeader();

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
        ContentHost.Padding = (Thickness)resources[width < (double)resources["CompactPageThreshold"]
            ? "CompactPagePadding" : "PagePadding"];
        var boundedPageHeight = Math.Max(ViewportHeight, (double)resources["BoundedPageMinHeight"]);
        var canScroll = _scrollable || boundedPageHeight > ViewportHeight;
        Viewport.VerticalScrollBarVisibility = canScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        Viewport.VerticalScrollMode = canScroll ? ScrollMode.Auto : ScrollMode.Disabled;
        // Full-page flows retain bottom padding. Bounded lists receive a finite viewport and own their scrolling.
        ContentHost.Height = _scrollable ? double.NaN : boundedPageHeight;
        ContentHost.MinHeight = _scrollable ? ViewportHeight : 0;
    }
}
