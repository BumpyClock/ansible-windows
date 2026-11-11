using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

internal static class ShortcutKeycaps
{
    internal static void Show(Panel keys, string displayText)
    {
        var resources = Application.Current.Resources;
        keys.Children.Clear();
        var parts = displayText.Split(" + ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                keys.Children.Add(new TextBlock
                {
                    Text = "+",
                    Style = (Style)resources["CaptionSecondaryStyle"],
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            keys.Children.Add(new Border
            {
                Style = (Style)resources["KeycapStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = parts[index], Style = (Style)resources["KeycapTextStyle"] }
            });
        }
    }
}
