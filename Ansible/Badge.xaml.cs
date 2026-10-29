using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class Badge : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Badge), new PropertyMetadata(""));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(UIElement), typeof(Badge), new PropertyMetadata(null, IconChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public UIElement? Icon
    {
        get => (UIElement?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public Badge() => InitializeComponent();

    private static void IconChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var badge = (Badge)sender;
        badge.IconPresenter.Visibility = args.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
        badge.BadgeLayout.ColumnSpacing = args.NewValue is null ? 0 : 6;
    }
}
