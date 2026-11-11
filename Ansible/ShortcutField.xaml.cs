using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

/// <summary>A shortcut shown as keycaps. The hidden <see cref="CaptureBox"/> receives focus and keys while recording.</summary>
public sealed partial class ShortcutField : UserControl
{
    private string _shown = "";

    public ShortcutField() => InitializeComponent();

    internal TextBox CaptureBox => Box;

    /// <summary>Shows a shortcut such as "Ctrl + Space" with one keycap per key.</summary>
    internal void ShowShortcut(string text)
    {
        // The page renders on every session snapshot; rebuild the keycaps only when something changed.
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

    /// <summary>Replaces the keycaps with a prompt or message while recording.</summary>
    internal void ShowStatus(string text)
    {
        CaptureBox.Text = text;
        _shown = "";
        Keycaps.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Visible;
        Status.Text = text;
    }
}
