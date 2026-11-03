using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Ansible;

/// <summary>
/// One page of the main window: the page, the sidebar entry that marks it current, the notice area it shows
/// (null shows every notice), and the name the title bar shows once its heading scrolls out.
/// </summary>
internal sealed record ShellPage(Page Page, Button Nav, Path Indicator, NoticeArea? Area, string Title)
{
    /// <summary>A sidebar page. Its title is the sidebar entry's accessible name, so XAML stays the one source.</summary>
    public static ShellPage InSidebar(Page page, Button nav, Path indicator, NoticeArea area) =>
        new(page, nav, indicator, area, AutomationProperties.GetName(nav));

    /// <summary>Every page roots at its PageCanvas.</summary>
    public PageCanvas Canvas => (PageCanvas)Page.Content;

    public void SetSelected(bool selected)
    {
        var resources = Application.Current.Resources;
        Nav.Style = (Style)resources[selected ? "SelectedNavigationButtonStyle" : "NavigationButtonStyle"];
        Indicator.Style = (Style)resources[selected ? "SelectedNavigationIconStyle" : "NavigationIconStyle"];
        AutomationProperties.SetHelpText(Nav, selected ? "Current page" : "");
        AutomationProperties.SetItemStatus(Nav, selected ? "Current page" : "");
    }
}

/// <summary>The main window's pages. Welcome has no sidebar entry of its own; it marks Home current.</summary>
internal sealed record ShellPages(
    ShellPage Home, ShellPage Welcome, ShellPage Meetings, ShellPage Models, ShellPage Dictionary, ShellPage Settings)
{
    /// <summary>The sidebar pages, in sidebar order.</summary>
    public IReadOnlyList<ShellPage> Sidebar { get; } = [Home, Meetings, Models, Dictionary, Settings];
}
