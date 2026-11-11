using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class ShortcutField : UserControl
{
    private string _shown = "";

    public ShortcutField() => InitializeComponent();

    internal TextBox CaptureBox => Box;

    internal void ShowShortcut(string text)
    {
        if (text == _shown && Keycaps.Visibility == Visibility.Visible) return;
        _shown = text;
        CaptureBox.Text = text;
        Status.Visibility = Visibility.Collapsed;
        Keycaps.Visibility = Visibility.Visible;
        Keycaps.Children.Clear();
        var keycap = (Style)Application.Current.Resources["KeycapStyle"];
        var keycapText = (Style)Application.Current.Resources["KeycapTextStyle"];
        var secondary = (Style)Application.Current.Resources["CaptionSecondaryStyle"];
        var keys = text.Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0)
            {
                Keycaps.Children.Add(new TextBlock { Text = "+", Style = secondary, VerticalAlignment = VerticalAlignment.Center });
            }
            Keycaps.Children.Add(new Border { Style = keycap, Child = new TextBlock { Text = keys[i], Style = keycapText } });
        }
    }

    internal void ShowStatus(string text)
    {
        CaptureBox.Text = text;
        _shown = "";
        Keycaps.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Visible;
        Status.Text = text;
    }
}
