using System.Globalization;
using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class DeviceComparisonView : UserControl
{
    private DeviceComparisonSnapshot? _comparison;
    private string _summary = "";

    public DeviceComparisonSnapshot? Comparison
    {
        get => _comparison;
        set { _comparison = value; Render(); }
    }

    public string Summary
    {
        get => _summary;
        set { _summary = value ?? ""; Render(); }
    }

    public DeviceComparisonView()
    {
        InitializeComponent();
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
        var hasMeasurements = cpuValid || gpuValid;

        ComparisonMetrics.Visibility = hasMeasurements ? Visibility.Visible : Visibility.Collapsed;
        ComparisonDetails.Visibility = hasMeasurements ? Visibility.Visible : Visibility.Collapsed;
        SummaryOnlyText.Visibility = hasMeasurements ? Visibility.Collapsed : Visibility.Visible;
        SummaryOnlyText.Text = string.IsNullOrWhiteSpace(_summary)
            ? "No device comparison has run yet."
            : _summary;
        if (!hasMeasurements) return;

        var maximum = Math.Max(cpuValid ? cpu!.Value : 0, gpuValid ? gpu!.Value : 0);
        CpuComparisonBar.Maximum = maximum;
        GpuComparisonBar.Maximum = maximum;
        CpuComparisonBar.Value = cpuValid ? cpu!.Value : 0;
        GpuComparisonBar.Value = gpuValid ? gpu!.Value : 0;
        CpuComparisonValue.Text = cpuValid ? FormatMilliseconds(cpu!.Value) : comparison?.CpuError ?? "Unavailable";
        GpuComparisonValue.Text = gpuValid ? FormatMilliseconds(gpu!.Value) : comparison?.GpuError ?? "Unavailable";

        var recommended = comparison!.RecommendedBackend;
        RecommendationText.Text = $"Auto chose {(recommended == NativeBackend.Cpu ? "CPU" : "Vulkan")}";
        ComparisonDetailsText.Text = $"Public sample measured {comparison.MeasuredAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss'Z'}. " +
            "Loading and warmup excluded. Compared automatically for this model.";
    }

    private static string FormatMilliseconds(double milliseconds) =>
        $"{milliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms";

    private void ComparisonLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < (double)Application.Current.Resources["CompactPageThreshold"];
        Grid.SetColumn(ComparisonDetails, compact ? 0 : 1);
        Grid.SetRow(ComparisonDetails, compact ? 1 : 0);
        Grid.SetColumnSpan(ComparisonDetails, compact ? 2 : 1);
        ComparisonDetails.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }
}
