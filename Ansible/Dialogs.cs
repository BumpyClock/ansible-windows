using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

/// <summary>
/// A ContentDialog opens in the popup layer, which follows the application theme and ignores a theme requested on
/// the window's content root.
/// </summary>
internal static class DialogTheme
{
    public static void FollowWindowTheme(this ContentDialog dialog, XamlRoot root)
    {
        dialog.XamlRoot = root;
        dialog.RequestedTheme = root.Content is FrameworkElement content ? content.RequestedTheme : ElementTheme.Default;
    }
}

internal static class MeetingDeletion
{
    public static ContentDialog Dialog(int count, XamlRoot root)
    {
        var (title, content) = MeetingText.DeleteConfirmation(count);
        var dialog = new ContentDialog
        {
            Title = title, Content = content, PrimaryButtonText = "Delete", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        dialog.FollowWindowTheme(root);
        return dialog;
    }
}
