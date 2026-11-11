using System.ComponentModel;
using Ansible.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Ansible;

public sealed partial class ShortcutField : UserControl
{
    private string _shortcut = "";
    private string? _shown;
    private DictationShortcut? _pending;
    private bool _primarySeen;

    public ShortcutField()
    {
        InitializeComponent();
        Box.GotFocus += (_, _) => StartCapture();
        Box.LostFocus += (_, _) => EndCapture();
        Box.KeyDown += BoxKeyDown;
        Box.KeyUp += BoxKeyUp;
        // TextBox handles pointer presses itself, so listen for handled presses to restart capture on click.
        Box.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => StartCapture()), true);
        Unloaded += (_, _) => EndCapture();
    }

    internal event EventHandler<CancelEventArgs>? CaptureStarted;
    internal event EventHandler? CaptureEnded;
    internal event Action<DictationShortcut>? ShortcutCaptured;

    internal bool IsCapturing { get; private set; }

    internal void Describe(string name, string? automationId = null, string? help = null)
    {
        AutomationProperties.SetName(Box, name);
        if (automationId is not null) AutomationProperties.SetAutomationId(Box, automationId);
        if (help is null) return;
        ToolTipService.SetToolTip(Box, help);
        AutomationProperties.SetHelpText(Box, help);
    }

    internal void BeginCapture() => Box.Focus(FocusState.Programmatic);

    internal void ShowShortcut(string text)
    {
        _shortcut = text;
        if (!IsCapturing) ShowCurrentShortcut();
    }

    private void ShowCurrentShortcut()
    {
        if (_shown == _shortcut) return;
        _shown = _shortcut;
        Box.Text = _shortcut;
        Status.Visibility = Visibility.Collapsed;
        Keycaps.Visibility = Visibility.Visible;
        ShortcutKeycaps.Show(Keycaps, _shortcut);
    }

    private void ShowStatus(string text)
    {
        _shown = null;
        Box.Text = text;
        Keycaps.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Visible;
        Status.Text = text;
    }

    private void StartCapture()
    {
        if (IsCapturing || !IsEnabled) return;
        var starting = new CancelEventArgs();
        CaptureStarted?.Invoke(this, starting);
        if (starting.Cancel) return;
        IsCapturing = true;
        _pending = null;
        _primarySeen = false;
        ShowStatus("Press a shortcut; Esc cancels");
    }

    private void EndCapture()
    {
        if (!IsCapturing) return;
        IsCapturing = false;
        _pending = null;
        _primarySeen = false;
        ShowCurrentShortcut();
        CaptureEnded?.Invoke(this, EventArgs.Empty);
    }

    private void BoxKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!IsCapturing)
        {
            if (args.Key == VirtualKey.Enter) { args.Handled = true; StartCapture(); }
            return;
        }
        if (args.Key == VirtualKey.Tab) return;
        args.Handled = true;
        if (args.Key == VirtualKey.Escape) { EndCapture(); return; }
        uint modifiers = 0;
        if (Down(VirtualKey.Menu)) modifiers |= 1;
        if (Down(VirtualKey.Control)) modifiers |= 2;
        if (Down(VirtualKey.Shift)) modifiers |= 4;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= 8;
        if (IsModifierKey(args.Key))
        {
            if (!_primarySeen)
            {
                var candidate = new DictationShortcut(modifiers, 0);
                _pending = candidate.IsModifierOnly ? candidate : null;
                if (_pending is { } modifierOnly) ShowStatus($"{modifierOnly.DisplayText} (release to apply)");
            }
            return;
        }
        _primarySeen = true;
        _pending = null;
        var shortcut = new DictationShortcut(modifiers, (uint)args.Key);
        if (!shortcut.IsValid)
        {
            ShowStatus("Use Ctrl + Win, Alt + Win, F1-F11, or a modified letter or digit");
            return;
        }
        _pending = shortcut;
        ShowStatus($"{shortcut.DisplayText} (release to apply)");
    }

    private void BoxKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (!IsCapturing) return;
        if (_pending is not { } shortcut)
        {
            if (!IsModifierKey(args.Key)) _primarySeen = false;
            return;
        }
        if (shortcut.IsModifierOnly)
        {
            if (!new ModifierShortcutState(shortcut.Modifiers).IsChordKey((uint)args.Key)) return;
        }
        else if ((uint)args.Key != shortcut.Key) return;
        args.Handled = true;
        ShortcutCaptured?.Invoke(shortcut);
        EndCapture();
    }

    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private static bool IsModifierKey(VirtualKey key) =>
        key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows;
}
