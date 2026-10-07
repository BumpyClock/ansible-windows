using DictationPoc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.System;
using Windows.Storage.Pickers;

namespace DictationPoc;

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
        Render();
        await _initialization;
        Render();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var catalog = await NativeModelCatalog.LoadAsync(_paths.ModelCatalog, _lifetime.Token);
            var families = await NativeAudioEngine.GetSupportedFamiliesAsync(_paths.NativeLibrary, _lifetime.Token);
            var candidate = new ModelDownloadManager(catalog, families, _session.State.ModelsDirectory, _http);
            try
            {
                _themeSettings = ThemeSettings.CreateForWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle()));
                foreach (var entry in catalog)
                {
                    var card = new ModelCard();
                    _cardList.Add(card);
                    card.Initialize(entry, _themeSettings);
                    card.PrimaryRequested += OnPrimaryRequested;
                    card.PauseRequested += OnPauseRequested;
                    card.RemoveRequested += OnRemoveRequested;
                    card.DiscardRequested += OnDiscardRequested;
                }
                LayoutCards(ModelsGrid.ActualWidth >= TwoColumnThreshold ? 2 : 1);
                candidate.Changed += ManagerChanged;
                _manager = candidate;
            }
            finally
            {
                if (_manager is null)
                {
                    foreach (var card in _cardList) { card.DetachThemeEvents(); }
                    ModelsGrid.Children.Clear();
                    _cardList.Clear();
                    ModelsGrid.RowDefinitions.Clear();
                    ModelsGrid.ColumnDefinitions.Clear();
                    _columns = 0;
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
            System.Diagnostics.Debug.WriteLine("Local Voice: model view dispatcher is closed.");
    }

    private void OnModelsGridSizeChanged(object sender, SizeChangedEventArgs args) =>
        LayoutCards(args.NewSize.Width >= TwoColumnThreshold ? 2 : 1);

    // Owns intrinsic card sizing: a bounded Grid with Auto rows per pair so each card keeps its
    // natural height (no equal-height sizing from the shortest card) and the catalog never exceeds
    // two columns, collapsing to one when narrow.
    private void LayoutCards(int columns)
    {
        if (columns < 1) { columns = 1; }
        if (_columns == columns && ModelsGrid.Children.Count == _cardList.Count) { return; }
        _columns = columns;
        ModelsGrid.ColumnDefinitions.Clear();
        ModelsGrid.RowDefinitions.Clear();
        for (var column = 0; column < columns; column++)
            ModelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = (_cardList.Count + columns - 1) / columns;
        for (var row = 0; row < rows; row++)
            ModelsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < _cardList.Count; index++)
        {
            var card = _cardList[index];
            Grid.SetRow(card, index / columns);
            Grid.SetColumn(card, index % columns);
            if (index >= ModelsGrid.Children.Count) { ModelsGrid.Children.Add(card); }
        }
    }

    private void Render()
    {
        if (_closed) { return; }
        var state = _session.State;
        var managerIdle = _manager is { IsBusy: false } && !_dialogActive;
        var idle = state.IsIdle && managerIdle;
        var managerBusy = _manager?.IsBusy == true;
        FolderText.Text = _manager?.DirectoryPath ?? state.ModelsDirectory;
        FolderButton.IsEnabled = idle;
        RefreshButton.IsEnabled = _initialization is not { IsCompleted: false } && !managerBusy && !_dialogActive;
        FolderMenuButton.IsEnabled = !_dialogActive;

        var opening = _manager is null && _initialization is { IsCompleted: false } && _error is null;
        var attention = state.Notice.Kind is NoticeKind.Warning or NoticeKind.Error;
        StatusInfo.Severity = _error is not null || state.Notice.Kind == NoticeKind.Error ? InfoBarSeverity.Error :
            state.Notice.Kind == NoticeKind.Warning ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        StatusInfo.IsOpen = _error is not null || _manager is null || !state.IsIdle || attention;
        StatusInfo.Title = _error is not null ? "Model operation failed" :
            opening ? "Opening model catalog" :
            _manager is null ? "Catalog unavailable" :
            attention || !state.IsIdle ? state.Notice.Title : "";
        StatusInfo.Message = _error ?? (opening ? "Please wait while the model catalog opens." :
            _manager is null ? "Open Model folder, then Verify installed files to retry opening the catalog." :
            attention || !state.IsIdle ? state.Notice.Message : "");

        if (_manager is null) { return; }
        foreach (var card in _cardList)
        {
            card.Update(_manager.Get(card.ModelId), idle, managerIdle, managerBusy);
        }
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
            _error = null;
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
