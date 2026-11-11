using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Ansible;

/// <summary>
/// The main window's title bar: the brand above the sidebar, the page name once the current page's title scrolls
/// out, and a copy of the page's declared primary action once the page's header actions scroll out.
/// </summary>
public sealed partial class ShellTitleBar : UserControl
{
    private Window? _window;
    private ThemeSettings? _themeSettings;
    private PageCanvas? _canvas;
    private PageAction? _action;
    private bool _closed;

    public ShellTitleBar()
    {
        InitializeComponent();
        AddAnimations();
    }

    /// <summary>Extends the window's content into its title bar and keeps the caption buttons in the app's colours.</summary>
    public void Attach(Window window)
    {
        _window = window;
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(Bar);
        var titleBar = window.AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _themeSettings = ThemeSettings.CreateForWindowId(window.AppWindow.Id);
        _themeSettings.Changed += ContrastThemeChanged;
        RefreshCaptionColors();
    }

    /// <summary>Stops following theme, page and action changes once the window has closed.</summary>
    public void Detach()
    {
        _closed = true;
        if (_themeSettings is not null) _themeSettings.Changed -= ContrastThemeChanged;
        _themeSettings = null;
        Follow(null, null);
    }

    /// <summary>
    /// Follows the sidebar. Expanded, the brand spans the sidebar and the page name starts at the content's left
    /// edge. Compact, the app name is the only label left in the shell, so it stays: the mark sits centred over the
    /// icon rail, the name follows it, and the page name follows the name.
    /// </summary>
    public void SetCompact(bool compact)
    {
        var resources = Application.Current.Resources;
        BrandColumn.Width = compact ? GridLength.Auto : (GridLength)resources["SidebarColumnWidth"];
        var rail = ((GridLength)resources["CompactSidebarColumnWidth"]).Value;
        Brand.Margin = compact ? new Thickness((rail - BrandMark.Width) / 2, 0, 0, 0) : (Thickness)resources["SidebarBrandMargin"];
        PageTitle.Margin = (Thickness)resources[compact ? "CompactTitleBarPageTitleMargin" : "TitleBarPageTitleMargin"];
        // The page name and the action copy move with the brand; keep the copy clickable where it now sits.
        RefreshDragRegions();
    }

    /// <summary>
    /// Follows the current page. The title names the page as the sidebar does; the page heading may be a greeting
    /// instead. An empty title keeps the title bar clear for pages outside the sidebar.
    /// </summary>
    public void ShowPage(string title, PageCanvas canvas)
    {
        PageTitle.Text = title;
        Follow(canvas, canvas.PrimaryAction);
        Render();
    }

    private void Follow(PageCanvas? canvas, PageAction? action)
    {
        if (!ReferenceEquals(_canvas, canvas))
        {
            if (_canvas is not null) _canvas.HeaderScrolledOutChanged -= HeaderScrolledOutChanged;
            _canvas = canvas;
            if (canvas is not null) canvas.HeaderScrolledOutChanged += HeaderScrolledOutChanged;
        }
        if (!ReferenceEquals(_action, action))
        {
            if (_action is not null) _action.Changed -= ActionChanged;
            _action = action;
            if (action is not null) action.Changed += ActionChanged;
        }
    }

    private void HeaderScrolledOutChanged(object? sender, EventArgs args) => Render();

    // Deferred so a page that updates several properties of its action renders the copy once.
    private void ActionChanged() => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        if (_closed) return;
        var scrolled = _canvas is { IsTitleScrolledOut: true } && PageTitle.Text.Length > 0;
        var action = scrolled && _canvas is { IsActionScrolledOut: true } && _action is { IsVisible: true } ? _action : null;
        if (action is not null)
        {
            var icon = _canvas?.PrimaryActionIcon;
            if (!ReferenceEquals(PrimaryActionIcon.IconSource, icon)) PrimaryActionIcon.IconSource = icon;
            PrimaryActionIcon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
            PrimaryActionLabel.Text = action.Label;
            PrimaryActionCopy.IsEnabled = action.IsEnabled;
            AutomationProperties.SetName(PrimaryActionCopy, action.AccessibleName);
        }
        var changed = SetShown(PageTitle, scrolled) | SetShown(PrimaryActionCopy, action is not null);
        // The TitleBar keeps visible controls in its content clickable; refresh its regions after the button toggles.
        if (changed) RefreshDragRegions();
    }

    // Deferred so the regions are computed from the layout the change produces.
    private void RefreshDragRegions() => DispatcherQueue.TryEnqueue(() => { if (!_closed) Bar.RecomputeDragRegions(); });

    private static bool SetShown(UIElement element, bool shown)
    {
        var visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility == visibility) return false;
        element.Visibility = visibility;
        return true;
    }

    private void PrimaryActionCopyClicked(object sender, RoutedEventArgs args)
    {
        if (_action is { IsEnabled: true } action) action.Invoke();
    }

    // The page name and action fade and rise into the title bar, and fade out. Skipped when Windows animations are off.
    private void AddAnimations()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var duration = TimeSpan.FromMilliseconds((double)Application.Current.Resources["MotionNormalMilliseconds"]);
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        foreach (UIElement element in new UIElement[] { PageTitle, PrimaryActionCopy })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var fadeIn = compositor.CreateScalarKeyFrameAnimation();
            fadeIn.Target = "Opacity";
            fadeIn.InsertKeyFrame(0, 0);
            fadeIn.InsertKeyFrame(1, 1);
            fadeIn.Duration = duration;
            var rise = compositor.CreateVector3KeyFrameAnimation();
            rise.Target = "Translation";
            rise.InsertKeyFrame(0, new Vector3(0, 4, 0));
            rise.InsertKeyFrame(1, Vector3.Zero);
            rise.Duration = duration;
            var show = compositor.CreateAnimationGroup();
            show.Add(fadeIn);
            show.Add(rise);
            ElementCompositionPreview.SetImplicitShowAnimation(element, show);
            var fadeOut = compositor.CreateScalarKeyFrameAnimation();
            fadeOut.Target = "Opacity";
            fadeOut.InsertKeyFrame(1, 0);
            fadeOut.Duration = duration;
            ElementCompositionPreview.SetImplicitHideAnimation(element, fadeOut);
        }
    }

    private void ShellThemeChanged(FrameworkElement sender, object args) => RefreshCaptionColors();
    private void ContrastThemeChanged(ThemeSettings sender, object args) => RefreshCaptionColors();

    private void RefreshCaptionColors()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || _window is null) return;
            _window.AppWindow.TitleBar.ButtonForegroundColor = ((SolidColorBrush)Bar.Foreground).Color;
            _window.AppWindow.TitleBar.ButtonInactiveForegroundColor = ((SolidColorBrush)BorderBrush).Color;
        });
    }
}
