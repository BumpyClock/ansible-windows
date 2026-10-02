using System.Runtime.InteropServices;
using DictationPoc.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace DictationPoc;

public sealed partial class FloatingDictationWindow : Window
{
    private readonly DictationSession _session;
    private SessionSnapshot _state;
    private readonly UiSessionObserver _observer;
    private readonly MainWindow _owner;
    private readonly List<Rectangle> _bars = [];
    private readonly double[] _levels = new double[44];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly UISettings _settings = new();
    private bool _closed;
    private bool _visible;
    private bool _reducedMotion;

    internal FloatingDictationWindow(DictationSession session, MainWindow owner)
    {
        InitializeComponent();
        _session = session;
        _state = session.State;
        _owner = owner;
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var style = GetWindowLongPtr(handle, -20);
        if (SetWindowLongPtr(handle, -20, style | 0x08000080) == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create a non-activating preview window.");
        }
        for (var index = 0; index < _levels.Length; index++)
        {
            var bar = new Rectangle
            {
                Width = 5, Height = 2, RadiusX = 2.5, RadiusY = 2.5,
                Style = (Style)Application.Current.Resources["PreviewWaveformBarStyle"],
                VerticalAlignment = VerticalAlignment.Center
            };
            _bars.Add(bar);
            WaveBars.Children.Add(bar);
        }
        _timer.Tick += (_, _) => DurationText.Text = _session.Elapsed.ToString(@"mm\:ss");
        _observer = new UiSessionObserver(session, DispatcherQueue, state => { _state = state; Render(); }, UpdateWaveform);
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _observer.Dispose();
        };
        Render();
    }

    internal void ShowPreview()
    {
        if (_closed || _visible) { return; }
        _reducedMotion = !_settings.AnimationsEnabled;
        Array.Clear(_levels);
        var ownerHandle = WinRT.Interop.WindowNative.GetWindowHandle(_owner);
        var scale = GetDpiForWindow(ownerHandle) / 96.0;
        var width = (int)Math.Round(560 * scale);
        var height = (int)Math.Round(286 * scale);
        var area = DisplayArea.GetFromWindowId(_owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - width) / 2, area.Y + area.Height - height - 28));
        AppWindow.Show(false);
        _visible = true;
        _timer.Start();
    }

    private void Render()
    {
        if (_closed) { return; }
        StateText.Text = _state.Phase switch
        {
            DictationPhase.Preparing => "Preparing model",
            DictationPhase.Recording => _state.IsReplay ? "Live verification" : "Listening",
            DictationPhase.Finishing => "Finishing transcript",
            DictationPhase.Cancelling => "Waiting for native cancellation",
            DictationPhase.Closing => "Releasing native resources",
            _ => _state.Notice.Kind == NoticeKind.Error ? "Recognition failed" : "Transcript ready"
        };
        ModelText.Text = _state.SelectedModel?.DisplayName ?? "Native audio.cpp";
        ModeText.Text = _state.IsReplay ? "WAV REPLAY / LOCAL" : "MICROPHONE / LOCAL";
        if (PreviewText.Text != _state.Transcript)
        {
            PreviewText.Text = _state.Transcript;
            PreviewText.Select(PreviewText.Text.Length, 0);
        }
        PreviewText.PlaceholderText = _state.Notice.Kind == NoticeKind.Error
            ? "Recognition failed. The app shows the error details."
            : _state.SelectedModel?.Preview == "final-only"
            ? "This model returns text after Finish."
            : _state.Phase == DictationPhase.Preparing ? "Loading the native model before capturing audio..." : "Waiting for actual model output...";
        FinishButton.IsEnabled = _state.CanFinish;
        FinishButton.Visibility = _state.IsLiveOperation ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = _state.IsLiveOperation ? "Cancel" : "Dismiss";
        CancelButton.IsEnabled = _state.CanCancel || _state.IsIdle || _state.Phase == DictationPhase.RecoveryRequired;
        if (!_state.IsLiveOperation) { _timer.Stop(); }
    }

    private void UpdateWaveform(double level)
    {
        if (_closed || !_visible) { return; }
        level = Math.Clamp(level * 8, 0, 1);
        if (_reducedMotion)
        {
            for (var index = 0; index < _levels.Length; index++) { _levels[index] = level; }
        }
        else
        {
            Array.Copy(_levels, 1, _levels, 0, _levels.Length - 1);
            _levels[^1] = level;
        }
        for (var index = 0; index < _bars.Count; index++) { _bars[index].Height = 2 + _levels[index] * 40; }
    }

    private async void FinishClicked(object sender, RoutedEventArgs args)
    {
        try { await _session.FinishAsync(); }
        catch (Exception error) { _session.ReportUiError(error); }
    }
    private async void CancelClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_state.IsLiveOperation) { await _session.CancelAsync(); }
            else { AppWindow.Hide(); _visible = false; }
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint handle, int index);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint handle, int index, nint value);
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint handle);
}
