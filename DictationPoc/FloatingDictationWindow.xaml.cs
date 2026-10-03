using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DictationPoc.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace DictationPoc;

public sealed partial class FloatingDictationWindow : Window
{
    private readonly DictationSession _session;
    private SessionSnapshot _state;
    private readonly UiSessionObserver _observer;
    private readonly MainWindow _owner;
    private readonly List<Rectangle> _bars = [];
    private readonly double[] _levels = new double[20];
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private XamlRoot? _xamlRoot;
    private NativeTextRibbon? _textRibbon;
    private NativeCompositionPill? _pill;
    private readonly List<Task> _cleanupTasks = [];
    private Task? _closeTask;
    private bool _closed;
    private bool _visible;
    private bool _layoutQueued;
    private bool _sizing;
    private bool _reducedMotion;
    private bool _replay;
    private bool _ribbonDirty;
    private bool _ribbonRendering;
    private bool _ribbonInstant;
    private bool _ribbonUnavailable;
    private long _ribbonGeneration;
    private double _ribbonScale;
    private long _dismissStarted;
    private long _dismissVersion = -1;
    private (int Left, int Top, int Width, int Height) _windowShape;

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
        var extendedStyle = GetWindowLongPtr(handle, -20);
        SetStyle(handle, -20, extendedStyle | 0x08000080);
        var nonClientPolicy = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, 2, ref nonClientPolicy, 4));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var noBorder = unchecked((int)0xFFFFFFFE);
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, 34, ref noBorder, 4));
        }
        for (var index = 0; index < _levels.Length; index++)
        {
            var bar = new Rectangle
            {
                Width = 3, Height = 3, RadiusX = 1.5, RadiusY = 1.5,
                Style = (Style)PreviewRoot.Resources["CompactWaveformBarStyle"],
                VerticalAlignment = VerticalAlignment.Center
            };
            _bars.Add(bar);
            WaveBars.Children.Add(bar);
        }
        _dismissTimer.Tick += (_, _) =>
        {
            var current = _session.State;
            if (_dismissVersion == current.Version && _dismissStarted != 0 &&
                Stopwatch.GetElapsedTime(_dismissStarted) >= _dismissTimer.Interval &&
                FloatingPreviewPresentation.From(current).AutoDismiss) { HidePreview(); }
        };
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _settings.AnimationsEnabledChanged += AnimationsChanged;
        }
        _settings.TextScaleFactorChanged += PresentationSettingsChanged;
        _settings.ColorValuesChanged += PresentationSettingsChanged;
        _observer = new UiSessionObserver(session, DispatcherQueue, state => { _state = state; Render(); }, UpdateWaveform);
        Closed += async (_, _) =>
        {
            if (_closed) { return; }
            StopPreviewResources();
            try { await Task.WhenAll(_cleanupTasks); }
            catch (Exception error)
            {
                _session.ReportUiError(new InvalidOperationException("The floating preview could not shut down.", error));
            }
        };
        Render();
    }

    internal Task ClosePreviewAsync() => _closeTask ??= ClosePreviewCoreAsync();

    private async Task ClosePreviewCoreAsync()
    {
        StopPreviewResources();
        try { await Task.WhenAll(_cleanupTasks); }
        finally { Close(); }
    }

    private void StopPreviewResources()
    {
        if (_closed) { return; }
        _closed = true;
        _ribbonGeneration++;
        _dismissTimer.Stop();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _settings.AnimationsEnabledChanged -= AnimationsChanged;
        }
        _settings.TextScaleFactorChanged -= PresentationSettingsChanged;
        _settings.ColorValuesChanged -= PresentationSettingsChanged;
        if (_xamlRoot is not null) { _xamlRoot.Changed -= RootChanged; }
        _observer.Dispose();
        try { _textRibbon?.Dispose(); }
        catch (Exception error) { _cleanupTasks.Add(Task.FromException(error)); }
        try { _pill?.Dispose(); }
        catch (Exception error) { _cleanupTasks.Add(Task.FromException(error)); }
    }

    internal void ShowPreview()
    {
        if (_closed || _visible || !_session.State.IsLiveOperation) { return; }
        _state = _session.State;
        Render();
        _reducedMotion = !_settings.AnimationsEnabled;
        Array.Clear(_levels);
        PaintWaveform();
        var area = DisplayArea.GetFromWindowId(_owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + area.Width / 2, area.Y + area.Height / 2));
        SizeToContent();
        AppWindow.Show(false);
        _visible = true;
        _pill?.Show();
        QueueLayout();
    }

    private void Render()
    {
        if (_closed) { return; }
        if (_state.Phase == DictationPhase.Preparing) { _ribbonUnavailable = false; }
        var presentation = FloatingPreviewPresentation.From(_state);
        if (_state.IsLiveOperation) { _replay = _state.IsReplay; }
        var source = _replay ? "Public WAV replay. No microphone is open." : "Microphone audio. On-device recognition.";
        var dismissHelp = presentation.IsTerminal ? " Tap or right-click to dismiss the preview." : "";
        AutomationProperties.SetName(PreviewRoot, presentation.Transcript.Length > 0
            ? $"{presentation.Status}. {presentation.Transcript}" : presentation.Status);
        AutomationProperties.SetHelpText(PreviewRoot, $"{source} {presentation.Hint}{dismissHelp}");
        ToolTipService.SetToolTip(PreviewRoot, $"{presentation.Status}. {presentation.Hint}{dismissHelp}");
        _pill?.SetAccessibleText($"{presentation.Status}. {presentation.Transcript}. {presentation.Hint}{dismissHelp}");
        DismissPreviewItem.IsEnabled = presentation.IsTerminal;
        if (RibbonText.Text != presentation.Ribbon)
        {
            RibbonText.Text = presentation.Ribbon;
            RequestRibbon(false);
        }
        RibbonViewport.Visibility = presentation.ShowRibbon ? Visibility.Visible : Visibility.Collapsed;
        if (!presentation.ShowRibbon) { _textRibbon?.Hide(); }
        if (!_state.IsLiveOperation)
        {
            Array.Clear(_levels);
            PaintWaveform();
        }
        if (presentation.AutoDismiss && _dismissVersion != _state.Version && _visible)
        {
            _dismissVersion = _state.Version;
            _dismissStarted = Stopwatch.GetTimestamp();
            _dismissTimer.Stop();
            _dismissTimer.Start();
        }
        if (!presentation.AutoDismiss)
        {
            _dismissStarted = 0;
            _dismissVersion = -1;
            _dismissTimer.Stop();
        }
        if (_state.Phase is DictationPhase.Connecting or DictationPhase.UpdatingPreferences or DictationPhase.Closed ||
            _state.Activity is SessionActivity.File or SessionActivity.ModelMaintenance)
        {
            HidePreview();
        }
        QueueLayout();
    }

    private void PreviewLoaded(object sender, RoutedEventArgs args)
    {
        if (_xamlRoot is not null) { _xamlRoot.Changed -= RootChanged; }
        _xamlRoot = PreviewRoot.XamlRoot;
        _xamlRoot.Changed += RootChanged;
        RequestRibbon(true);
        QueueLayout();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        QueueLayout();
    }

    private void ThemeChanged(FrameworkElement sender, object args)
    {
        RequestRibbon(true);
        QueueLayout();
    }

    private void PreviewSizeChanged(object sender, SizeChangedEventArgs args) => QueueLayout();
    private void PresentationSettingsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) { return; }
        RequestRibbon(true);
        QueueLayout();
    });

    private void RequestRibbon(bool instant)
    {
        _ribbonGeneration++;
        _ribbonDirty = true;
        _ribbonInstant = instant;
    }

    private void QueueLayout()
    {
        if (_closed || _layoutQueued || _sizing) { return; }
        _layoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _layoutQueued = false;
            if (_closed) { return; }
            SizeToContent();
            if (_ribbonDirty) { RenderRibbon(); }
        }))
        {
            _layoutQueued = false;
            Debug.WriteLine("Local Voice: the preview dispatcher is closed; a late layout update was discarded.");
        }
    }

    private void SizeToContent()
    {
        if (_closed || _sizing) { return; }
        _sizing = true;
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            var hasRibbon = RibbonViewport.Visibility == Visibility.Visible;
            var width = Math.Min(hasRibbon ? FloatingPreviewPresentation.Width : FloatingPreviewPresentation.WaveformWidth,
                area.Width / scale);
            PillBorder.Width = Math.Min(FloatingPreviewPresentation.WaveformWidth, width);
            RibbonViewport.Height = 24 * _settings.TextScaleFactor;
            PreviewRoot.Measure(new Size(width, double.PositiveInfinity));
            var bounds = FloatingPreviewPresentation.Place(area.X, area.Y, area.Width, area.Height,
                scale, width, Math.Max(1, PreviewRoot.DesiredSize.Height));
            if (AppWindow.Size.Width != bounds.Width || AppWindow.Size.Height != bounds.Height)
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(bounds.Width, bounds.Height));
            }
            var pillWidth = (int)Math.Ceiling(PillBorder.Width * scale);
            var pillHeight = (int)Math.Min(bounds.Height, Math.Ceiling(56 * scale));
            var shape = ((bounds.Width - pillWidth) / 2, bounds.Height - pillHeight, pillWidth, pillHeight);
            if (_windowShape != shape)
            {
                var region = CreateRectRgn(0, 0, 0, 0);
                if (region == 0) { throw new InvalidOperationException("Cannot hide the text rasterization host."); }
                if (SetWindowRgn(WinRT.Interop.WindowNative.GetWindowHandle(this), region, 0) == 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    DeleteObject(region);
                    throw new Win32Exception(error, "Cannot hide the text rasterization host.");
                }
                // The WinUI host only shapes glyphs. Its entire window region remains empty.
                _windowShape = shape;
            }
            if (AppWindow.Position.X != bounds.X || AppWindow.Position.Y != bounds.Y)
            {
                AppWindow.Move(new Windows.Graphics.PointInt32(bounds.X, bounds.Y));
            }
            _pill ??= new NativeCompositionPill(WinRT.Interop.WindowNative.GetWindowHandle(_owner),
                QueueTerminalDismiss, _cleanupTasks.Add);
            var foreground = ((SolidColorBrush)RibbonText.Foreground).Color;
            _pill.Configure(bounds.X + shape.Item1, bounds.Y + shape.Item2, pillWidth, pillHeight,
                scale, foreground, PreviewRoot.ActualTheme == ElementTheme.Dark, _accessibility.HighContrast,
                _settings.AdvancedEffectsEnabled);
            _pill.UpdateLevels(_levels);
            if (_ribbonScale != scale)
            {
                _ribbonScale = scale;
                RequestRibbon(true);
            }
            _textRibbon?.Move(bounds.X, bounds.Y);
        }
        finally { _sizing = false; }
    }

    private async void RenderRibbon()
    {
        if (_closed || !_visible || _ribbonRendering || _ribbonUnavailable ||
            RibbonViewport.Visibility != Visibility.Visible || RibbonText.Text.Length == 0) { return; }
        _ribbonRendering = true;
        _ribbonDirty = false;
        var generation = _ribbonGeneration;
        var instant = _ribbonInstant;
        try
        {
            PreviewRoot.UpdateLayout();
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(RibbonText);
            if (_closed || !_visible || generation != _ribbonGeneration) { return; }
            var buffer = await bitmap.GetPixelsAsync();
            if (_closed || !_visible || generation != _ribbonGeneration) { return; }
            var pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) { reader.ReadBytes(pixels); }
            var words = RibbonText.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var rasters = WordRasters(words, pixels, bitmap.PixelWidth, bitmap.PixelHeight);
            _textRibbon ??= new NativeTextRibbon(_pill!.Handle, RibbonFailed);
            _textRibbon.Update(rasters, AppWindow.Size.Width, bitmap.PixelHeight,
                AppWindow.Position.X, AppWindow.Position.Y, _ribbonScale,
                !instant && !_reducedMotion && !_accessibility.HighContrast, !_accessibility.HighContrast);
        }
        catch (Exception error) when (error is COMException or Win32Exception or InvalidOperationException)
        {
            if (_closed) { Debug.WriteLine($"Local Voice: text rendering ended during preview teardown: {error.Message}"); }
            else { RibbonFailed(error); }
        }
        finally
        {
            _ribbonRendering = false;
            if (!_closed && _ribbonDirty && !_ribbonUnavailable) { QueueLayout(); }
        }
    }

    private WordRaster[] WordRasters(string[] words, byte[] pixels, int width, int height)
    {
        var result = new WordRaster[words.Length];
        var position = 0;
        var structural = Math.Max(0, (RibbonText.ContentEnd.Offset - RibbonText.ContentStart.Offset - RibbonText.Text.Length) / 2);
        var centered = (AppWindow.Size.Width - width) / 2;
        for (var index = 0; index < words.Length; index++)
        {
            var left = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            for (var character = position; character < position + words[index].Length; character++)
            {
                var pointer = RibbonText.ContentStart.GetPositionAtOffset(character + structural, LogicalDirection.Forward);
                if (pointer is null) { throw new InvalidOperationException("Native word layout has no character position."); }
                var box = pointer.GetCharacterRect(LogicalDirection.Forward);
                left = Math.Min(left, box.Left);
                right = Math.Max(right, box.Right);
            }
            var after = RibbonText.ContentStart.GetPositionAtOffset(position + words[index].Length + structural,
                LogicalDirection.Forward);
            if (after is not null)
            {
                var endBox = after.GetCharacterRect(LogicalDirection.Backward);
                left = Math.Min(left, endBox.Left);
                right = Math.Max(right, endBox.Right);
            }
            var x = Math.Clamp((int)Math.Floor(left * _ribbonScale) - 2, 0, width);
            var end = Math.Clamp((int)Math.Ceiling(right * _ribbonScale) + 2, x, width);
            var wordPixels = new byte[(end - x) * height * 4];
            for (var row = 0; row < height; row++)
            {
                pixels.AsSpan((row * width + x) * 4, (end - x) * 4)
                    .CopyTo(wordPixels.AsSpan(row * (end - x) * 4));
            }
            result[index] = new(words[index], centered + x, end - x, wordPixels);
            position += words[index].Length + 1;
        }
        return result;
    }

    private void RibbonFailed(Exception error)
    {
        _ribbonUnavailable = true;
        _textRibbon?.Dispose();
        _textRibbon = null;
        _session.ReportUiError(new InvalidOperationException("The floating text could not be rendered.", error));
    }

    private void HidePreview()
    {
        _ribbonGeneration++;
        _dismissStarted = 0;
        _dismissVersion = -1;
        _dismissTimer.Stop();
        _textRibbon?.Hide();
        _pill?.Hide();
        AppWindow.Hide();
        _visible = false;
    }

    private void DismissPreviewClicked(object sender, RoutedEventArgs args) => DismissTerminalPreview();

    private void DismissTerminalPreview()
    {
        if (FloatingPreviewPresentation.From(_session.State).IsTerminal) { HidePreview(); }
    }

    private void QueueTerminalDismiss()
    {
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) { return; }
            try { DismissTerminalPreview(); }
            catch (Exception error)
            {
                _session.ReportUiError(new InvalidOperationException("The floating preview could not be dismissed.", error));
            }
        }))
        {
            Debug.WriteLine("Local Voice: the preview dispatcher is closed; a late dismiss was discarded.");
        }
    }

    private void AnimationsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) { return; }
        _reducedMotion = !_settings.AnimationsEnabled;
        if (_reducedMotion) { _textRibbon?.FinishAnimations(); }
        Array.Fill(_levels, _levels[^1]);
        PaintWaveform();
    });

    private void UpdateWaveform(double level)
    {
        if (_closed || !_visible || _state.Phase != DictationPhase.Recording) { return; }
        level = FloatingPreviewPresentation.MeterLevel(level);
        if (_reducedMotion) { Array.Fill(_levels, level); }
        else
        {
            Array.Copy(_levels, 1, _levels, 0, _levels.Length - 1);
            _levels[^1] = level;
        }
        PaintWaveform();
    }

    private void PaintWaveform()
    {
        for (var index = 0; index < _bars.Count; index++) { _bars[index].Height = 3 + _levels[index] * 21; }
        AutomationProperties.SetName(WaveBars, _reducedMotion ? "Current real audio energy" : "Real audio energy history");
        _pill?.UpdateLevels(_levels);
    }

    private static void SetStyle(nint handle, int index, nint style)
    {
        if (SetWindowLongPtr(handle, index, style) == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot set the preview window style.");
        }
    }

    private sealed record WordRaster(string Text, int X, int Width, byte[] Pixels);

    private sealed unsafe partial class NativeCompositionPill : IDisposable
    {
        private readonly Windows.UI.Composition.Compositor _compositor;
        private readonly Windows.UI.Composition.Desktop.DesktopWindowTarget _target;
        private readonly Windows.UI.Composition.ShapeVisual _visual;
        private readonly Windows.UI.Composition.CompositionSpriteShape _outline;
        private readonly Windows.UI.Composition.CompositionRoundedRectangleGeometry _geometry;
        private readonly Windows.UI.Composition.SpriteVisual _backdrop;
        private readonly Windows.UI.Composition.CompositionRoundedRectangleGeometry _backdropGeometry;
        private readonly List<Windows.UI.Composition.CompositionSpriteShape> _bars = [];
        private readonly Windows.System.DispatcherQueueController? _queue;
        private readonly nint _handle;
        private readonly Action<Task> _retainCleanup;
        private bool _disposed;
        private double _scale;
        private (int Width, int Height, double Scale, Windows.UI.Color Foreground, bool Dark, bool Contrast, bool Effects) _style;

        public nint Handle => _handle;

        public NativeCompositionPill(nint owner, Action dismiss, Action<Task> retainCleanup)
        {
            _retainCleanup = retainCleanup;
            try
            {
                if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
                {
                    var options = new QueueOptions { Size = 12, ThreadType = 2, Apartment = 2 };
                    Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var queue));
                    try { _queue = WinRT.MarshalInterface<Windows.System.DispatcherQueueController>.FromAbi(queue); }
                    finally { if (queue != 0) { Marshal.Release(queue); } }
                }
                _compositor = new Windows.UI.Composition.Compositor();
                _handle = NativeTextRibbon.CreateCompositionWindow(owner, dismiss);
                var abi = WinRT.MarshalInspectable<Windows.UI.Composition.Compositor>.FromManaged(_compositor);
                var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");
                nint interop = 0;
                try
                {
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in iid, out interop));
                    var table = *(nint**)interop;
                    var create = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)table[3];
                    nint target = 0;
                    Marshal.ThrowExceptionForHR(create(interop, _handle, 1, &target));
                    try { _target = WinRT.MarshalInterface<Windows.UI.Composition.Desktop.DesktopWindowTarget>.FromAbi(target); }
                    finally { if (target != 0) { Marshal.Release(target); } }
                }
                finally
                {
                    if (interop != 0) { Marshal.Release(interop); }
                    WinRT.MarshalInspectable<Windows.UI.Composition.Compositor>.DisposeAbi(abi);
                }
                var root = _compositor.CreateContainerVisual();
                _target.Root = root;
                _backdrop = _compositor.CreateSpriteVisual();
                _backdropGeometry = _compositor.CreateRoundedRectangleGeometry();
                _backdrop.Clip = _compositor.CreateGeometricClip(_backdropGeometry);
                root.Children.InsertAtBottom(_backdrop);
                _visual = _compositor.CreateShapeVisual();
                root.Children.InsertAtTop(_visual);
                _geometry = _compositor.CreateRoundedRectangleGeometry();
                _outline = _compositor.CreateSpriteShape(_geometry);
                _visual.Shapes.Add(_outline);
                for (var index = 0; index < 20; index++)
                {
                    var geometry = _compositor.CreateRoundedRectangleGeometry();
                    var shape = _compositor.CreateSpriteShape(geometry);
                    _bars.Add(shape);
                    _visual.Shapes.Add(shape);
                }
            }
            catch
            {
                try { Dispose(); }
                catch (Exception error) { _retainCleanup(Task.FromException(error)); }
                throw;
            }
        }

        public void Configure(int x, int y, int width, int height, double scale,
            Windows.UI.Color foreground, bool dark, bool contrast, bool effects)
        {
            if (_disposed) { return; }
            _scale = scale;
            var style = (width, height, scale, foreground, dark, contrast, effects);
            if (_style != style)
            {
                _style = style;
                _visual.Size = new Vector2(width, height);
                _geometry.Offset = new Vector2((float)scale);
                _geometry.Size = new Vector2(width - (float)(2 * scale), height - (float)(2 * scale));
                _geometry.CornerRadius = new Vector2((float)(height / 2.0 - scale));
                var backdrop = !contrast && effects && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
                _backdrop.Size = new Vector2(width, height);
                _backdropGeometry.Size = _backdrop.Size;
                _backdropGeometry.CornerRadius = new Vector2(height / 2.0f);
                if (backdrop)
                {
                    var enabled = 1;
                    Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(_handle, 17, ref enabled, 4));
                    _backdrop.Brush = _compositor.CreateHostBackdropBrush();
                }
                else { _backdrop.Brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)); }
                _outline.FillBrush = _compositor.CreateColorBrush(contrast
                    ? new UISettings().GetColorValue(UIColorType.Background)
                    : dark ? Windows.UI.Color.FromArgb(backdrop ? (byte)230 : (byte)255, 35, 35, 35)
                    : Windows.UI.Color.FromArgb(backdrop ? (byte)235 : (byte)255, 247, 247, 247));
                _outline.StrokeBrush = _compositor.CreateColorBrush(foreground);
                _outline.StrokeThickness = (float)(2 * scale);
                foreach (var bar in _bars) { bar.FillBrush = _compositor.CreateColorBrush(foreground); }
            }
            if (SetWindowPos(_handle, -1, x, y, width, height, 0x10) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot position the native waveform overlay.");
            }
        }

        public void UpdateLevels(double[] levels)
        {
            if (_disposed || _scale == 0) { return; }
            var left = (_visual.Size.X - (20 * 3 + 19 * 3) * _scale) / 2;
            for (var index = 0; index < _bars.Count; index++)
            {
                var height = (3 + levels[index] * 21) * _scale;
                var geometry = (Windows.UI.Composition.CompositionRoundedRectangleGeometry)_bars[index].Geometry;
                geometry.Size = new Vector2((float)(3 * _scale), (float)height);
                geometry.CornerRadius = new Vector2((float)(1.5 * _scale));
                _bars[index].Offset = new Vector2((float)(left + index * 6 * _scale), (float)((_visual.Size.Y - height) / 2));
            }
        }

        public void SetAccessibleText(string text) => NativeTextRibbon.SetAccessibleText(_handle, text);
        public void Show() => NativeTextRibbon.ShowCompositionWindow(_handle);
        public void Hide() => NativeTextRibbon.HideCompositionWindow(_handle);

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            try { _target?.Dispose(); }
            finally
            {
                try { _compositor?.Dispose(); }
                finally
                {
                    try
                    {
                        if (_handle != 0) { NativeTextRibbon.DestroyCompositionWindow(_handle); }
                    }
                    finally
                    {
                        if (_queue is not null)
                        {
                            try { _retainCleanup(_queue.ShutdownQueueAsync().AsTask()); }
                            catch (Exception error) { _retainCleanup(Task.FromException(error)); }
                        }
                    }
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QueueOptions { public uint Size, ThreadType, Apartment; }
        [LibraryImport("CoreMessaging.dll")]
        private static partial int CreateDispatcherQueueController(QueueOptions options, out nint controller);
    }

    private sealed unsafe partial class NativeTextRibbon : IDisposable
    {
        private const string ClassName = "LocalVoiceTransparentText";
        private static readonly Dictionary<nint, NativeTextRibbon> Windows = [];
        private static readonly Dictionary<nint, Action> PillClicks = [];
        private static bool _registered;
        private readonly Action<Exception> _failed;
        private readonly DispatcherTimer _frames = new() { Interval = TimeSpan.FromMilliseconds(16) };
        private nint _handle;
        private nint _dc;
        private nint _bitmap;
        private nint _previousBitmap;
        private nint _bits;
        private byte[] _canvas = [];
        private Sprite[] _sprites = [];
        private int _width;
        private int _height;
        private int _x;
        private int _y;
        private double _scale;
        private bool _fadeEdges;
        private bool _disposed;

        public NativeTextRibbon(nint owner, Action<Exception> failed)
        {
            _failed = failed;
            EnsureWindowClass();
            _handle = CreateWindowEx(0x080800A8, ClassName, "Local Voice text", 0x80000000,
                0, 0, 1, 1, owner, 0, GetModuleHandle(null), 0);
            if (_handle == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create transparent text."); }
            Windows.Add(_handle, this);
            _frames.Tick += (_, _) =>
            {
                try { Paint(); }
                catch (Win32Exception error)
                {
                    _frames.Stop();
                    _failed(error);
                }
            };
        }

        private static void EnsureWindowClass()
        {
            var instance = GetModuleHandle(null);
            if (!_registered)
            {
                fixed (char* name = ClassName)
                {
                    var type = new WindowClass
                    {
                        Size = (uint)sizeof(WindowClass), Instance = instance, Name = (nint)name,
                        Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure
                    };
                    if (RegisterClassEx(ref type) == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                }
                _registered = true;
            }
        }

        public static nint CreateCompositionWindow(nint owner, Action dismiss)
        {
            EnsureWindowClass();
            var handle = CreateWindowEx(0x08200088, ClassName, "Local Voice overlay", 0x80000000,
                0, 0, 1, 1, owner, 0, GetModuleHandle(null), 0);
            if (handle == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            PillClicks.Add(handle, dismiss);
            return handle;
        }

        public static void ShowCompositionWindow(nint handle) => ShowWindow(handle, 4);
        public static void HideCompositionWindow(nint handle) => ShowWindow(handle, 0);
        public static void DestroyCompositionWindow(nint handle) => DestroyWindow(handle);
        public static void SetAccessibleText(nint handle, string text) => SetWindowText(handle, text);

        public void Update(WordRaster[] words, int width, int height, int x, int y,
            double scale, bool animate, bool fadeEdges)
        {
            if (_disposed) { return; }
            var now = Stopwatch.GetTimestamp();
            var reused = FloatingPreviewPresentation.ReuseWords(
                _sprites.Select(sprite => sprite.Raster.Text).ToArray(), words.Select(word => word.Text).ToArray());
            var next = new Sprite[words.Length];
            var arriving = 0;
            foreach (var index in Enumerable.Range(0, words.Length).OrderBy(index => words[index].X))
            {
                var old = reused[index] >= 0 ? _sprites[reused[index]] : null;
                var same = old?.Raster.Text == words[index].Text;
                var from = old is null ? words[index].X - 6 * scale : old.X(now) + _x - x;
                var reveal = old is null ? 0 : same ? words[index].Width : Math.Min(old.Reveal(now), old.Raster.Width);
                var opacity = old?.Opacity(now) ?? 0;
                var delay = old is null ? arriving++ * 24 : 0;
                next[index] = new(words[index], animate ? from : words[index].X,
                    animate ? reveal : words[index].Width, animate ? opacity : 1,
                    animate ? now + (long)(delay * Stopwatch.Frequency / 1000.0) : 0);
            }
            _sprites = next;
            _scale = scale;
            _fadeEdges = fadeEdges;
            _x = x;
            _y = y;
            EnsureSurface(width, height);
            Paint();
            if (animate) { _frames.Start(); }
            else { _frames.Stop(); }
        }

        public void Move(int x, int y)
        {
            if (_disposed || _bitmap == 0 || (_x == x && _y == y)) { return; }
            _x = x;
            _y = y;
            Paint();
        }

        public void FinishAnimations()
        {
            foreach (var sprite in _sprites) { sprite.Started = 0; }
            _frames.Stop();
            if (!_disposed && _bitmap != 0) { Paint(); }
        }

        public void Hide()
        {
            _frames.Stop();
            if (_handle != 0) { ShowWindow(_handle, 0); }
            ReleaseSurface();
            _sprites = [];
        }

        private void EnsureSurface(int width, int height)
        {
            if (_bitmap != 0 && width == _width && height == _height) { return; }
            ReleaseSurface();
            _width = width;
            _height = height;
            _canvas = new byte[checked(width * height * 4)];
            var screen = GetDC(0);
            try
            {
                _dc = CreateCompatibleDC(screen);
                if (_dc == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                var header = new BitmapHeader
                {
                    Size = (uint)sizeof(BitmapHeader), Width = width, Height = -height,
                    Planes = 1, Bits = 32
                };
                _bitmap = CreateDIBSection(screen, ref header, 0, out _bits, 0, 0);
                if (_bitmap == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                _previousBitmap = SelectObject(_dc, _bitmap);
                if (_previousBitmap == 0 || _previousBitmap == -1) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            }
            finally { if (screen != 0) { ReleaseDC(0, screen); } }
        }

        private void Paint()
        {
            if (_disposed || _bitmap == 0 || _handle == 0) { return; }
            Array.Clear(_canvas);
            var now = Stopwatch.GetTimestamp();
            var left = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var sprite in _sprites)
                {
                    var raster = sprite.Raster;
                    var x = (int)Math.Round(sprite.X(now));
                    var reveal = sprite.Reveal(now);
                    var opacity = sprite.Opacity(now);
                    left = Math.Min(left, x);
                    right = Math.Max(right, x + raster.Width);
                    for (var row = 0; row < _height; row++)
                    {
                        for (var column = 0; column < raster.Width; column++)
                        {
                            var targetX = x + column;
                            if (targetX < 0 || targetX >= _width) { continue; }
                            var amount = opacity * Math.Clamp((reveal - column) / Math.Max(1, 2 * _scale), 0, 1);
                            var source = (row * raster.Width + column) * 4;
                            var alpha = (int)(raster.Pixels[source + 3] * amount);
                            if (alpha == 0) { continue; }
                            if (pass == 0)
                            {
                                var shadowAlpha = alpha / 3;
                                var white = raster.Pixels[source] + raster.Pixels[source + 1] + raster.Pixels[source + 2]
                                    < raster.Pixels[source + 3] * 1.5;
                                for (var dy = -1; dy <= 1; dy++)
                                {
                                    for (var dx = -1; dx <= 1; dx++)
                                    {
                                        if (row + dy < 0 || row + dy >= _height || targetX + dx < 0 || targetX + dx >= _width) { continue; }
                                        var shadow = ((row + dy) * _width + targetX + dx) * 4;
                                        for (var channel = 0; channel < 4; channel++)
                                        {
                                            var color = channel == 3 || white ? shadowAlpha : 0;
                                            _canvas[shadow + channel] = (byte)Math.Min(255,
                                                color + _canvas[shadow + channel] * (255 - shadowAlpha) / 255);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var target = (row * _width + targetX) * 4;
                                for (var channel = 0; channel < 4; channel++)
                                {
                                    _canvas[target + channel] = (byte)Math.Min(255,
                                        raster.Pixels[source + channel] * amount + _canvas[target + channel] * (255 - alpha) / 255);
                                }
                            }
                        }
                    }
                }
            }
            if (_fadeEdges && right > left)
            {
                var edge = Math.Min(16 * _scale, (right - left) / 8);
                for (var column = 0; column < _width; column++)
                {
                    var opacity = Math.Clamp(Math.Min(column - left, right - column) / edge, 0, 1);
                    for (var row = 0; row < _height; row++)
                    {
                        var pixel = (row * _width + column) * 4;
                        for (var channel = 0; channel < 4; channel++) { _canvas[pixel + channel] = (byte)(_canvas[pixel + channel] * opacity); }
                    }
                }
            }
            Marshal.Copy(_canvas, 0, _bits, _canvas.Length);
            var position = new NativePoint { X = _x, Y = _y };
            var size = new NativeSize { Width = _width, Height = _height };
            var sourcePosition = new NativePoint();
            var blend = new Blend { ConstantAlpha = 255, AlphaFormat = 1 };
            if (UpdateLayeredWindow(_handle, 0, ref position, ref size, _dc, ref sourcePosition, 0, ref blend, 2) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot display floating text.");
            }
            ShowWindow(_handle, 4);
            if (_sprites.All(sprite => sprite.Progress(now) >= 1)) { _frames.Stop(); }
        }

        private void ReleaseSurface()
        {
            if (_bits != 0) { new Span<byte>((void*)_bits, _canvas.Length).Clear(); }
            if (_dc != 0 && _previousBitmap != 0 && _previousBitmap != -1) { SelectObject(_dc, _previousBitmap); }
            if (_bitmap != 0) { DeleteObject(_bitmap); }
            if (_dc != 0) { DeleteDC(_dc); }
            Array.Clear(_canvas);
            _canvas = [];
            _bits = _bitmap = _dc = _previousBitmap = 0;
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            Hide();
            if (_handle != 0) { DestroyWindow(_handle); }
        }

        private sealed class Sprite(WordRaster raster, double fromX, double fromReveal, double fromOpacity, long started)
        {
            public WordRaster Raster { get; } = raster;
            public long Started { get; set; } = started;
            public double Progress(long now) => Started == 0 ? 1 :
                Math.Clamp((now - Started) / (Stopwatch.Frequency * 0.18), 0, 1);
            private double Eased(long now) => 1 - Math.Pow(1 - Progress(now), 3);
            public double X(long now) => fromX + (Raster.X - fromX) * Eased(now);
            public double Reveal(long now) => fromReveal + (Raster.Width - fromReveal) * Eased(now);
            public double Opacity(long now) => fromOpacity + (1 - fromOpacity) * Eased(now);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static nint WindowProcedure(nint handle, uint message, nuint wparam, nint lparam)
        {
            if (message == 0x84) { return PillClicks.ContainsKey(handle) ? 1 : -1; }
            if (message is 0x202 or 0x205 && PillClicks.TryGetValue(handle, out var dismiss))
            {
                try { dismiss(); }
                catch (Exception error)
                {
                    Debug.WriteLine($"Local Voice: native preview dispatch failed: {error}");
                }
                return 0;
            }
            if (message == 0x318 && Windows.TryGetValue(handle, out var ribbon) && ribbon._dc != 0)
            {
                BitBlt((nint)wparam, 0, 0, ribbon._width, ribbon._height, ribbon._dc, 0, 0, 0x00CC0020);
                return 0;
            }
            if (message == 0x82 && Windows.Remove(handle, out var destroyed)) { destroyed._handle = 0; }
            if (message == 0x82) { PillClicks.Remove(handle); }
            return DefWindowProc(handle, message, wparam, lparam);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowClass
        {
            public uint Size, Style;
            public nint Procedure;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background, Menu, Name, SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, Bits;
            public uint Compression, ImageSize;
            public int XPixels, YPixels;
            public uint ColorsUsed, ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Blend { public byte Operation, Flags, ConstantAlpha, AlphaFormat; }

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint GetModuleHandle(string? name);
        [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
        private static partial ushort RegisterClassEx(ref WindowClass type);
        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial nint CreateWindowEx(uint extended, string type, string title, uint style,
            int x, int y, int width, int height, nint owner, nint menu, nint instance, nint parameter);
        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static partial nint DefWindowProc(nint handle, uint message, nuint wparam, nint lparam);
        [LibraryImport("user32.dll")]
        private static partial int ShowWindow(nint handle, int command);
        [LibraryImport("user32.dll")]
        private static partial int DestroyWindow(nint handle);
        [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int SetWindowText(nint handle, string text);
        [LibraryImport("user32.dll")]
        private static partial nint GetDC(nint handle);
        [LibraryImport("user32.dll")]
        private static partial int ReleaseDC(nint handle, nint dc);
        [LibraryImport("gdi32.dll", SetLastError = true)]
        private static partial nint CreateCompatibleDC(nint source);
        [LibraryImport("gdi32.dll", SetLastError = true)]
        private static partial nint CreateDIBSection(nint dc, ref BitmapHeader header, uint usage,
            out nint bits, nint section, uint offset);
        [LibraryImport("gdi32.dll", SetLastError = true)]
        private static partial nint SelectObject(nint dc, nint value);
        [LibraryImport("gdi32.dll")]
        private static partial int DeleteDC(nint dc);
        [LibraryImport("gdi32.dll")]
        private static partial int BitBlt(nint target, int x, int y, int width, int height,
            nint source, int sourceX, int sourceY, uint operation);
        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial int UpdateLayeredWindow(nint handle, nint target, ref NativePoint position,
            ref NativeSize size, nint source, ref NativePoint sourcePosition, uint key, ref Blend blend, uint flags);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint handle, int index);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint handle, int index, nint value);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint handle);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint handle, uint attribute, ref int value, uint size);
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowRgn(nint handle, nint region, int redraw);
    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint handle);
}
