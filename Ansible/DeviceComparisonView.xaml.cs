using System.Globalization;
using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class DeviceComparisonView : UserControl
{
    // The bars stay readable down to this width; with the divider, gaps, and result column the wide arrangement needs
    // their sum, so a narrower panel stacks the result under the bars instead.
    private const double MinimumMetricsWidth = 400;

    private DeviceComparisonSnapshot? _comparison;
    private string _summary = "";
    private string _modelName = "";
    private NativeBackend _choice = NativeBackend.Auto;
    private bool _compact;
    private bool _hasMeasurements;

    public DeviceComparisonView()
    {
        InitializeComponent();
        Render();
    }

    /// <summary>
    /// Shows the last comparison in plain words. <paramref name="modelName"/> is the plain name the page shows; the
    /// comparison's full catalog name becomes its tooltip. Without measurements the panel explains why from
    /// <paramref name="choice"/>, the saved processor, and keeps the engine's technical <paramref name="summary"/>
    /// one hover away.
    /// </summary>
    internal void Show(DeviceComparisonSnapshot? comparison, string summary, string modelName, NativeBackend choice)
    {
        _comparison = comparison;
        _summary = summary ?? "";
        _modelName = modelName;
        _choice = choice;
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
        // A manual processor skips the test, so it never produces a comparison of its own.
        SummaryOnlyText.Text = _choice == NativeBackend.Auto
            ? "No speed test result yet."
            : $"Auto is off, so Ansible uses the {DeviceName(_choice)} you chose and skips the speed test.";
        ToolTipService.SetToolTip(SummaryOnlyText, string.IsNullOrWhiteSpace(_summary) ? null : _summary);
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
        ShowValue(CpuComparisonValue, cpuValid ? cpu : null, comparison?.CpuError);
        ShowValue(GpuComparisonValue, gpuValid ? gpu : null, comparison?.GpuError);

        // Lower elapsed time leads. A tie or a single valid measurement has no slower device to contrast.
        var leader = cpuValid && gpuValid
            ? cpu!.Value < gpu!.Value ? "CpuLeads" : gpu.Value < cpu.Value ? "GpuLeads" : "NoLeader"
            : cpuValid ? "CpuLeads" : "GpuLeads";
        VisualStateManager.GoToState(this, leader, false);

        var recommended = comparison!.RecommendedBackend;
        // Each model and kind keeps its own result, so the panel names the one it shows.
        var kind = comparison.Kind switch
        {
            DeviceComparisonKind.Meeting => "meetings",
            DeviceComparisonKind.WholeRecording => "recognizing after you finish",
            _ => "recognizing while you speak"
        };
        var name = string.IsNullOrWhiteSpace(_modelName) ? comparison.ModelName : _modelName;
        ComparisonTitleText.Text = $"Last speed test: {name}, {kind}";
        ToolTipService.SetToolTip(ComparisonTitleText, name == comparison.ModelName ? null : comparison.ModelName);
        RecommendationBadge.Text = $"Auto uses the {DeviceName(recommended)}";
        ComparisonDetailsText.Text = $"{Reason(recommended, cpuValid ? cpu : null, gpuValid ? gpu : null)} " +
            $"Tested {FormatLocalTime(comparison.MeasuredAt)} with the sample recording included with Ansible, " +
            "not counting the time to load and warm up the model. Each model and use is tested separately.";
    }

    private static string DeviceName(NativeBackend backend) =>
        backend == NativeBackend.Cpu ? "CPU" : "graphics card";

    // Why Auto picked this processor, following DeviceBenchmark.Select. The graphics card wins when it is faster or
    // within five percent of the CPU, unless only the CPU keeps a meeting's pace. So with both times valid, a CPU pick
    // was always strictly faster. A device that failed its test or a later recognition has no valid time.
    private static string Reason(NativeBackend recommended, double? cpu, double? gpu)
    {
        if (cpu is null) return "The CPU could not be used.";
        if (gpu is null) return "The graphics card could not be used.";
        if (recommended == NativeBackend.Cpu || gpu < cpu) return "It was faster for this model.";
        return gpu == cpu
            ? "Both took the same time, and Auto prefers the graphics card."
            : "The CPU was only slightly faster, and Auto prefers the graphics card when they are this close.";
    }

    // A failed device shows one plain word. The exact time, or the engine's error, stays one hover away.
    private static void ShowValue(TextBlock value, double? milliseconds, string? error)
    {
        value.Text = milliseconds is { } measured ? FormatSeconds(measured) : error is null ? "Unavailable" : "Failed";
        ToolTipService.SetToolTip(value, milliseconds is { } exact
            ? $"{exact.ToString("N1", CultureInfo.CurrentCulture)} ms"
            : error);
    }

    private static string FormatLocalTime(DateTimeOffset measuredAt)
    {
        var local = measuredAt.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return $"{local.ToString("d MMM yyyy", culture)}, {local.ToString("t", culture)}";
    }

    // One decimal under 10 seconds, whole seconds from there. Rounding to tenths comes first, so 9.96 s reads "10 s".
    private static string FormatSeconds(double milliseconds)
    {
        var culture = CultureInfo.CurrentCulture;
        var seconds = milliseconds / 1000;
        var tenths = Math.Round(seconds, 1, MidpointRounding.AwayFromZero);
        if (tenths < 0.1) return $"Under {0.1.ToString("0.0", culture)} s";
        return tenths < 10
            ? $"{tenths.ToString("0.0", culture)} s"
            : $"{Math.Round(seconds, MidpointRounding.AwayFromZero).ToString("N0", culture)} s";
    }

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
