using System.Diagnostics;
using DictationPoc.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DictationPoc;

public sealed partial class MainWindow : Window
{
    private readonly DictationSession _session;
    private readonly InsightsPage _insights;
    private readonly DictationPage _dictation;
    private readonly MainPage _settings;
    private readonly ModelManagementPage _models;
    private readonly UiSessionObserver _observer;
    private FloatingDictationWindow? _floating;
    private bool _initialized;
    private bool _closed;
    private bool _allowClose;
    private bool _closing;
    private bool _popupFailed;

    internal MainWindow(DictationSession session, AppPaths paths, HttpClient modelDownloads)
    {
        InitializeComponent();
        _session = session;
        _insights = new InsightsPage(session, Navigate);
        _dictation = new DictationPage(session);
        _settings = new MainPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this));
        _models = new ModelManagementPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), modelDownloads);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1700, area.Width - 60), Math.Min(1100, area.Height - 60)));
        MainContent.Loaded += Initialize;
        AppWindow.Closing += CloseRequested;
        Closed += WindowClosed;
        Navigate("insights");
        _observer = new UiSessionObserver(session, DispatcherQueue, SessionChanged);
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        if (_initialized) { return; }
        _initialized = true;
        try
        {
            await _session.InitializeAsync();
            if (_session.State.Phase == DictationPhase.Disconnected && _session.State.Models.Count == 0)
                Navigate("models");
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    private void NavigateClicked(object sender, RoutedEventArgs args) => Navigate((string)((Button)sender).Tag);

    private void Navigate(string page)
    {
        MainContent.Content = page switch { "settings" => _settings, "dictation" => _dictation, "models" => _models, _ => _insights };
        foreach (var button in new[] { InsightsNav, DictationNav, SettingsNav, ModelsNav })
        {
            button.Style = (Style)Application.Current.Resources[
                (string)button.Tag == page ? "SelectedNavigationButtonStyle" : "NavigationButtonStyle"];
            AutomationProperties.SetHelpText(button, (string)button.Tag == page ? "Current page" : "");
        }
    }

    private void SessionChanged(SessionSnapshot state)
    {
        if (_closed) { return; }
        SidebarStatus.Text = state.Phase == DictationPhase.Disconnected
            ? "Open Speech models to download or verify local weights."
            : state.Phase is DictationPhase.Closing or DictationPhase.Closed
                ? "Releasing owned native resources."
                : $"{state.Models.Count} models available / native CPU";
        if (!state.IsLiveOperation) { _popupFailed = false; }
        if (state.IsLiveOperation && _floating is null && !_popupFailed)
        {
            try
            {
                _floating = new FloatingDictationWindow(_session, this);
                _floating.Closed += (_, _) => _floating = null;
            }
            catch (Exception error)
            {
                _popupFailed = true;
                _session.ReportUiError(new InvalidOperationException("The floating preview could not be opened.", error));
            }
        }
        if (state.IsLiveOperation) { _floating?.ShowPreview(); }
    }

    private async void CloseRequested(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) { return; }
        args.Cancel = true;
        if (_closing) { return; }
        _closing = true;
        try
        {
            await Task.WhenAll(_session.CloseAsync(), _models.DisposeAsync().AsTask());
            if (_floating is not null) { await _floating.ClosePreviewAsync(); }
            _allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Local Voice: native shutdown needs recovery: {error.Message}");
            SidebarStatus.Text = $"Close failed. {error.Message} Try closing the window again.";
            _closing = false;
        }
    }

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _observer.Dispose();
        AppWindow.Closing -= CloseRequested;
    }
}
