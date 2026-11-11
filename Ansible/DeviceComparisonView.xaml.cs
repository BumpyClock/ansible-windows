using System.Globalization;
using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class DeviceComparisonView : UserControl
{
    // The bars stay readable down to this width; with the divider, gaps, and result column the wide arrangement needs
    // their sum, so a narrower panel stacks the result under the bars instead.
    private const double MinimumMetricsWidth = 360;

    private DeviceComparisonSnapshot? _comparison;
    private string _summary = "";
    private string _modelName = "";
    private bool _compact;
    private bool _hasMeasurements;

    public DeviceComparisonView()
    {
        InitializeComponent();
        Render();
    }

    /// <summary>
    /// Shows the last comparison, or <paramref name="summary"/> when it has no measurements. <paramref name="modelName"/>
    /// is the plain name the page shows; the comparison's full catalog name becomes its tooltip.
    /// </summary>
    internal void Show(DeviceComparisonSnapshot? comparison, string summary, string modelName)
    {
        _comparison = comparison;
        _summary = summary ?? "";
        _modelName = modelName;
        Render();
    }

    private void Render()
    {
        if (CpuComparisonBar is null) return;

        var comparison = _comparison;
        var cpu = comparison?.CpuMilliseconds;
        var gpu = comparison?.GpuMilliseconds;
        var cpuValid = comparison?.CpuError is null && cpu is > 0 && double.IsFinite(cpu.Value);
        var gpuValid = comparison?.GpuError is null && gpu is > 0 && double.IsFinite(gpu.Value);
        _hasMeasurements = cpuValid || gpuValid;

        ComparisonMetrics.Visibility = _hasMeasurements ? Visibility.Visible : Visibility.Collapsed;
        ComparisonDetails.Visibility = _hasMeasurements ? Visibility.Visible : Visibility.Collapsed;
        SummaryOnlyText.Visibility = _hasMeasurements ? Visibility.Collapsed : Visibility.Visible;
        SummaryOnlyText.Text = string.IsNullOrWhiteSpace(_summary)
            ? "No device comparison has run yet."
            : _summary;
        ApplyLayout();
        if (!_hasMeasurements)
        {
            VisualStateManager.GoToState(this, "NoLeader", false);
            return;
        }

        var maximum = Math.Max(cpuValid ? cpu!.Value : 0, gpuValid ? gpu!.Value : 0);
        CpuComparisonBar.Maximum = maximum;
        GpuComparisonBar.Maximum = maximum;
        CpuComparisonBar.Value = cpuValid ? cpu!.Value : 0;
        GpuComparisonBar.Value = gpuValid ? gpu!.Value : 0;
        CpuComparisonValue.Text = cpuValid ? FormatMilliseconds(cpu!.Value) : comparison?.CpuError ?? "Unavailable";
        GpuComparisonValue.Text = gpuValid ? FormatMilliseconds(gpu!.Value) : comparison?.GpuError ?? "Unavailable";

        // Lower elapsed time leads. A tie or a single valid measurement has no slower device to contrast.
        var leader = cpuValid && gpuValid
            ? cpu!.Value < gpu!.Value ? "CpuLeads" : gpu.Value < cpu.Value ? "GpuLeads" : "NoLeader"
            : cpuValid ? "CpuLeads" : "GpuLeads";
        VisualStateManager.GoToState(this, leader, false);

        var recommended = comparison!.RecommendedBackend;
        // Each model and kind keeps its own result, so the panel names the one it shows.
        var kind = comparison.Kind switch
        {
            DeviceComparisonKind.Meeting => "meeting",
            DeviceComparisonKind.WholeRecording => "whole recording",
            _ => "streaming"
        };
        var name = string.IsNullOrWhiteSpace(_modelName) ? comparison.ModelName : _modelName;
        ComparisonTitleText.Text = $"Last device comparison: {name}, {kind}";
        ToolTipService.SetToolTip(ComparisonTitleText, name == comparison.ModelName ? null : comparison.ModelName);
        RecommendationBadge.Text = $"Auto chose {(recommended == NativeBackend.Cpu ? "CPU" : "Vulkan")}";
        ComparisonDetailsText.Text = $"Measured {FormatLocalTime(comparison.MeasuredAt)} on the bundled public sample, " +
            "excluding model loading and warmup. Each model and use is compared separately.";
    }

    private static string FormatLocalTime(DateTimeOffset measuredAt)
    {
        var local = measuredAt.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return $"{local.ToString("d MMM yyyy", culture)}, {local.ToString("t", culture)}";
    }

    private static string FormatMilliseconds(double milliseconds) =>
        $"{milliseconds.ToString("N1", CultureInfo.InvariantCulture)} ms";

    private void ComparisonLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var resources = Application.Current.Resources;
        var wide = MinimumMetricsWidth + 2 * (double)resources["BenchmarkColumnGap"] + ComparisonDivider.Width +
            (double)resources["BenchmarkResultWidth"];
        var compact = args.NewSize.Width < wide;
        if (compact == _compact) return;
        _compact = compact;
        ApplyLayout();
    }

    // Wide: bars | divider | result. Compact: result stacks under the bars and the divider is dropped.
    private void ApplyLayout()
    {
        var gap = (double)Application.Current.Resources["BenchmarkColumnGap"];
        ComparisonDivider.Visibility = _hasMeasurements && !_compact ? Visibility.Visible : Visibility.Collapsed;
        ComparisonLayout.ColumnSpacing = _compact || !_hasMeasurements ? 0 : gap;
        ComparisonLayout.RowSpacing = _compact && _hasMeasurements ? gap : 0;
        Grid.SetColumn(ComparisonDetails, _compact ? 0 : 2);
        Grid.SetRow(ComparisonDetails, _compact ? 1 : 0);
        Grid.SetColumnSpan(ComparisonDetails, _compact ? 3 : 1);
        ComparisonDetails.Width = _compact ? double.NaN : (double)Application.Current.Resources["BenchmarkResultWidth"];
        ComparisonDetails.HorizontalAlignment = _compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }
}
