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
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace DictationPoc;

public sealed partial class FloatingDictationWindow : Window
{
    private readonly DictationSession _session;
    private SessionSnapshot _state;
    private readonly UiSessionObserver _observer;
    private readonly MainWindow _owner;
    private readonly double[] _levels = new double[FloatingPreviewPresentation.WaveformBarCount];
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private XamlRoot? _xamlRoot;
    private NativeCompositionPill? _pill;
    private readonly List<Task> _cleanupTasks = [];
    private Task? _closeTask;
    private bool _closed;
    private bool _visible;
    private bool _layoutQueued;
    private bool _sizing;
    private bool _reducedMotion;
    private bool _replay;
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
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _settings.AnimationsEnabledChanged -= AnimationsChanged;
        }
        _settings.TextScaleFactorChanged -= PresentationSettingsChanged;
        _settings.ColorValuesChanged -= PresentationSettingsChanged;
        if (_xamlRoot is not null) { _xamlRoot.Changed -= RootChanged; }
        _observer.Dispose();
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
        var presentation = FloatingPreviewPresentation.From(_state);
        if (_state.IsLiveOperation) { _replay = _state.IsReplay; }
        var source = _replay ? "Public WAV replay. No microphone is open." : "Microphone audio. On-device recognition.";
        AutomationProperties.SetName(PreviewRoot, presentation.Status);
        AutomationProperties.SetHelpText(PreviewRoot, $"{source} {presentation.Hint}");
        ToolTipService.SetToolTip(PreviewRoot, $"{presentation.Status}. {presentation.Hint}");
        _pill?.SetAccessibleText($"{presentation.Status}. {presentation.Hint}");
        if (!_state.IsLiveOperation)
        {
            Array.Clear(_levels);
            PaintWaveform();
        }
        if (_state.Phase is DictationPhase.Connecting or DictationPhase.Closed ||
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
        QueueLayout();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        QueueLayout();
    }

    private void ThemeChanged(FrameworkElement sender, object args)
    {
        QueueLayout();
    }

    private void PreviewSizeChanged(object sender, SizeChangedEventArgs args) => QueueLayout();
    private void PresentationSettingsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) { return; }
        QueueLayout();
    });

    private void QueueLayout()
    {
        if (_closed || _layoutQueued || _sizing) { return; }
        _layoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _layoutQueued = false;
            if (_closed || !_visible) { return; }
            SizeToContent();
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
            var width = Math.Min(FloatingPreviewPresentation.WaveformWidth, area.Width / scale);
            PillBorder.Width = width;
            PreviewRoot.Measure(new Size(width, double.PositiveInfinity));
            var bounds = FloatingPreviewPresentation.Place(area.X, area.Y, area.Width, area.Height,
                scale, width, Math.Max(1, PreviewRoot.DesiredSize.Height));
            if (AppWindow.Size.Width != bounds.Width || AppWindow.Size.Height != bounds.Height)
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(bounds.Width, bounds.Height));
            }
            var pillWidth = (int)Math.Ceiling(PillBorder.Width * scale);
            var pillHeight = (int)Math.Min(bounds.Height, Math.Ceiling(FloatingPreviewPresentation.WaveformHeight * scale));
            var shape = ((bounds.Width - pillWidth) / 2, bounds.Height - pillHeight, pillWidth, pillHeight);
            if (_windowShape != shape)
            {
                var region = CreateRectRgn(0, 0, 0, 0);
                if (region == 0) { throw new InvalidOperationException("Cannot hide the WinUI waveform host."); }
                if (SetWindowRgn(WinRT.Interop.WindowNative.GetWindowHandle(this), region, 0) == 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    DeleteObject(region);
                    throw new Win32Exception(error, "Cannot hide the WinUI waveform host.");
                }
                // The WinUI host stays region-empty; the native composition pill draws the waveform.
                _windowShape = shape;
            }
            if (AppWindow.Position.X != bounds.X || AppWindow.Position.Y != bounds.Y)
            {
                AppWindow.Move(new Windows.Graphics.PointInt32(bounds.X, bounds.Y));
            }
            _pill ??= new NativeCompositionPill(WinRT.Interop.WindowNative.GetWindowHandle(_owner),
                QueueTerminalDismiss, _cleanupTasks.Add);
            var foreground = ((SolidColorBrush)PillBorder.BorderBrush).Color;
            _pill.Configure(bounds.X + shape.Item1, bounds.Y + shape.Item2, pillWidth, pillHeight,
                scale, foreground, PreviewRoot.ActualTheme == ElementTheme.Dark, _accessibility.HighContrast,
                _settings.AdvancedEffectsEnabled);
            _pill.UpdateLevels(_levels, !_reducedMotion);
        }
        finally { _sizing = false; }
    }

    internal void HidePreview()
    {
        _pill?.Hide();
        AppWindow.Hide();
        _visible = false;
    }

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
        AutomationProperties.SetName(WaveBars, _reducedMotion ? "Current real audio energy" : "Real audio energy history");
        _pill?.UpdateLevels(_levels, !_reducedMotion);
    }

    private static void SetStyle(nint handle, int index, nint style)
    {
        if (SetWindowLongPtr(handle, index, style) == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot set the preview window style.");
        }
    }

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
        private readonly Windows.UI.Composition.ImplicitAnimationCollection _barMotion;
        private readonly Windows.System.DispatcherQueueController? _queue;
        private readonly nint _handle;
        private readonly Action<Task> _retainCleanup;
        private bool _disposed;
        private bool _animated;
        private double _scale;
        // Logical pixels. The bar row is centred in the pill.
        private const double BarWidth = 3, BarGap = 3;
        private (int Width, int Height, double Scale, Windows.UI.Color Foreground, bool Dark, bool Contrast, bool Effects) _style;

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
                _handle = NativeOverlayWindow.CreateCompositionWindow(owner, dismiss);
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
                for (var index = 0; index < FloatingPreviewPresentation.WaveformBarCount; index++)
                {
                    var geometry = _compositor.CreateRoundedRectangleGeometry();
                    var shape = _compositor.CreateSpriteShape(geometry);
                    _bars.Add(shape);
                    _visual.Shapes.Add(shape);
                }
                // Each bar geometry grows around the shape's vertical centre, so Size and Offset move together.
                _barMotion = _compositor.CreateImplicitAnimationCollection();
                foreach (var property in (string[])["Size", "Offset"])
                {
                    var animation = _compositor.CreateVector2KeyFrameAnimation();
                    animation.Target = property;
                    animation.InsertExpressionKeyFrame(1, "this.FinalValue");
                    animation.Duration = TimeSpan.FromMilliseconds(80);
                    _barMotion[property] = animation;
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
                // Inset the outline by half the 1px hairline so the stroke stays inside the pill.
                var inset = (float)(scale / 2);
                _geometry.Offset = new Vector2(inset);
                _geometry.Size = new Vector2(width - 2 * inset, height - 2 * inset);
                _geometry.CornerRadius = new Vector2(height / 2f - inset);
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
                // Translucent fill and a faint hairline over the blurred host backdrop. Without the
                // backdrop, and in high contrast, use an opaque fill and the window text colour.
                _outline.FillBrush = _compositor.CreateColorBrush(contrast
                    ? new UISettings().GetColorValue(UIColorType.Background)
                    : dark ? Windows.UI.Color.FromArgb(backdrop ? (byte)0xCC : (byte)0xFF, 0x20, 0x20, 0x20)
                    : Windows.UI.Color.FromArgb(backdrop ? (byte)0xD9 : (byte)0xFF, 0xF7, 0xF7, 0xF7));
                _outline.StrokeBrush = _compositor.CreateColorBrush(!backdrop ? foreground
                    : dark ? Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
                    : Windows.UI.Color.FromArgb(0x14, 0x00, 0x00, 0x00));
                _outline.StrokeThickness = (float)scale;
                var barBrush = _compositor.CreateColorBrush(contrast ? foreground
                    : dark ? Windows.UI.Color.FromArgb(0xFF, 0x79, 0xCF, 0xC0)
                    : Windows.UI.Color.FromArgb(0xFF, 0x18, 0x5C, 0x5E));
                var rowWidth = FloatingPreviewPresentation.WaveformBarCount * (BarWidth + BarGap) - BarGap;
                var left = (width - rowWidth * scale) / 2;
                var middle = height / 2f;
                for (var index = 0; index < _bars.Count; index++)
                {
                    _bars[index].FillBrush = barBrush;
                    _bars[index].Offset = new Vector2(
                        (float)(left + index * (BarWidth + BarGap) * scale), middle);
                    ((Windows.UI.Composition.CompositionRoundedRectangleGeometry)_bars[index].Geometry).CornerRadius =
                        new Vector2((float)(BarWidth / 2 * scale));
                }
            }
            if (SetWindowPos(_handle, -1, x, y, width, height, 0x10) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot position the native waveform overlay.");
            }
        }

        public void UpdateLevels(double[] levels, bool animate)
        {
            if (_disposed || _scale == 0) { return; }
            var motionChanged = _animated != animate;
            _animated = animate;
            for (var index = 0; index < _bars.Count; index++)
            {
                var height = (float)(FloatingPreviewPresentation.BarHeight(levels[index]) * _scale);
                var geometry = (Windows.UI.Composition.CompositionRoundedRectangleGeometry)_bars[index].Geometry;
                if (motionChanged) { geometry.ImplicitAnimations = animate ? _barMotion : null; }
                geometry.Size = new Vector2((float)(BarWidth * _scale), height);
                geometry.Offset = new Vector2(0, -height / 2);
            }
        }

        public void SetAccessibleText(string text) => NativeOverlayWindow.SetAccessibleText(_handle, text);
        public void Show() => NativeOverlayWindow.ShowCompositionWindow(_handle);
        public void Hide() => NativeOverlayWindow.HideCompositionWindow(_handle);

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
                        if (_handle != 0) { NativeOverlayWindow.DestroyCompositionWindow(_handle); }
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

    private static unsafe partial class NativeOverlayWindow
    {
        private const string ClassName = "LocalVoiceWaveform";
        private static readonly Dictionary<nint, Action> PillClicks = [];
        private static bool _registered;

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
            var handle = CreateWindowEx(0x08200088, ClassName, "Ansible overlay", 0x80000000,
                0, 0, 1, 1, owner, 0, GetModuleHandle(null), 0);
            if (handle == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            PillClicks.Add(handle, dismiss);
            return handle;
        }

        public static void ShowCompositionWindow(nint handle) => ShowWindow(handle, 4);
        public static void HideCompositionWindow(nint handle) => ShowWindow(handle, 0);
        public static void DestroyCompositionWindow(nint handle) => DestroyWindow(handle);
        public static void SetAccessibleText(nint handle, string text) => SetWindowText(handle, text);

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
