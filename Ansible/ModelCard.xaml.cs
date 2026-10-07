using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.System;

namespace Ansible;

public sealed partial class ModelCard : UserControl
{
    private ThemeSettings? _themeSettings;
    private string? _assetName;
    private string _name = "";
    private Uri? _iconUri;
    private bool _iconFailureLogged;
    private string? _stateDotBrushKey;
    private ModelInstallState _state;
    private bool _removeDiscardsPartial;

    public ModelCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ActualThemeChanged += OnThemeChanged;
    }

    public string ModelId { get; private set; } = "";

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
        NameText.Text = _name;
        AutomationProperties.SetName(NameText, _name);
        CaptionRun.Text = entry.Precision is { } precision ? $"{BehaviorLabel(entry)} \u00B7 {precision}" : BehaviorLabel(entry);
        // The catalog mixes concrete values ("English") with caveat sentences; only concrete values stay on the card.
        var languages = entry.Languages ?? "Languages not specified";
        var concreteLanguages = entry.Languages is { } value && !value.TrimEnd().EndsWith('.');
        LanguageText.Text = concreteLanguages ? languages : "";
        LanguageText.Visibility = concreteLanguages ? Visibility.Visible : Visibility.Collapsed;

        DescriptionText.Text = entry.Description ?? "";
        AboutSection.Visibility = string.IsNullOrEmpty(entry.Description) ? Visibility.Collapsed : Visibility.Visible;
        CapabilityText.Text = (entry.SupportsCustomDictionary
            ? "Supports custom dictionary context hints."
            : "Custom dictionary hints are not supported.") + $"\n{languages}";
        LicenseDetailText.Text = $"{entry.License ?? "License not specified"}\n{entry.LicenseNotes ?? ""}".TrimEnd();
        SourceText.Text = $"{entry.Repo}\nPinned revision {entry.Revision}\n{entry.RemoteFile}";
        ByteCountText.Text = $"{entry.Bytes:N0} bytes";
        HashText.Text = $"SHA-256 {entry.Sha256}";

        AutomationProperties.SetAutomationId(PrimaryButton, $"ModelPrimary_{entry.Id}");
        AutomationProperties.SetAutomationId(UseButton, $"ModelUse_{entry.Id}");
        AutomationProperties.SetAutomationId(DetailsButton, $"ModelDetails_{entry.Id}");
        AutomationProperties.SetName(UseButton, $"Use {_name}");
        AutomationProperties.SetName(DetailsButton, $"Details for {_name}");

        _assetName = entry.Family switch
        {
            "moonshine_asr" => "moonshine",
            "qwen3_asr" => "qwen",
            "vibevoice_asr" or "vibevoice_asr_streaming" => "vibevoice",
            "nemotron_asr" => "nvidia",
            _ => null
        };
        Monogram.Text = char.ToUpperInvariant(entry.Family[0]).ToString();
        ApplyIcon();
    }

    public void Update(ModelDownloadSnapshot snapshot, bool idle, bool managerIdle, bool managerBusy, bool active, bool canSelect)
    {
        var entry = snapshot.Model;
        var state = snapshot.State;
        _state = state;

        // Not installed has no state label; the Download button already says it.
        var stateLabel = !snapshot.Supported ? "Unsupported" : active ? "In use" : StateLabel(state);
        StateRun.Text = stateLabel ?? "";
        SeparatorRun.Text = stateLabel is null ? "" : " \u00B7 ";
        StateDot.Visibility = stateLabel is null ? Visibility.Collapsed : Visibility.Visible;
        _stateDotBrushKey = !snapshot.Supported || state == ModelInstallState.Failed ? "SystemFillColorCriticalBrush"
            : "AccentGraphicBrush";
        ApplyStateDot();
        CardBorder.BorderBrush = (Brush)Application.Current.Resources[active ? "AccentGraphicBrush" : "CardStrokeColorDefaultBrush"];
        CardBorder.BorderThickness = new Thickness(active ? 1.5 : 1);

        // A paid download cost leaves the card once the model file exists.
        StatsText.Text = snapshot.HasModelFile || active ? $"{FormatBytes(entry.EstimatedMemoryBytes)} memory"
            : $"{FormatBytes(entry.Bytes)} download \u00B7 {FormatBytes(entry.EstimatedMemoryBytes)} memory";

        var showBar = state is ModelInstallState.Downloading or ModelInstallState.Paused or ModelInstallState.Verifying;
        DownloadProgress.Value = snapshot.Progress;
        DownloadProgress.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        // Verifying shows the state and bar only.
        ProgressText.Text = state == ModelInstallState.ReadyToInstall
            ? "Verified. Install when dictation is idle."
            : $"{FormatBytes(snapshot.DownloadedBytes)} / {FormatBytes(entry.Bytes)}" +
              (snapshot.BytesPerSecond > 0 ? $" / {FormatBytes((long)snapshot.BytesPerSecond)}/s" : "");
        ProgressText.Visibility = state is ModelInstallState.Downloading or ModelInstallState.Paused or ModelInstallState.ReadyToInstall
            ? Visibility.Visible : Visibility.Collapsed;

        ErrorInfo.Message = snapshot.Error ?? "";
        ErrorInfo.IsOpen = snapshot.Error is not null;

        // One primary action per card. While a transfer runs the primary action stops it; an installed
        // file has no primary action here because Use or the in-use state covers it.
        var transferring = state is ModelInstallState.Downloading or ModelInstallState.Verifying;
        // Accent marks a forward step only; Pause and Stop use the default style.
        PrimaryButton.Style = (Style)Application.Current.Resources[transferring ? "DefaultButtonStyle" : "AccentButtonStyle"];
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
        PrimaryButton.Visibility = !active && (transferring || !snapshot.HasModelFile) ? Visibility.Visible : Visibility.Collapsed;
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

        // Use appears only when it can be clicked; a disabled Use cannot explain itself.
        UseButton.Visibility = snapshot.HasModelFile && !active && snapshot.Supported && canSelect
            ? Visibility.Visible : Visibility.Collapsed;
        ActionRow.Visibility = PrimaryButton.Visibility == Visibility.Visible || UseButton.Visibility == Visibility.Visible
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

    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        ApplyIcon();
        ApplyStateDot();
    }

    // Theme brushes resolved from code do not follow theme changes, so the fill is reapplied on ActualThemeChanged.
    private void ApplyStateDot()
    {
        if (_stateDotBrushKey is { } key) { StateDot.Fill = (Brush)Application.Current.Resources[key]; }
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
        entry.Mode == "offline" ? "WAV files only" :
        entry.Preview == "live" ? "Live transcript" :
        entry.Preview == "buffered" ? "Buffered text" :
        "Text after you finish";

    internal static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GiB" :
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):F1} MiB" : $"{bytes:N0} bytes";

    private static string? StateLabel(ModelInstallState state) => state switch
    {
        ModelInstallState.NotInstalled => null,
        ModelInstallState.ReadyToInstall => "Ready to install",
        _ => state.ToString()
    };
}
