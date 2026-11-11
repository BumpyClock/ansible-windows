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
        ShortcutKeycaps.Show(Keycaps, text);
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
