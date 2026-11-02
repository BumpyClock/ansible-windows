using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class Badge : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Badge), new PropertyMetadata("", TextChanged));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(UIElement), typeof(Badge), new PropertyMetadata(null, IconChanged));

    private string? _textToolTip;

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

    // The badge trims long text, so it mirrors the text as a tooltip. A tooltip the consumer set is never replaced.
    private bool HasConsumerToolTip =>
        ToolTipService.GetToolTip(this) is { } current && !(current is string text && text == _textToolTip);

    private static void TextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var badge = (Badge)sender;
        if (badge.HasConsumerToolTip)
            return;
        var text = args.NewValue as string;
        badge._textToolTip = string.IsNullOrEmpty(text) ? null : text;
        ToolTipService.SetToolTip(badge, badge._textToolTip);
    }

    private static void IconChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var badge = (Badge)sender;
        badge.IconPresenter.Visibility = args.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
        badge.BadgeLayout.ColumnSpacing = args.NewValue is null ? 0 : 6;
    }
}
