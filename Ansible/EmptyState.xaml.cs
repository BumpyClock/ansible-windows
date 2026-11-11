using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Ansible;

public sealed partial class EmptyState : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata("", Changed));

    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(string), typeof(EmptyState), new PropertyMetadata("", IconDataChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata("", Changed));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(EmptyState), new PropertyMetadata("", Changed));

    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(UIElement), typeof(EmptyState), new PropertyMetadata(null, Changed));

    public EmptyState() => InitializeComponent();

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Path data for drawn artwork, shown in place of the glyph while <see cref="Glyph"/> is empty.</summary>
    public string IconData
    {
        get => (string)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>One line of help under the title. Empty hides the line.</summary>
    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Content under the text, such as a shortcut hint or an action. Null hides the slot.</summary>
    public UIElement? Footer
    {
        get => (UIElement?)GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    private static void IconDataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var state = (EmptyState)sender;
        state.IconPath.Data = state.IconData.Length == 0
            ? null
            : (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), state.IconData);
        Changed(sender, args);
    }

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var state = (EmptyState)sender;
        state.Icon.Glyph = state.Glyph;
        var drawn = state.Glyph.Length == 0 && state.IconPath.Data is not null;
        state.Icon.Visibility = drawn ? Visibility.Collapsed : Visibility.Visible;
        state.IconPath.Visibility = drawn ? Visibility.Visible : Visibility.Collapsed;
        state.TitleText.Text = state.Title;
        state.DetailText.Text = TextFlow.NoWidow(state.Detail);
        state.DetailText.Visibility = string.IsNullOrWhiteSpace(state.Detail) ? Visibility.Collapsed : Visibility.Visible;
        state.FooterPresenter.Content = state.Footer;
        state.FooterPresenter.Visibility = state.Footer is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
