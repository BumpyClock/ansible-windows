using DictationPoc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.System;
using Windows.Foundation;

namespace DictationPoc;

public sealed partial class ModelCard : UserControl
{
    private ThemeSettings? _themeSettings;
    private string? _assetName;
    private string _name = "";
    private Uri? _iconUri;
    private bool _iconFailureLogged;

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

    public void Initialize(NativeModelEntry entry, ThemeSettings themeSettings)
    {
        _themeSettings = themeSettings;
        themeSettings.Changed += OnThemeSettingsChanged;
        ModelId = entry.Id;
        _name = entry.DisplayName ?? entry.Id;
        NameText.Text = _name;
        AutomationProperties.SetName(NameText, _name);
        VariantText.Text = $"{ModeLabel(entry.Mode)} \u00B7 {entry.Precision ?? "Precision not specified"}";
        BehaviorText.Text = BehaviorLabel(entry);
        LanguageText.Text = entry.Languages ?? "Languages not specified";
        DownloadSizeText.Text = FormatBytes(entry.Bytes);
        MemoryText.Text = FormatBytes(entry.EstimatedMemoryBytes);
        LicenseText.Text = entry.License ?? "License not specified";

        DescriptionText.Text = entry.Description ?? "";
        DescriptionText.Visibility = string.IsNullOrEmpty(entry.Description) ? Visibility.Collapsed : Visibility.Visible;
        CapabilityText.Text = entry.SupportsCustomDictionary
            ? "Supports custom dictionary context hints."
            : "Custom dictionary hints are not supported.";
        LicenseDetailText.Text = $"{entry.License ?? "License not specified"}\n{entry.LicenseNotes ?? ""}".TrimEnd();
        SourceText.Text = $"{entry.Repo}\nPinned revision {entry.Revision}\n{entry.RemoteFile}";

        AutomationProperties.SetAutomationId(PrimaryButton, $"ModelPrimary_{entry.Id}");
        AutomationProperties.SetAutomationId(PauseButton, $"ModelPause_{entry.Id}");
        AutomationProperties.SetAutomationId(RemoveButton, $"ModelRemove_{entry.Id}");
        AutomationProperties.SetAutomationId(DiscardButton, $"ModelDiscard_{entry.Id}");
        AutomationProperties.SetAutomationId(DetailsButton, $"ModelDetails_{entry.Id}");
        AutomationProperties.SetName(DetailsButton, $"Details for {_name}");
        AutomationProperties.SetName(PauseButton, $"Pause download of {_name}");
        AutomationProperties.SetName(RemoveButton, $"Remove {_name}");
        AutomationProperties.SetName(DiscardButton, $"Discard partial download of {_name}");

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

    public void Update(ModelDownloadSnapshot snapshot, bool idle, bool managerIdle, bool managerBusy)
    {
        var entry = snapshot.Model;
        var state = snapshot.State;

        StateText.Text = snapshot.Supported ? StateLabel(state) : "Unsupported by this compiled backend";

        var showBar = state is ModelInstallState.Downloading or ModelInstallState.Paused or ModelInstallState.Verifying;
        DownloadProgress.Value = snapshot.Progress;
        DownloadProgress.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(DownloadProgress, state == ModelInstallState.Verifying
            ? $"File verification progress for {_name}"
            : $"Model download progress for {_name}");
        ProgressText.Text = state == ModelInstallState.ReadyToInstall
            ? "Weights verified. Select Install verified model when dictation is idle."
            : state == ModelInstallState.Verifying
                ? "Checking exact length and SHA-256 before installation."
                : $"{FormatBytes(snapshot.DownloadedBytes)} / {FormatBytes(entry.Bytes)}" +
                  (snapshot.BytesPerSecond > 0 ? $" / {FormatBytes((long)snapshot.BytesPerSecond)}/s" : "");
        ProgressText.Visibility = showBar || state == ModelInstallState.ReadyToInstall
            ? Visibility.Visible : Visibility.Collapsed;

        ErrorInfo.Message = snapshot.Error ?? "";
        ErrorInfo.IsOpen = snapshot.Error is not null;

        PrimaryButton.Content = state switch
        {
            ModelInstallState.ReadyToInstall => "Install verified model",
            ModelInstallState.Paused => "Resume",
            ModelInstallState.Failed => "Retry download",
            _ => "Download"
        };
        PrimaryButton.IsEnabled = snapshot.Supported && (state == ModelInstallState.ReadyToInstall
            ? idle
            : managerIdle && !snapshot.HasModelFile &&
              state is ModelInstallState.NotInstalled or ModelInstallState.Paused or ModelInstallState.Failed);
        PrimaryButton.Visibility = snapshot.HasModelFile ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(PrimaryButton, $"{PrimaryButton.Content} {_name}");

        PauseButton.IsEnabled = managerBusy && state is ModelInstallState.Downloading or ModelInstallState.Verifying;
        PauseButton.Visibility = state is ModelInstallState.Downloading or ModelInstallState.Verifying
            ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(PauseButton, state == ModelInstallState.Verifying
            ? $"Pause file verification for {_name}"
            : $"Pause download of {_name}");

        RemoveButton.IsEnabled = idle && snapshot.HasModelFile;
        RemoveButton.Visibility = snapshot.HasModelFile ? Visibility.Visible : Visibility.Collapsed;

        DiscardButton.IsEnabled = idle && snapshot.HasPartial;
        DiscardButton.Visibility = snapshot.HasPartial ? Visibility.Visible : Visibility.Collapsed;

        IntegrityText.Text = $"{entry.Id} / {entry.Family} / {entry.Precision ?? "Precision not specified"}\n" +
            $"{entry.Bytes:N0} bytes\nSHA-256 {entry.Sha256}\n{snapshot.Path}";
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

    private void OnThemeSettingsChanged(ThemeSettings sender, object args)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (_themeSettings is not null) { ApplyIcon(); }
            }))
            System.Diagnostics.Debug.WriteLine($"Local Voice: high-contrast icon update for '{ModelId}' could not be dispatched.");
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
                $"Local Voice: model icon '{_iconUri}' for '{ModelId}' failed to load: {e.ErrorMessage}. Using monogram fallback.");
        }
    }

    private bool IsHighContrast() => (_themeSettings ??
        throw new InvalidOperationException("Initialize the model card before loading it.")).HighContrast;

    private void PrimaryClicked(object sender, RoutedEventArgs e) => PrimaryRequested?.Invoke(ModelId);
    private void PauseClicked(object sender, RoutedEventArgs e) => PauseRequested?.Invoke(ModelId);
    private void RemoveClicked(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(ModelId);
    private void DiscardClicked(object sender, RoutedEventArgs e) => DiscardRequested?.Invoke(ModelId);

    private static string ModeLabel(string mode) => mode switch
    {
        "streaming" => "Streaming",
        "offline" => "Offline",
        _ => mode
    };

    private static string BehaviorLabel(NativeModelEntry entry) =>
        entry.Mode == "offline" ? "WAV transcription only. No microphone dictation." :
        entry.Preview == "live" ? "Live transcript." :
        entry.Preview == "buffered" ? "Buffered recognition. Text updates are not continuous." :
        "Text after Finish dictation.";

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GiB" :
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):F1} MiB" : $"{bytes:N0} bytes";

    private static string StateLabel(ModelInstallState state) => state switch
    {
        ModelInstallState.NotInstalled => "Not installed",
        ModelInstallState.ReadyToInstall => "Ready to install",
        _ => state.ToString()
    };
}

// Flow layout for the footer: keeps license + actions on one row when they fit and reflows buttons
// onto additional rows when the card is too narrow, so no action is clipped at the two-column width.
public sealed partial class ActionWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        double x = 0, rowHeight = 0, totalHeight = 0, widest = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) { continue; }
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + HorizontalSpacing + size.Width > limit)
            {
                totalHeight += rowHeight + VerticalSpacing;
                widest = Math.Max(widest, x);
                x = 0;
                rowHeight = 0;
            }
            if (x > 0) { x += HorizontalSpacing; }
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        totalHeight += rowHeight;
        widest = Math.Max(widest, x);
        return new Size(double.IsInfinity(limit) ? widest : limit, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) { continue; }
            var size = child.DesiredSize;
            if (x > 0 && x + HorizontalSpacing + size.Width > finalSize.Width)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }
            if (x > 0) { x += HorizontalSpacing; }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return finalSize;
    }
}
