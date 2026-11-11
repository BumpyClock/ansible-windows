using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
    private ModelInstallState _state;
    private bool _removeDiscardsPartial;
    private bool _companion;
    private const string MemoryEstimateTip = "Memory is an estimate made before loading, not measured use.";
    // Segoe Fluent Icons: Microphone for a dictation model, People for the meeting speaker companion.
    private const string DictationGlyph = "\uE720";
    private const string CompanionGlyph = "\uE716";
    // Below this row width the buttons move under the name so the name and badges keep readable space.
    private const double StackedActionsWidth = 600;

    public ModelCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ActualThemeChanged += OnThemeChanged;
    }

    public string ModelId { get; private set; } = "";

    /// <summary>The name the row shows: the catalog display name without its quantization suffix.</summary>
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
        var catalogName = entry.DisplayName ?? entry.Id;
        _name = ModelCardText.PlainName(entry);
        _companion = ModelSelectionPolicy.IsDiarizationTask(entry.Task);
        NameText.Text = _name;
        AutomationProperties.SetName(NameText, _name);
        ToolTipService.SetToolTip(NameText, catalogName == _name ? null : catalogName);
        SummaryText.Text = ModelCardText.Summary(entry);
        TileGlyph.Glyph = _companion ? CompanionGlyph : DictationGlyph;

        DescriptionText.Text = entry.Description ?? "";
        AboutRow.Visibility = string.IsNullOrEmpty(entry.Description) ? Visibility.Collapsed : Visibility.Visible;
        LanguagesText.Text = entry.Languages ?? "Languages not specified";
        CustomWordsText.Text = entry.SupportsCustomDictionary
            ? "Uses your Dictionary words as recognition hints."
            : "Does not use Dictionary words.";
        LicenseDetailText.Text = $"{entry.License ?? "License not specified"}\n{entry.LicenseNotes ?? ""}".TrimEnd();
        CatalogNameText.Text = catalogName == entry.Id ? entry.Id : $"{catalogName}\n{entry.Id}";
        SourceText.Text = $"{entry.Repo}\nPinned revision {entry.Revision}\n{entry.RemoteFile}";
        ByteCountText.Text = $"{entry.Bytes:N0} bytes";
        HashText.Text = $"SHA-256 {entry.Sha256}";
        PrecisionText.Text = entry.Precision is { } precision ? $"Precision {precision}" : "";
        PrecisionText.Visibility = entry.Precision is null ? Visibility.Collapsed : Visibility.Visible;

        AutomationProperties.SetAutomationId(PrimaryButton, $"ModelPrimary_{entry.Id}");
        AutomationProperties.SetAutomationId(UseButton, $"ModelUse_{entry.Id}");
        AutomationProperties.SetAutomationId(MoreButton, $"ModelMore_{entry.Id}");
        AutomationProperties.SetAutomationId(DetailsItem, $"ModelDetails_{entry.Id}");
        AutomationProperties.SetName(UseButton, $"Use {_name} for dictation");
        AutomationProperties.SetName(MoreButton, $"More options for {_name}");
        ApplyDetailsState();

        _assetName = entry.Family switch
        {
            "moonshine_asr" => "moonshine",
            "qwen3_asr" => "qwen",
            "vibevoice_asr" or "vibevoice_asr_streaming" => "vibevoice",
            _ => null
        };
        ApplyIcon();
    }

    // busyReason explains a Download or Resume that waits on another model operation.
    public void Update(ModelDownloadSnapshot snapshot, bool idle, bool managerIdle, bool managerBusy, bool active, bool canSelect,
        string busyReason = "Wait for the current model operation to finish.")
    {
        var state = snapshot.State;
        _state = state;

        // The group heading says In use, Installed or Available. One badge says what an in-use model is used for;
        // the other calls out a state the person must act on.
        InUseBadge.Text = _companion ? "In use for meetings" : "In use for dictation";
        InUseBadge.Visibility = active || _companion && snapshot.HasModelFile ? Visibility.Visible : Visibility.Collapsed;
        var stateLabel = ModelCardText.StateLabel(snapshot.Supported, state);
        StateBadge.Text = stateLabel ?? "";
        StateBadge.Visibility = stateLabel is null ? Visibility.Collapsed : Visibility.Visible;

        FootprintText.Text = ModelCardText.Footprint(snapshot, installed: snapshot.HasModelFile || active);
        ToolTipService.SetToolTip(FootprintText, state is ModelInstallState.Downloading or ModelInstallState.Paused ? null : MemoryEstimateTip);

        var showProgress = state is ModelInstallState.Downloading or ModelInstallState.Paused or ModelInstallState.Verifying;
        ProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Value = snapshot.Progress;
        // Verification reports no progress, so the bar shows activity rather than a fill level.
        DownloadProgress.IsIndeterminate = state == ModelInstallState.Verifying;
        ProgressText.Text = state switch
        {
            ModelInstallState.Verifying => "Checking the download",
            ModelInstallState.Paused => $"Paused at {snapshot.Progress:F0}%",
            _ => $"Downloading {snapshot.Progress:F0}%"
        };
        AutomationProperties.SetName(DownloadProgress, state == ModelInstallState.Verifying
            ? $"File verification progress for {_name}"
            : $"Model download progress for {_name}");

        if (snapshot.Error is { } error)
            ErrorNotice.Show(new NoticeContent(NoticeSeverity.Error, "Model file problem", error, IsClosable: false));
        ErrorNotice.Visibility = snapshot.Error is null ? Visibility.Collapsed : Visibility.Visible;

        // One transfer action per row, always a default button: a page of models has no single primary action.
        // While a transfer runs the action stops it; an installed file has no transfer action because Use for
        // dictation or the In use group covers it.
        var transferring = state is ModelInstallState.Downloading or ModelInstallState.Verifying;
        var label = ModelCardText.PrimaryAction(state);
        PrimaryButton.Content = label;
        PrimaryButton.IsEnabled = transferring ? managerBusy : snapshot.Supported && (state == ModelInstallState.ReadyToInstall
            ? idle
            : managerIdle && !snapshot.HasModelFile &&
              state is ModelInstallState.NotInstalled or ModelInstallState.Paused or ModelInstallState.Failed);
        var showPrimary = !active && (transferring || !snapshot.HasModelFile);
        PrimaryHost.Visibility = showPrimary ? Visibility.Visible : Visibility.Collapsed;
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
            _ => $"{label} {_name}"
        });

        // Use for dictation appears only when it can be clicked; a disabled button cannot explain itself. A row that
        // is still downloading or verifying offers Pause or Stop only, so selection follows a finished install.
        // The diarization companion never offers it: it is not a dictation model.
        var showUse = !_companion && state == ModelInstallState.Installed && !active && snapshot.Supported && canSelect;
        UseButton.Visibility = showUse ? Visibility.Visible : Visibility.Collapsed;
        // An empty button group takes no space, so it adds no gap when it sits under the name.
        ActionsPanel.Visibility = showProgress || showPrimary || showUse ? Visibility.Visible : Visibility.Collapsed;

        // An installed file takes precedence; after removal a leftover partial surfaces as Discard.
        _removeDiscardsPartial = !snapshot.HasModelFile && snapshot.HasPartial;
        var removable = snapshot.HasModelFile || snapshot.HasPartial;
        RemoveSeparator.Visibility = RemoveItem.Visibility = removable ? Visibility.Visible : Visibility.Collapsed;
        RemoveItem.IsEnabled = idle;
        RemoveItem.Text = _removeDiscardsPartial ? "Discard partial download" : "Remove from this device";
        AutomationProperties.SetAutomationId(RemoveItem, _removeDiscardsPartial ? $"ModelDiscard_{ModelId}" : $"ModelRemove_{ModelId}");
        AutomationProperties.SetName(RemoveItem, _removeDiscardsPartial ? $"Discard partial download of {_name}" : $"Remove {_name}");

        LocationText.Text = snapshot.Path;
    }

    internal void DetachThemeEvents()
    {
        Loaded -= OnLoaded;
        if (_themeSettings is { } settings) { settings.Changed -= OnThemeSettingsChanged; }
        _themeSettings = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyIcon();

    // More keeps its corner; only the transfer and selection buttons move under the name.
    private void RowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var stacked = args.NewSize.Width < StackedActionsWidth;
        Grid.SetRow(ActionsPanel, stacked ? 1 : 0);
        Grid.SetColumn(ActionsPanel, stacked ? 1 : 2);
        Grid.SetColumnSpan(ActionsPanel, stacked ? 3 : 1);
        ActionsPanel.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        ActionsPanel.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    private void DetailsClicked(object sender, RoutedEventArgs e)
    {
        DetailsPanel.Visibility = DetailsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ApplyDetailsState();
    }

    private void ApplyDetailsState() =>
        DetailsItem.Text = DetailsPanel.Visibility == Visibility.Visible ? "Hide details" : "Show details";

    private void OnThemeSettingsChanged(ThemeSettings sender, object args)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (_themeSettings is not null) { ApplyIcon(); }
            }))
            System.Diagnostics.Debug.WriteLine($"Ansible: high-contrast icon update for '{ModelId}' could not be dispatched.");
    }

    // A mark with a "{name}-dark.png" loads that file in the dark theme.
    private static bool HasDarkVariant(string assetName) => assetName == "vibevoice";

    // The glyph shows until a maker's mark opens, and stays when there is none, High Contrast is on, or loading
    // fails. The mark sits on the themed tile.
    private void ApplyIcon()
    {
        TileGlyph.Visibility = Visibility.Visible;
        if (_assetName is null || IsHighContrast())
        {
            _iconUri = null;
            IconImage.Source = null;
            IconImage.Visibility = Visibility.Collapsed;
            return;
        }
        var file = HasDarkVariant(_assetName) && ActualTheme == ElementTheme.Dark ? $"{_assetName}-dark.png" : $"{_assetName}.png";
        _iconUri = new Uri($"ms-appx:///Assets/ModelIcons/{file}");
        _iconFailureLogged = false;
        IconImage.Visibility = Visibility.Visible;
        IconImage.Source = new BitmapImage(_iconUri);
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => ApplyIcon();

    private void IconOpened(object sender, RoutedEventArgs e) => TileGlyph.Visibility = Visibility.Collapsed;

    private void IconFailed(object sender, ExceptionRoutedEventArgs e)
    {
        IconImage.Visibility = Visibility.Collapsed;
        IconImage.Source = null;
        TileGlyph.Visibility = Visibility.Visible;
        if (!_iconFailureLogged)
        {
            _iconFailureLogged = true;
            System.Diagnostics.Debug.WriteLine(
                $"Ansible: model icon '{_iconUri}' for '{ModelId}' failed to load: {e.ErrorMessage}. Using the glyph tile.");
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
}
