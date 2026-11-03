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

    public static readonly DependencyProperty ShowDividerProperty =
        DependencyProperty.Register(nameof(ShowDivider), typeof(bool), typeof(SettingsRow), new PropertyMetadata(true, OnLayoutPropertyChanged));

    public static readonly DependencyProperty IsChildProperty =
        DependencyProperty.Register(nameof(IsChild), typeof(bool), typeof(SettingsRow), new PropertyMetadata(false, OnLayoutPropertyChanged));

    public SettingsRow()
    {
        InitializeComponent();
        ApplyChrome();
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

    /// <summary>Draws a hairline above the row. The first row in a group turns it off. Collapsing a row removes its divider.</summary>
    public bool ShowDivider
    {
        get => (bool)GetValue(ShowDividerProperty);
        set => SetValue(ShowDividerProperty, value);
    }

    /// <summary>Indents the row under the setting it depends on.</summary>
    public bool IsChild
    {
        get => (bool)GetValue(IsChildProperty);
        set => SetValue(IsChildProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is SettingsRow row)
        {
            if (args.Property == ControlContentProperty) row.AttachToggle(args.NewValue as ToggleSwitch);
            row.UpdateDescription();
            row.ApplyChrome();
            row.ApplyLayout(row.ActualWidth);
        }
    }

    private void LayoutSizeChanged(object sender, SizeChangedEventArgs args) => ApplyLayout(args.NewSize.Width);

    // The stock ToggleSwitch draws its On/Off text after the switch. The row draws it before the switch instead,
    // and the empty template column after the switch is pulled back so the switch ends at the right edge.
    private void AttachToggle(ToggleSwitch? toggle)
    {
        ToggleStateText.Visibility = toggle is null ? Visibility.Collapsed : Visibility.Visible;
        if (toggle is null) return;
        toggle.OnContent = null;
        toggle.OffContent = null;
        toggle.MinWidth = 0;
        toggle.Margin = new Thickness(0, 0, -12, 0);
        toggle.Toggled += (_, _) => RenderToggleState(toggle);
        toggle.IsEnabledChanged += (_, _) => RenderToggleState(toggle);
        RenderToggleState(toggle);
    }

    private void RenderToggleState(ToggleSwitch toggle)
    {
        ToggleStateText.Text = toggle.IsOn ? "On" : "Off";
        ToggleStateText.Opacity = toggle.IsEnabled ? 1 : 0.5;
    }

    // Divider and padding follow ShowDivider and IsChild. Code is their only source so the two cannot disagree.
    private void ApplyChrome()
    {
        var resources = Application.Current.Resources;
        LayoutRoot.BorderThickness = ShowDivider ? (Thickness)resources["SettingsRowDividerThickness"] : new Thickness(0);
        LayoutRoot.Padding = (Thickness)resources[IsChild ? "SettingsChildRowPadding" : "SettingsRowPadding"];
    }

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
        LayoutRoot.ColumnSpacing = wide ? SettingsColumnGap : 0;
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

    private static double SettingsColumnGap =>
        (double)Application.Current.Resources["SettingsRowColumnGap"];

    private static double SettingsRowSpacing =>
        (double)Application.Current.Resources["SettingsRowRowSpacing"];
}
