using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

/// <summary>
/// A page's primary header action, declared through PageCanvas.PrimaryAction. Once the page heading scrolls out,
/// the window title bar shows a copy with the same label, enabled state and visibility, and the copy runs the same
/// click handler. The label is the label TextBlock when given, otherwise the button's text content.
/// </summary>
public sealed class PageAction
{
    private readonly Button _button;
    private readonly RoutedEventHandler _click;
    private readonly TextBlock? _label;

    public PageAction(Button button, RoutedEventHandler click, TextBlock? label = null)
    {
        _button = button;
        _click = click;
        _label = label;
        button.IsEnabledChanged += (_, _) => Changed?.Invoke();
        Watch(button, UIElement.VisibilityProperty);
        Watch(button, AutomationProperties.NameProperty);
        if (label is null) Watch(button, ContentControl.ContentProperty);
        else Watch(label, TextBlock.TextProperty);
    }

    /// <summary>Raised when the label, accessible name, enabled state or visibility changes.</summary>
    public event Action? Changed;

    public string Label => _label?.Text ?? _button.Content as string ?? "";

    public string AccessibleName => AutomationProperties.GetName(_button) is { Length: > 0 } name ? name : Label;

    public bool IsEnabled => _button.IsEnabled;

    public bool IsVisible => _button.Visibility == Visibility.Visible;

    public void Invoke() => _click(_button, new RoutedEventArgs());

    private void Watch(DependencyObject source, DependencyProperty property) =>
        source.RegisterPropertyChangedCallback(property, (_, _) => Changed?.Invoke());
}
