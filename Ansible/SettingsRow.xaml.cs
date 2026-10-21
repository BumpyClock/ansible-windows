using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Ansible;

[ContentProperty(Name = nameof(ControlContent))]
public sealed partial class SettingsRow : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ControlContentProperty =
        DependencyProperty.Register(nameof(ControlContent), typeof(UIElement), typeof(SettingsRow), new PropertyMetadata(null, OnLayoutPropertyChanged));

    public static readonly DependencyProperty WideWidthProperty =
        DependencyProperty.Register(nameof(WideWidth), typeof(double), typeof(SettingsRow), new PropertyMetadata(double.NaN, OnLayoutPropertyChanged));

    public static readonly DependencyProperty CompactAlignmentProperty =
        DependencyProperty.Register(nameof(CompactAlignment), typeof(HorizontalAlignment), typeof(SettingsRow), new PropertyMetadata(HorizontalAlignment.Stretch, OnLayoutPropertyChanged));

    public SettingsRow()
    {
        InitializeComponent();
        SizeChanged += LayoutSizeChanged;
        Loaded += (_, _) =>
        {
            UpdateDescription();
            ApplyLayout(ActualWidth);
        };
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public UIElement? ControlContent
    {
        get => (UIElement?)GetValue(ControlContentProperty);
        set => SetValue(ControlContentProperty, value);
    }

    /// <summary>Width applied to the control in the two-column layout. NaN keeps its natural width.</summary>
    public double WideWidth
    {
        get => (double)GetValue(WideWidthProperty);
        set => SetValue(WideWidthProperty, value);
    }

    /// <summary>Alignment used by the control when the row stacks at the compact breakpoint.</summary>
    public HorizontalAlignment CompactAlignment
    {
        get => (HorizontalAlignment)GetValue(CompactAlignmentProperty);
        set => SetValue(CompactAlignmentProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is SettingsRow row)
        {
            row.UpdateDescription();
            row.ApplyLayout(row.ActualWidth);
        }
    }

    private void LayoutSizeChanged(object sender, SizeChangedEventArgs args) => ApplyLayout(args.NewSize.Width);

    private void ApplyLayout(double width)
    {
        var control = ControlContent as FrameworkElement;
        if (control is null || LayoutRoot is null)
        {
            return;
        }

        var wide = width >= SettingsWideMinWidth;
        // The row keeps one shared visual tree, but only the active axis may contribute
        // geometry. Collapsing the inactive track and its spacing prevents a hidden
        // column or row from changing compact widths or wide row heights.
        LayoutRoot.ColumnDefinitions[1].Width = wide ? GridLength.Auto : new GridLength(0);
        LayoutRoot.RowDefinitions[1].Height = wide ? new GridLength(0) : GridLength.Auto;
        LayoutRoot.ColumnSpacing = wide ? SettingsCardGap : 0;
        LayoutRoot.RowSpacing = wide ? 0 : SettingsRowSpacing;
        Grid.SetColumn(DescriptionPanel, 0);
        Grid.SetRow(DescriptionPanel, 0);
        Grid.SetColumn(ControlPresenter, wide ? 1 : 0);
        Grid.SetRow(ControlPresenter, wide ? 0 : 1);
        ControlPresenter.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;

        control.HorizontalAlignment = wide ? HorizontalAlignment.Right : CompactAlignment;
        control.Width = wide && !double.IsNaN(WideWidth) ? WideWidth : double.NaN;
    }

    private void UpdateDescription()
    {
        if (DescriptionPanel is null || DescriptionText is null)
        {
            return;
        }

        var hasDescription = !string.IsNullOrWhiteSpace(Description);
        DescriptionText.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
        DescriptionPanel.Spacing = hasDescription
            ? (double)Application.Current.Resources["SettingsRowDescriptionSpacing"]
            : 0;
    }

    private static double SettingsWideMinWidth =>
        (double)Application.Current.Resources["SettingsWideMinWidth"];

    private static double SettingsCardGap =>
        (double)Application.Current.Resources["CardGap"];

    private static double SettingsRowSpacing =>
        (double)Application.Current.Resources["SettingsRowRowSpacing"];
}
