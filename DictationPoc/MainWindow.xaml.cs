using DictationPoc.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace DictationPoc;

public sealed partial class MainWindow : Window
{
    private readonly DictationController _controller;
    private readonly InsightsPage _insights;
    private readonly DictationPage _dictation;
    private readonly MainPage _settings;
    private FloatingDictationWindow? _floating;
    private bool _initialized;
    private bool _closed;
    private bool _popupFailed;

    public MainWindow()
    {
        InitializeComponent();
        _controller = ((App)Application.Current).Controller;
        _insights = new InsightsPage();
        _dictation = new DictationPage();
        _settings = new MainPage();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1700, area.Width - 60), Math.Min(1100, area.Height - 60)));
        _controller.Changed += ControllerChanged;
        MainContent.Loaded += Initialize;
        Closed += WindowClosed;
        Navigate("insights");
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        if (_initialized) { return; }
        _initialized = true;
        await _controller.InitializeAsync();
    }

    private void NavigateClicked(object sender, RoutedEventArgs args) => Navigate((string)((Button)sender).Tag);

    internal void Navigate(string page)
    {
        MainContent.Content = page switch { "settings" => _settings, "dictation" => _dictation, _ => _insights };
        foreach (var button in new[] { InsightsNav, DictationNav, SettingsNav })
        {
            button.Background = (string)button.Tag == page
                ? (Brush)Application.Current.Resources["SelectionBrush"]
                : new SolidColorBrush(Colors.Transparent);
            AutomationProperties.SetHelpText(button, (string)button.Tag == page ? "Current page" : "");
        }
    }

    private void ControllerChanged()
    {
        if (_closed) { return; }
        SidebarStatus.Text = _controller.Phase == DictationPhase.Disconnected
            ? "Open Settings to load the native backend."
            : $"{_controller.Models.Count} models available / native CPU";
        if (!_controller.IsLiveOperation) { _popupFailed = false; }
        if (_controller.IsLiveOperation && _floating is null && !_popupFailed)
        {
            try
            {
                _floating = new FloatingDictationWindow(_controller, this);
                _floating.Closed += (_, _) => _floating = null;
            }
            catch (Exception error)
            {
                _popupFailed = true;
                _controller.Fail(new InvalidOperationException("The floating preview could not be opened.", error));
            }
        }
        if (_controller.IsLiveOperation)
        {
            _floating?.ShowPreview();
        }
    }

    private async void WindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _controller.Changed -= ControllerChanged;
        await _controller.ShutdownAsync();
        _floating?.Close();
    }
}
