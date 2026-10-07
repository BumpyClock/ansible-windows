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
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ModelCard> _cardList = [];
    private const double TwoColumnThreshold = 620;
    private int _columns;
    private IReadOnlyList<ModelCard> _ordered = [];
    private ModelCard[] _laidOut = [];
    private ModelDownloadManager? _manager;
    private ThemeSettings? _themeSettings;
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
                _ordered = _cardList.ToArray();
                LayoutCards(_ordered, ModelsGrid.ActualWidth >= TwoColumnThreshold ? 2 : 1);
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

    private void OnModelsGridSizeChanged(object sender, SizeChangedEventArgs args) =>
        LayoutCards(_ordered, args.NewSize.Width >= TwoColumnThreshold ? 2 : 1);

    // Children are re-added in display order so keyboard navigation follows the visual order; the
    // StaggeredPanel packs cards of different heights into the shortest column.
    private void LayoutCards(IReadOnlyList<ModelCard> cards, int columns)
    {
        if (columns < 1) { columns = 1; }
        ModelsGrid.Columns = columns;
        if (_columns == columns && _laidOut.SequenceEqual(cards)) { return; }
        _columns = columns;
        _laidOut = cards.ToArray();
        ModelsGrid.Children.Clear();
        foreach (var card in cards) { ModelsGrid.Children.Add(card); }
    }

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
        RefreshButton.IsEnabled = !managerBusy && !_dialogActive;
        FolderMenuButton.IsEnabled = !_dialogActive;
        BackendText.Text = $"audio.cpp {state.BackendVersion} \u00B7 native CPU";
        BackendText.Visibility = string.IsNullOrEmpty(state.BackendVersion) ? Visibility.Collapsed : Visibility.Visible;

        // Warnings and errors only. A missing catalog counts only after initialization finishes, so the
        // page does not flash "Catalog unavailable" while the catalog is still loading.
        var attention = state.Notice.Kind is NoticeKind.Warning or NoticeKind.Error;
        var catalogMissing = _manager is null && _initialization?.IsCompleted == true;
        StatusInfo.Severity = _error is not null || catalogMissing || state.Notice.Kind == NoticeKind.Error
            ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        StatusInfo.IsOpen = _error is not null || catalogMissing || attention;
        StatusInfo.Title = _error is not null ? "Model operation failed" :
            catalogMissing ? "Catalog unavailable" :
            attention ? state.Notice.Title : "";
        StatusInfo.Message = _error ?? (catalogMissing ? "Open Model folder, then Verify installed files to retry opening the catalog." :
            attention ? state.Notice.Message : "");
        // A closed InfoBar stays Visible and still takes stack spacing, which pushes the cards below other pages.
        StatusInfo.Visibility = StatusInfo.IsOpen ? Visibility.Visible : Visibility.Collapsed;

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
        // In use first, then installed, then not installed. OrderBy is stable, so catalog order holds within each group.
        _ordered = _cardList.OrderBy(card => card.ModelId == activeId ? 0 : snapshots[card].HasModelFile ? 1 : 2).ToArray();
        LayoutCards(_ordered, _columns);
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
                "Open Model folder, then Verify installed files to load this model.");
        }
        else
        {
            try { _session.SelectModel(index); }
            catch (Exception error) { _session.ReportUiError(error); }
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
            await MaintainAsync(token => _manager.InstallAsync(id, token));
            return;
        }
        try
        {
            _error = null;
            await _manager.DownloadAsync(id, _lifetime.Token);
            if (!_closed && _session.State.IsIdle)
                await MaintainAsync(token => _manager.InstallAsync(id, token));
        }
        catch (OperationCanceledException)
        {
            if (!_closed) { _session.Notify(NoticeKind.Information, "Download paused", "Partial weights remain available for Resume."); }
        }
        catch (Exception error) { ShowError(error); }
        finally { Render(); }
    }

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
                Text = partial ? "A complete model stays installed." : "You can download it again from this page.",
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
