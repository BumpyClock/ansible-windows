using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Ansible;

[ContentProperty(Name = nameof(PageContent))]
public sealed partial class PageCanvas : UserControl
{
    private bool _scrollable = true;

    public PageCanvas()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateLayoutBounds();
    }

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

    private void ViewportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutBounds();

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
