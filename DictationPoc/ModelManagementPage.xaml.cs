using DictationPoc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DictationPoc;

public sealed partial class ModelManagementPage : Page, IAsyncDisposable
{
    private readonly DictationSession _session;
    private readonly AppPaths _paths;
    private readonly Func<nint> _windowHandle;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ComboBoxItem> _items = [];
    private ModelDownloadManager? _manager;
    private UiSessionObserver? _observer;
    private Task? _initialization;
    private Task? _disposal;
    private bool _loaded;
    private bool _dialogActive;
    private bool _closed;
    private string? _error;

    internal ModelManagementPage(DictationSession session, AppPaths paths, Func<nint> windowHandle, HttpClient http)
    {
        InitializeComponent();
        _session = session;
        _paths = paths;
        _windowHandle = windowHandle;
        _http = http;
        Loaded += PageLoaded;
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _observer?.Dispose();
            _observer = null;
        };
    }

    private async void PageLoaded(object sender, RoutedEventArgs args)
    {
        _loaded = true;
        _observer ??= new UiSessionObserver(_session, DispatcherQueue, _ => Render());
        _initialization ??= InitializeAsync();
        await _initialization;
        Render();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var catalog = await NativeModelCatalog.LoadAsync(_paths.ModelCatalog, _lifetime.Token);
            var families = await NativeAudioEngine.GetSupportedFamiliesAsync(_paths.NativeLibrary, _lifetime.Token);
            _manager = new ModelDownloadManager(catalog, families, _session.State.ModelsDirectory, _http);
            _manager.Changed += ManagerChanged;
            foreach (var model in catalog)
            {
                var item = new ComboBoxItem { Tag = model.Id };
                _items.Add(item);
                CatalogBox.Items.Add(item);
            }
            CatalogBox.SelectedIndex = 0;
            await _manager.RefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
    }

    private void ManagerChanged()
    {
        if (!_closed && !DispatcherQueue.TryEnqueue(() => { if (_loaded && !_closed) { Render(); } }))
            System.Diagnostics.Debug.WriteLine("Local Voice: model view dispatcher is closed.");
    }

    private ModelDownloadSnapshot? Selection => CatalogBox.SelectedItem is ComboBoxItem { Tag: string id }
        ? _manager?.Get(id) : null;

    private void Render()
    {
        if (_closed) { return; }
        var state = _session.State;
        var managerIdle = _manager is { IsBusy: false } && !_dialogActive;
        var idle = state.IsIdle && managerIdle;
        FolderText.Text = _manager?.DirectoryPath ?? state.ModelsDirectory;
        FolderButton.IsEnabled = idle;
        RefreshButton.IsEnabled = _manager?.IsBusy != true && !_dialogActive;
        CatalogBox.IsEnabled = _manager is not null && !_dialogActive;
        if (_manager is not null)
        {
            foreach (var item in _items)
            {
                var model = _manager.Get((string)item.Tag);
                item.Content = $"{model.Model.DisplayName ?? model.Model.Id} / {StateLabel(model.State)}" +
                    (model.Supported ? "" : " / backend unavailable");
            }
        }
        StatusInfo.Severity = _error is not null ? InfoBarSeverity.Error :
            state.Notice.Kind == NoticeKind.Error ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        StatusInfo.Title = _error is not null ? "Model operation failed" :
            _manager is null ? "Catalog unavailable" :
            state.Phase == DictationPhase.MaintainingModels ? "Maintaining models" :
            state.Models.Count == 0 ? "Choose a model to get started" : "On-device recognition";
        StatusInfo.Message = _error ?? (_manager is null ? "Select Verify installed files to retry opening the catalog." :
            !state.IsIdle ? state.Notice.Message :
            $"{state.Models.Count} models available for recognition. Downloads require your explicit selection.");
        var selected = Selection;
        ModelCard.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (selected is null) { return; }
        var entry = selected.Model;
        NameText.Text = entry.DisplayName ?? entry.Id;
        DescriptionText.Text = entry.Description ?? "";
        CapabilitiesText.Text = $"{entry.Languages ?? "Languages not specified"} / {entry.Precision ?? "Precision not specified"}\n" +
            (entry.Mode == "offline" ? "WAV transcription only. No microphone dictation." :
                entry.Preview == "live" ? "Streaming input with incremental transcript events." :
                entry.Preview == "buffered" ? "Streaming input; buffered decoding, not continuous text." :
                "Streaming input; transcript after Finish.");
        SizeText.Text = $"Download {FormatBytes(entry.Bytes)} ({entry.Bytes:N0} bytes). Estimated admission memory {FormatBytes(entry.EstimatedMemoryBytes)}.";
        StateText.Text = selected.Supported ? StateLabel(selected.State) : "Unsupported by this compiled backend";
        DownloadProgress.Value = selected.Progress;
        DownloadProgress.Visibility = selected.State is ModelInstallState.Downloading or ModelInstallState.Paused or
            ModelInstallState.Verifying ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = selected.State == ModelInstallState.ReadyToInstall ?
            "Weights verified. Select Install verified model when dictation is idle." :
            selected.State == ModelInstallState.Verifying ? "Checking exact length and SHA-256 before installation." :
            $"{FormatBytes(selected.DownloadedBytes)} / {FormatBytes(entry.Bytes)}" +
            (selected.BytesPerSecond > 0 ? $" / {FormatBytes((long)selected.BytesPerSecond)}/s" : "");
        ErrorText.Text = selected.Error ?? "";
        ErrorText.Visibility = selected.Error is null ? Visibility.Collapsed : Visibility.Visible;
        DownloadButton.Content = selected.State == ModelInstallState.ReadyToInstall ? "Install verified model" :
            selected.State == ModelInstallState.Paused ? "Resume" :
            selected.State == ModelInstallState.Failed ? "Retry download" : "Download";
        DownloadButton.IsEnabled = selected.Supported && (selected.State == ModelInstallState.ReadyToInstall ? idle :
            managerIdle && !selected.HasModelFile &&
            selected.State is ModelInstallState.NotInstalled or ModelInstallState.Paused or ModelInstallState.Failed);
        PauseButton.IsEnabled = _manager?.IsBusy == true && selected.State is ModelInstallState.Downloading or ModelInstallState.Verifying;
        RemoveButton.IsEnabled = idle && selected.HasModelFile;
        DiscardButton.IsEnabled = idle && selected.HasPartial;
        DiscardButton.Visibility = selected.HasPartial ? Visibility.Visible : Visibility.Collapsed;
        LicenseText.Text = $"{entry.License ?? "License not specified"}\n{entry.LicenseNotes}";
        SourceText.Text = $"{entry.Repo}\nPinned revision {entry.Revision}\n{entry.RemoteFile}";
        IntegrityText.Text = $"{entry.Id} / {entry.Family}\nSHA-256 {entry.Sha256}\n{selected.Path}";
    }

    private void ModelChanged(object sender, SelectionChangedEventArgs args) => Render();

    private async void RefreshClicked(object sender, RoutedEventArgs args)
    {
        if (_manager is null)
        {
            _error = null;
            _initialization = InitializeAsync();
            await _initialization;
            Render();
            return;
        }
        await RefreshAsync();
    }

    private async void DownloadClicked(object sender, RoutedEventArgs args)
    {
        if (Selection is not { } selected || _manager is null) { return; }
        if (selected.State == ModelInstallState.ReadyToInstall)
        {
            await MaintainAsync(token => _manager.InstallAsync(selected.Model.Id, token));
            return;
        }
        try
        {
            _error = null;
            await _manager.DownloadAsync(selected.Model.Id, _lifetime.Token);
            if (!_closed && _session.State.IsIdle)
                await MaintainAsync(token => _manager.InstallAsync(selected.Model.Id, token));
        }
        catch (OperationCanceledException)
        {
            if (!_closed) { _session.Notify(NoticeKind.Information, "Download paused", "Partial weights remain available for Resume."); }
        }
        catch (Exception error) { ShowError(error); }
        finally { Render(); }
    }

    private async void PauseClicked(object sender, RoutedEventArgs args)
    {
        try { if (_manager is not null) { await _manager.PauseAsync(); } }
        catch (Exception error) { ShowError(error); }
    }

    private async void RemoveClicked(object sender, RoutedEventArgs args) => await ConfirmRemovalAsync(false);
    private async void DiscardClicked(object sender, RoutedEventArgs args) => await ConfirmRemovalAsync(true);

    private async Task ConfirmRemovalAsync(bool partial)
    {
        if (Selection is not { } selected || _manager is null) { return; }
        _dialogActive = true;
        Render();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = partial ? "Discard partial download?" : "Remove model file?",
                Content = $"{selected.Model.DisplayName ?? selected.Model.Id}\n{selected.Path}" +
                    (partial ? "\nOnly its partial weights and resume metadata are deleted. A complete model stays installed." :
                        "\nThe native model is released first. Download the weights again to restore recognition."),
                PrimaryButtonText = partial ? "Discard partial" : "Remove file",
                CloseButtonText = "Keep files",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await MaintainAsync(token => partial ? _manager.DiscardPartialAsync(selected.Model.Id, token) :
                    _manager.RemoveAsync(selected.Model.Id, token));
        }
        catch (Exception error) { ShowError(error); }
        finally { _dialogActive = false; Render(); }
    }

    private async void ChooseFolderClicked(object sender, RoutedEventArgs args)
    {
        if (_manager is null) { return; }
        _dialogActive = true;
        Render();
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && await MaintainAsync(async token =>
                {
                    _manager.ChangeDirectory(folder.Path);
                    await _paths.SaveModelsDirectoryAsync(folder.Path, token);
                }, folder.Path))
                await RefreshAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { _dialogActive = false; Render(); }
    }

    private async Task RefreshAsync()
    {
        if (_manager is null) { return; }
        try
        {
            _error = null;
            await _manager.RefreshAsync(_lifetime.Token);
            if (!_closed && _session.State.IsIdle)
                await MaintainAsync(_ => Task.CompletedTask, _manager.DirectoryPath);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) { _session.Notify(NoticeKind.Information, "Verification paused", "Verify installed files again to complete the checks."); }
        }
        catch (Exception error) { ShowError(error); }
        finally { Render(); }
    }

    private async Task<bool> MaintainAsync(Func<CancellationToken, Task> action, string? directory = null)
    {
        try
        {
            _error = null;
            var outcome = await _session.MaintainModelsAsync(action, directory);
            if (outcome.Error is not null) { ShowError(outcome.Error); }
            return outcome.Kind == SessionOutcomeKind.Completed;
        }
        catch (Exception error) { ShowError(error); return false; }
        finally { Render(); }
    }

    private void ShowError(Exception error)
    {
        _error = error.Message;
        _session.ReportUiError(error);
        Render();
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GiB" :
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):F1} MiB" : $"{bytes:N0} bytes";

    private static string StateLabel(ModelInstallState state) => state switch
    {
        ModelInstallState.NotInstalled => "Not installed",
        ModelInstallState.ReadyToInstall => "Ready to install",
        _ => state.ToString()
    };

    public ValueTask DisposeAsync()
    {
        if (_disposal?.IsFaulted == true) { _disposal = null; }
        return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _closed = true;
        _lifetime.Cancel();
        _observer?.Dispose();
        Loaded -= PageLoaded;
        if (_initialization is not null) { await _initialization; }
        if (_manager is not null)
        {
            _manager.Changed -= ManagerChanged;
            await _manager.DisposeAsync();
        }
        _lifetime.Dispose();
        _http.Dispose();
    }
}
