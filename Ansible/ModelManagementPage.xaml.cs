using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.System;
using Windows.Storage.Pickers;

namespace Ansible;

public sealed partial class ModelManagementPage : Page, IAsyncDisposable
{
    private readonly DictationSession _session;
    private readonly AppPaths _paths;
    private readonly Func<nint> _windowHandle;
    private readonly HttpClient _http;
    private readonly LocalDiagnosticLog _diagnostics;
    private readonly Func<Task> _transcribeFile;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ModelCard> _cardList = [];
    private const string OperationNoticeKey = "models-operation";
    private const string CatalogNoticeKey = "models-catalog";
    private ModelDownloadManager? _manager;
    private ThemeSettings? _themeSettings;
    private UiSessionObserver? _observer;
    private Task? _initialization;
    private Task? _disposal;
    private bool _loaded;
    private bool _dialogActive;
    private bool _transcribingFile;
    private bool _closed;

    private readonly AppNotices _notices;

    internal ModelManagementPage(DictationSession session, AppPaths paths, Func<nint> windowHandle,
        HttpClient http, Func<Task> transcribeFile, LocalDiagnosticLog diagnostics, AppNotices notices)
    {
        InitializeComponent();
        _session = session;
        _notices = notices;
        _paths = paths;
        _windowHandle = windowHandle;
        _http = http;
        _diagnostics = diagnostics;
        _transcribeFile = transcribeFile;
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
            var candidate = new ModelDownloadManager(catalog, families, _session.State.ModelsDirectory, _http, _diagnostics);
            try
            {
                if (_cardList.Count == 0)
                {
                    _themeSettings = ThemeSettings.CreateForWindowId(
                        Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle()));
                    foreach (var entry in catalog)
                    {
                        var card = new ModelCard();
                        card.Initialize(entry, _themeSettings);
                        card.PrimaryRequested += OnPrimaryRequested;
                        card.PauseRequested += OnPauseRequested;
                        card.RemoveRequested += OnRemoveRequested;
                        card.DiscardRequested += OnDiscardRequested;
                        card.UseRequested += OnUseRequested;
                        _cardList.Add(card);
                    }
                }
                candidate.Changed += ManagerChanged;
                _manager = candidate;
            }
            finally
            {
                // A failure during card setup must leave no half-built page or leaked manager so a
                // later Verify installed files retry can rebuild the catalog from a clean state.
                if (_manager is null)
                {
                    foreach (var card in _cardList) { card.DetachThemeEvents(); }
                    foreach (var rows in GroupRows) { rows.Children.Clear(); }
                    _cardList.Clear();
                    _themeSettings = null;
                    await candidate.DisposeAsync();
                }
            }
            await _manager.RefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
    }

    private void ManagerChanged()
    {
        if (!_closed && !DispatcherQueue.TryEnqueue(() => { if (_loaded && !_closed) { Render(); } }))
            System.Diagnostics.Debug.WriteLine("Ansible: model view dispatcher is closed.");
    }

    private StackPanel[] GroupRows => [InUseRows, InstalledRows, AvailableRows];

    private void Render()
    {
        if (_closed) { return; }
        var state = _session.State;
        var managerIdle = _manager is { IsBusy: false } && !_dialogActive;
        var idle = state.IsIdle && managerIdle;
        var managerBusy = _manager?.IsBusy == true;
        FolderText.Text = _manager?.DirectoryPath ?? state.ModelsDirectory;
        ToolTipService.SetToolTip(FolderMenuButton, FolderText.Text);
        FolderButton.IsEnabled = idle;
        RefreshButton.IsEnabled = _initialization is not { IsCompleted: false } && !managerBusy && !_dialogActive;
        FolderMenuButton.IsEnabled = !_dialogActive;
        TranscribeFileButton.IsEnabled = idle && state.SelectedModel is not null && !_dialogActive && !_transcribingFile;
        BackendText.Text = $"audio.cpp {state.BackendVersion} \u00B7 selected: {(state.Settings.Backend == NativeBackend.Vulkan ? "GPU (Vulkan)" : "CPU")}";
        BackendText.Visibility = string.IsNullOrEmpty(state.BackendVersion) ? Visibility.Collapsed : Visibility.Visible;

        // A missing catalog counts only after initialization finishes, so the page does not flash the notice
        // while the catalog is still loading.
        var catalogMissing = _manager is null && _initialization?.IsCompleted == true;
        _notices.Set(CatalogNoticeKey, catalogMissing ? new NoticeContent(NoticeSeverity.Error, "Model catalog unavailable",
            "Open Model folder, then Verify installed files to retry opening the catalog.", IsClosable: false) : null,
            NoticeArea.Models);

        if (_manager is null) { return; }
        // Selecting an installed model does not depend on the download manager.
        var canSelect = state.Phase == DictationPhase.Ready;
        var activeId = state.SelectedModel?.Id;
        var snapshots = new Dictionary<ModelCard, ModelDownloadSnapshot>();
        foreach (var card in _cardList) { snapshots[card] = _manager.Get(card.ModelId); }
        // A card that waits is never the one transferring, so any verifying card is another card.
        var busyReason = snapshots.Values.Any(snapshot => snapshot.State == ModelInstallState.Verifying)
            ? "Wait for the current verification to finish." : "Wait for the current model operation to finish.";
        foreach (var card in _cardList)
        {
            card.Update(snapshots[card], idle, managerIdle, managerBusy,
                active: activeId == card.ModelId, canSelect: canSelect, busyReason: busyReason);
        }
        PlaceCards(activeId, snapshots);
    }

    // In use holds the dictation model, then an installed diarization companion. Installed holds other model files.
    // Available holds the rest, including transfers, failures, and a companion that is not installed yet.
    private void PlaceCards(string? activeId, Dictionary<ModelCard, ModelDownloadSnapshot> snapshots)
    {
        var groups = new List<ModelCard>[] { [], [], [] };
        foreach (var card in _cardList)
        {
            var snapshot = snapshots[card];
            var companion = ModelSelectionPolicy.IsDiarizationTask(snapshot.Model.Task);
            groups[card.ModelId == activeId || companion && snapshot.HasModelFile ? 0 : snapshot.HasModelFile ? 1 : 2].Add(card);
        }
        groups[0] = [.. groups[0].OrderBy(card => ModelSelectionPolicy.IsDiarizationTask(snapshots[card].Model.Task))];

        // Render runs on every download tick, so only groups whose rows changed are rebuilt. Every changed group
        // is emptied before any is filled because a row can belong to one parent only.
        var rows = GroupRows;
        var changed = Enumerable.Range(0, rows.Length).Where(index => !rows[index].Children.SequenceEqual(groups[index])).ToArray();
        foreach (var index in changed) { rows[index].Children.Clear(); }
        foreach (var index in changed)
            foreach (var card in groups[index]) { rows[index].Children.Add(card); }
        InUseSection.Visibility = groups[0].Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InstalledSection.Visibility = groups[1].Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AvailableSection.Visibility = groups[2].Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUseRequested(string id)
    {
        var models = _session.State.Models;
        var index = -1;
        for (var candidate = 0; candidate < models.Count; candidate++)
        {
            if (models[candidate].Id == id) { index = candidate; break; }
        }
        if (index < 0)
        {
            _session.Notify(NoticeKind.Warning, "Model not loaded",
                "Open Model folder, then Verify installed files to load this model.", NoticeArea.Models);
        }
        else
        {
            try
            {
                _session.SelectModel(index);
                _notices.Confirm($"Dictation now uses {DisplayName(id)}.");
            }
            catch (Exception error) { _session.ReportUiError(error, NoticeArea.Models); }
        }
        Render();
    }

    private async void OnPrimaryRequested(string id)
    {
        if (_manager is null) { return; }
        ModelDownloadSnapshot selected;
        try { selected = _manager.Get(id); }
        catch (Exception error) { ShowError(error); return; }
        if (selected.State == ModelInstallState.ReadyToInstall)
        {
            await InstallAsync(id);
            return;
        }
        try
        {
            _notices.Set(OperationNoticeKey, null);
            await _manager.DownloadAsync(id, _lifetime.Token);
            if (!_closed && _session.State.IsIdle)
                await InstallAsync(id);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) { _session.Notify(NoticeKind.Information, "Download paused", "Partial weights remain available for Resume.", NoticeArea.Models); }
        }
        catch (Exception error) { ShowError(error); }
        finally { Render(); }
    }

    // Confirms only a file the manager reports as installed after the maintenance step completes.
    private async Task InstallAsync(string id)
    {
        if (_manager is null) { return; }
        if (await MaintainAsync(token => _manager.InstallAsync(id, token)) && !_closed && _manager.Get(id).HasModelFile)
            _notices.Confirm($"{DisplayName(id)} is installed.");
    }

    private string DisplayName(string id) => _cardList.FirstOrDefault(card => card.ModelId == id)?.DisplayName ?? id;

    private async void OnPauseRequested(string id)
    {
        try { if (_manager is not null) { await _manager.PauseAsync(); } }
        catch (Exception error) { ShowError(error); }
    }

    private async void OnRemoveRequested(string id) => await ConfirmRemovalAsync(id, false);
    private async void OnDiscardRequested(string id) => await ConfirmRemovalAsync(id, true);

    private async Task ConfirmRemovalAsync(string id, bool partial)
    {
        if (_manager is null) { return; }
        ModelDownloadSnapshot selected;
        try { selected = _manager.Get(id); }
        catch (Exception error) { ShowError(error); return; }
        _dialogActive = true;
        Render();
        try
        {
            // RemoveAsync deletes only the model file; DiscardPartialAsync deletes only the partial weights.
            var freed = partial ? selected.DownloadedBytes
                : File.Exists(selected.Path) ? new FileInfo(selected.Path).Length : selected.Model.Bytes;
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = freed > 0 ? $"Frees {ModelCard.FormatBytes(freed)} on this device." : "Deletes the saved resume data.",
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
            });
            content.Children.Add(new TextBlock
            {
                Text = partial ? "Deletes partial weights and their resume data." : "You can download it again from this page.",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"]
            });
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                Title = partial ? "Discard partial download?" : $"Remove {selected.Model.DisplayName ?? selected.Model.Id}?",
                Content = content,
                PrimaryButtonText = partial ? "Discard" : "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await MaintainAsync(token => partial ? _manager.DiscardPartialAsync(id, token) :
                    _manager.RemoveAsync(id, token));
        }
        catch (Exception error) { ShowError(error); }
        finally { _dialogActive = false; Render(); }
    }

    private async void RefreshClicked(object sender, RoutedEventArgs args)
    {
        if (_closed || _initialization is { IsCompleted: false }) { return; }
        if (_manager is null)
        {
            _notices.Set(OperationNoticeKey, null);
            _initialization = InitializeAsync();
            Render();
            await _initialization;
            Render();
            return;
        }
        await RefreshAsync();
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
            if (folder is not null && await MaintainAsync(token =>
                {
                    token.ThrowIfCancellationRequested();
                    _manager.ChangeDirectory(folder.Path);
                    return Task.CompletedTask;
                }, folder.Path))
                await RefreshAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { _dialogActive = false; Render(); }
    }

    private async void TranscribeFileClicked(object sender, RoutedEventArgs args)
    {
        if (_closed || _transcribingFile || !_session.State.IsIdle || _session.State.SelectedModel is null) return;
        _transcribingFile = true;
        Render();
        try { await _transcribeFile(); }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Models); Render(); }
        finally { _transcribingFile = false; Render(); }
    }

    private async Task RefreshAsync()
    {
        if (_manager is null) { return; }
        try
        {
            _notices.Set(OperationNoticeKey, null);
            await _manager.RefreshAsync(_lifetime.Token);
            if (!_closed && _session.State.IsIdle)
                await MaintainAsync(_ => Task.CompletedTask, _manager.DirectoryPath);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) { _session.Notify(NoticeKind.Information, "Verification paused", "Verify installed files again to complete the checks.", NoticeArea.Models); }
        }
        catch (Exception error) { ShowError(error); }
        finally { Render(); }
    }

    private async Task<bool> MaintainAsync(Func<CancellationToken, Task> action, string? directory = null)
    {
        try
        {
            _notices.Set(OperationNoticeKey, null);
            var outcome = await _session.MaintainModelsAsync(action, directory);
            if (outcome.Error is not null) { ShowError(outcome.Error); }
            return outcome.Kind == SessionOutcomeKind.Completed;
        }
        catch (Exception error) { ShowError(error); return false; }
        finally { Render(); }
    }

    // The page notice replaces a session notice here: both would show the same message on this page.
    private void ShowError(Exception error)
    {
        if (_closed) { return; }
        _notices.Set(OperationNoticeKey, new NoticeContent(NoticeSeverity.Error, "Model operation failed", error.Message),
            NoticeArea.Models);
        Render();
    }

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
        foreach (var card in _cardList) { card.DetachThemeEvents(); }
        _themeSettings = null;
        if (_manager is not null)
        {
            _manager.Changed -= ManagerChanged;
            await _manager.DisposeAsync();
        }
        _lifetime.Dispose();
        _http.Dispose();
    }
}
