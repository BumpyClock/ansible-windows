using System.Globalization;
using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class DeviceComparisonView : UserControl
{
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
        SummaryOnlyText.Text = _choice == NativeBackend.Auto
            ? string.IsNullOrWhiteSpace(_summary) ? "No speed test result yet." : _summary
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

        var leader = cpuValid && gpuValid
            ? cpu!.Value < gpu!.Value ? "CpuLeads" : gpu.Value < cpu.Value ? "GpuLeads" : "NoLeader"
            : cpuValid ? "CpuLeads" : "GpuLeads";
        VisualStateManager.GoToState(this, leader, false);

        var recommended = comparison!.RecommendedBackend;
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
        ComparisonDetailsText.Text = $"{Reason(comparison.Reason)} " +
            $"Tested {FormatLocalTime(comparison.MeasuredAt)} with the sample recording included with Ansible, " +
            "not counting the time to load and warm up the model. Each model and use is tested separately.";
    }

    private static string DeviceName(NativeBackend backend) =>
        backend == NativeBackend.Cpu ? "CPU" : "graphics card";

    private static string Reason(DeviceChoiceReason reason) => reason switch
    {
        DeviceChoiceReason.CpuUnavailable => "The CPU could not be used.",
        DeviceChoiceReason.GpuUnavailable => "The graphics card could not be used.",
        DeviceChoiceReason.SameTime => "Both took the same time, and Auto prefers the graphics card.",
        DeviceChoiceReason.GpuPreferredWhenClose =>
            "The CPU was only slightly faster, and Auto prefers the graphics card when they are this close.",
        DeviceChoiceReason.KeepsMeetingPace =>
            "The CPU keeps up with meetings. The graphics card missed the meeting speed limit despite being close to the CPU.",
        _ => "It was faster for this model."
    };

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
