using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.System;
using Windows.Foundation;

namespace Ansible;

public sealed partial class ModelCard : UserControl
{
    private ThemeSettings? _themeSettings;
    private string? _assetName;
    private string _name = "";
    private Uri? _iconUri;
    private bool _iconFailureLogged;
    private ModelInstallState _state;
    private bool _removeDiscardsPartial;
    private bool _companion;
    private string _behavior = "";
    private const string MemoryEstimateTip = "Memory is the admission estimate used before loading, not measured usage.";
    // Below this row width the actions move under the name so the name and chips keep readable space.
    private const double StackedActionsWidth = 600;

    public ModelCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ActualThemeChanged += OnThemeChanged;
    }

    public string ModelId { get; private set; } = "";
    public string DisplayName => _name;

    public event Action<string>? PrimaryRequested;
    public event Action<string>? PauseRequested;
    public event Action<string>? RemoveRequested;
    public event Action<string>? DiscardRequested;
    public event Action<string>? UseRequested;

    public void Initialize(NativeModelEntry entry, ThemeSettings themeSettings)
    {
        _themeSettings = themeSettings;
        themeSettings.Changed += OnThemeSettingsChanged;
        ModelId = entry.Id;
        _name = entry.DisplayName ?? entry.Id;
        _companion = ModelSelectionPolicy.IsDiarizationTask(entry.Task);
        NameText.Text = _name;
        AutomationProperties.SetName(NameText, _name);
        _behavior = BehaviorLabel(entry);
        CustomWordsBadge.Visibility = entry.SupportsCustomDictionary ? Visibility.Visible : Visibility.Collapsed;

        DescriptionText.Text = entry.Description ?? "";
        AboutRow.Visibility = string.IsNullOrEmpty(entry.Description) ? Visibility.Collapsed : Visibility.Visible;
        LanguagesText.Text = entry.Languages ?? "Languages not specified";
        CustomWordsText.Text = entry.SupportsCustomDictionary
            ? "Supports custom dictionary context hints."
            : "Custom dictionary hints are not supported.";
        LicenseDetailText.Text = $"{entry.License ?? "License not specified"}\n{entry.LicenseNotes ?? ""}".TrimEnd();
        SourceText.Text = $"{entry.Repo}\nPinned revision {entry.Revision}\n{entry.RemoteFile}";
        ByteCountText.Text = $"{entry.Bytes:N0} bytes";
        HashText.Text = $"SHA-256 {entry.Sha256}";
        PrecisionText.Text = entry.Precision is { } precision ? $"Precision {precision}" : "";
        PrecisionText.Visibility = entry.Precision is null ? Visibility.Collapsed : Visibility.Visible;

        AutomationProperties.SetAutomationId(PrimaryButton, $"ModelPrimary_{entry.Id}");
        AutomationProperties.SetAutomationId(UseButton, $"ModelUse_{entry.Id}");
        AutomationProperties.SetAutomationId(DetailsButton, $"ModelDetails_{entry.Id}");
        AutomationProperties.SetName(UseButton, $"Use {_name} for dictation");
        ApplyDetailsState();

        _assetName = entry.Family switch
        {
            "moonshine_asr" => "moonshine",
            "qwen3_asr" => "qwen",
            "vibevoice_asr" or "vibevoice_asr_streaming" => "vibevoice",
            "nemotron_asr" or "nemotron_3_diar" => "nvidia",
            _ => null
        };
        Monogram.Text = char.ToUpperInvariant(entry.Family[0]).ToString();
        ApplyIcon();
    }

    // busyReason explains a Download or Resume that waits on another model operation.
    public void Update(ModelDownloadSnapshot snapshot, bool idle, bool managerIdle, bool managerBusy, bool active, bool canSelect,
        string busyReason = "Wait for the current model operation to finish.")
    {
        var entry = snapshot.Model;
        var state = snapshot.State;
        _state = state;

        // The group heading says In use, Installed or Available; the role chip says what an in-use model does.
        RoleBadge.Text = active ? "Dictation" : _companion ? "Meeting companion" : "";
        RoleBadge.Visibility = active || _companion ? Visibility.Visible : Visibility.Collapsed;
        VisualStateManager.GoToState(this, active ? "OutlineActive" : "OutlineRest", false);

        // Installed rows show behaviour and memory; other rows add the download size. A transfer replaces
        // the sizes with its byte counts so the 160 px progress label can stay short.
        var installed = snapshot.HasModelFile || active;
        var memory = FormatBytes(entry.EstimatedMemoryBytes);
        var transfer = $"{FormatBytes(snapshot.DownloadedBytes)} of {FormatBytes(entry.Bytes)}";
        var facts = state switch
        {
            ModelInstallState.Downloading => snapshot.BytesPerSecond > 0
                ? $"{transfer} · {FormatBytes((long)snapshot.BytesPerSecond)}/s" : transfer,
            ModelInstallState.Paused => $"{transfer} downloaded",
            _ when installed => memory,
            _ => $"{FormatBytes(entry.Bytes)} download · {memory} memory"
        };
        var caption = _behavior.Length > 0 ? $"{_behavior} · {facts}" : facts;
        var stateLabel = !snapshot.Supported ? "Unsupported" : StateLabel(state);
        StateRun.Text = stateLabel ?? "";
        SeparatorRun.Text = stateLabel is not null ? " · " : "";
        CaptionRun.Text = caption;
        ToolTipService.SetToolTip(FactsText, state is ModelInstallState.Downloading or ModelInstallState.Paused ? null : MemoryEstimateTip);

        var showProgress = state is ModelInstallState.Downloading or ModelInstallState.Paused or ModelInstallState.Verifying;
        ProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Value = snapshot.Progress;
        // Verification reports no progress, so the bar shows activity rather than a fill level.
        DownloadProgress.IsIndeterminate = state == ModelInstallState.Verifying;
        ProgressText.Text = state switch
        {
            ModelInstallState.Verifying => "Verifying checksum",
            ModelInstallState.Paused => $"Paused at {snapshot.Progress:F0}%",
            _ => $"Downloading {snapshot.Progress:F0}%"
        };
        AutomationProperties.SetName(DownloadProgress, state == ModelInstallState.Verifying
            ? $"File verification progress for {_name}"
            : $"Model download progress for {_name}");

        if (snapshot.Error is { } error)
            ErrorNotice.Show(new NoticeContent(NoticeSeverity.Error, "Model file problem", error, IsClosable: false));
        ErrorNotice.Visibility = snapshot.Error is null ? Visibility.Collapsed : Visibility.Visible;

        // One primary action per row. While a transfer runs the primary action stops it; an installed
        // file has no primary action here because Use for dictation or the In use group covers it.
        var transferring = state is ModelInstallState.Downloading or ModelInstallState.Verifying;
        PrimaryButton.Content = state switch
        {
            ModelInstallState.Downloading => "Pause",
            ModelInstallState.Verifying => "Stop",
            ModelInstallState.ReadyToInstall => "Install verified model",
            ModelInstallState.Paused => "Resume",
            ModelInstallState.Failed => "Retry download",
            _ => "Download"
        };
        PrimaryButton.IsEnabled = transferring ? managerBusy : snapshot.Supported && (state == ModelInstallState.ReadyToInstall
            ? idle
            : managerIdle && !snapshot.HasModelFile &&
              state is ModelInstallState.NotInstalled or ModelInstallState.Paused or ModelInstallState.Failed);
        // Accent marks an enabled forward step only; Pause, Stop and disabled buttons use the default style.
        PrimaryButton.Style = (Style)Application.Current.Resources[
            transferring || !PrimaryButton.IsEnabled ? "DefaultButtonStyle" : "AppPrimaryButtonStyle"];
        PrimaryHost.Visibility = !active && (transferring || !snapshot.HasModelFile) ? Visibility.Visible : Visibility.Collapsed;
        // A disabled button shows no tooltip, so the host explains a wait on another model operation. A Border has
        // no automation peer, so the button carries the same help text for keyboard and Narrator users.
        var waitReason = !transferring && !PrimaryButton.IsEnabled && managerBusy && snapshot.Supported ? busyReason : null;
        ToolTipService.SetToolTip(PrimaryHost, waitReason);
        AutomationProperties.SetHelpText(PrimaryHost, waitReason ?? "");
        AutomationProperties.SetHelpText(PrimaryButton, waitReason ?? "");
        ToolTipService.SetToolTip(PrimaryButton, state == ModelInstallState.Downloading
            ? "Pause keeps partial files for Resume. Dictation can continue during downloads; installation waits until dictation finishes."
            : null);
        AutomationProperties.SetName(PrimaryButton, state switch
        {
            ModelInstallState.Downloading => $"Pause download of {_name}",
            ModelInstallState.Verifying => $"Stop verifying {_name}",
            _ => $"{PrimaryButton.Content} {_name}"
        });

        // An installed file takes precedence; after removal a leftover partial surfaces as Discard.
        _removeDiscardsPartial = !snapshot.HasModelFile && snapshot.HasPartial;
        RemoveButton.Visibility = snapshot.HasModelFile || snapshot.HasPartial ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = idle;
        ToolTipService.SetToolTip(RemoveButton, _removeDiscardsPartial ? "Discard partial download" : "Remove model");
        AutomationProperties.SetAutomationId(RemoveButton, _removeDiscardsPartial ? $"ModelDiscard_{ModelId}" : $"ModelRemove_{ModelId}");
        AutomationProperties.SetName(RemoveButton, _removeDiscardsPartial ? $"Discard partial download of {_name}" : $"Remove {_name}");

        // Use for dictation appears only when it can be clicked; a disabled button cannot explain itself. A row that
        // is still downloading or verifying offers Pause or Stop only, so selection follows a finished install.
        // The diarization companion never offers it: it is not a dictation model.
        UseButton.Visibility = !_companion && state == ModelInstallState.Installed && !active && snapshot.Supported && canSelect
            ? Visibility.Visible : Visibility.Collapsed;

        LocationText.Text = snapshot.Path;
    }

    internal void DetachThemeEvents()
    {
        Loaded -= OnLoaded;
        ActualThemeChanged -= OnThemeChanged;
        if (_themeSettings is { } settings) { settings.Changed -= OnThemeSettingsChanged; }
        _themeSettings = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyIcon();

    private void OnThemeChanged(FrameworkElement sender, object args) => ApplyIcon();

    private void RowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var stacked = args.NewSize.Width < StackedActionsWidth;
        Grid.SetRow(ActionsPanel, stacked ? 1 : 0);
        Grid.SetColumn(ActionsPanel, stacked ? 1 : 2);
        Grid.SetColumnSpan(ActionsPanel, stacked ? 2 : 1);
        ActionsPanel.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        ActionsPanel.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    private void DetailsClicked(object sender, RoutedEventArgs e)
    {
        DetailsPanel.Visibility = DetailsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ApplyDetailsState();
    }

    private void ApplyDetailsState()
    {
        var expanded = DetailsPanel.Visibility == Visibility.Visible;
        AutomationProperties.SetName(DetailsButton, expanded ? $"Hide details for {_name}" : $"Show details for {_name}");
        ToolTipService.SetToolTip(DetailsButton, expanded ? "Hide details" : "Details");
    }

    private void OnThemeSettingsChanged(ThemeSettings sender, object args)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (_themeSettings is not null) { ApplyIcon(); }
            }))
            System.Diagnostics.Debug.WriteLine($"Ansible: high-contrast icon update for '{ModelId}' could not be dispatched.");
    }

    private void ApplyIcon()
    {
        if (_assetName is null || IsHighContrast())
        {
            _iconUri = null;
            IconImage.Source = null;
            IconImage.Visibility = Visibility.Collapsed;
            Monogram.Visibility = Visibility.Visible;
            return;
        }
        var file = ActualTheme == ElementTheme.Dark ? $"{_assetName}-dark.png" : $"{_assetName}.png";
        _iconUri = new Uri($"ms-appx:///Assets/ModelIcons/{file}");
        _iconFailureLogged = false;
        Monogram.Visibility = Visibility.Visible;
        IconImage.Visibility = Visibility.Visible;
        IconImage.Source = new BitmapImage(_iconUri);
    }

    private void IconOpened(object sender, RoutedEventArgs e) => Monogram.Visibility = Visibility.Collapsed;

    private void IconFailed(object sender, ExceptionRoutedEventArgs e)
    {
        IconImage.Visibility = Visibility.Collapsed;
        IconImage.Source = null;
        Monogram.Visibility = Visibility.Visible;
        if (!_iconFailureLogged)
        {
            _iconFailureLogged = true;
            System.Diagnostics.Debug.WriteLine(
                $"Ansible: model icon '{_iconUri}' for '{ModelId}' failed to load: {e.ErrorMessage}. Using monogram fallback.");
        }
    }

    private bool IsHighContrast() => (_themeSettings ??
        throw new InvalidOperationException("Initialize the model card before loading it.")).HighContrast;

    private void PrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (_state is ModelInstallState.Downloading or ModelInstallState.Verifying) { PauseRequested?.Invoke(ModelId); }
        else { PrimaryRequested?.Invoke(ModelId); }
    }
    private void RemoveClicked(object sender, RoutedEventArgs e) =>
        (_removeDiscardsPartial ? DiscardRequested : RemoveRequested)?.Invoke(ModelId);
    private void UseClicked(object sender, RoutedEventArgs e) => UseRequested?.Invoke(ModelId);

    private static string BehaviorLabel(NativeModelEntry entry) =>
        ModelSelectionPolicy.IsDiarizationTask(entry.Task) ? "Meeting speaker detection" :
        entry.Family == "vibevoice_asr" && entry.Mode == "offline" ? "Text after you finish" :
        entry.Mode == "offline" ? "WAV files only" :
        entry.Preview == "live" ? "Live transcript" :
        entry.Preview == "buffered" ? "Buffered text" :
        "Text after you finish";

    internal static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GiB" :
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):F1} MiB" : $"{bytes:N0} bytes";

    // Exceptional states only. Installed and Not installed are the group; transfers show in the progress label.
    private static string? StateLabel(ModelInstallState state) => state switch
    {
        ModelInstallState.ReadyToInstall => "Ready to install",
        ModelInstallState.Failed => "Failed",
        _ => null
    };
}

/// <summary>
/// Lays children out left to right and wraps to a new line when the next child does not fit, like CSS flex-wrap.
/// Each child is centred on its line, so a text name and taller chips share a line cleanly.
/// </summary>
public sealed partial class ModelRowWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);
        return Flow(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Flow(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Flow(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, extent = 0;
        var line = new List<(UIElement Child, double X)>();
        void EndLine()
        {
            if (arrange)
                foreach (var (child, left) in line)
                    child.Arrange(new Rect(left, y + (lineHeight - child.DesiredSize.Height) / 2,
                        child.DesiredSize.Width, child.DesiredSize.Height));
            line.Clear();
        }
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0) continue;
            if (line.Count > 0 && x + HorizontalSpacing + size.Width > width)
            {
                EndLine();
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            var left = line.Count == 0 ? 0 : x + HorizontalSpacing;
            line.Add((child, left));
            x = left + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
            extent = Math.Max(extent, x);
        }
        EndLine();
        return new Size(extent, y + lineHeight);
    }
}
