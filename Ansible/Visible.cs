using Microsoft.UI.Xaml;

namespace Ansible;

/// <summary>Visibility values for x:Bind function bindings.</summary>
public static class Visible
{
    public static Visibility If(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IfText(string? text) => If(!string.IsNullOrEmpty(text));
}
