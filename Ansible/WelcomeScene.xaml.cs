using System.Diagnostics;
using System.Numerics;
using Ansible.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace Ansible;

/// <summary>What the scene's waveform pill shows. Only the practice step passes a live session state.</summary>
internal enum ScenePill { Resting, Listening, Working }

/// <summary>
/// The welcome lesson's illustration: keycaps for the saved shortcut, a waveform pill, and a text field.
/// Nothing loops on its own. A cap presses while its real key is held, the pill follows the real session, and
/// the field lights only after a real practice insertion. Reduced motion keeps every state change without travel.
/// </summary>
public sealed partial class WelcomeScene : UserControl
{
    private readonly UISettings _uiSettings = new();
    private readonly List<Keycap> _caps = [];
    private readonly HashSet<VirtualKey> _down = [];
    private readonly Rectangle[] _bars = new Rectangle[FloatingPreviewPresentation.WaveformBarCount];
    private readonly Stopwatch _levelClock = new();
    private readonly Storyboard _working = new() { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
    private Storyboard? _fill;
    private DictationShortcut? _shortcut;
    private ScenePill _pill;
    private bool _chordHeld;
    private double _level;

    public WelcomeScene()
    {
        InitializeComponent();
        for (var index = 0; index < _bars.Length; index++)
        {
            _bars[index] = new Rectangle { Style = (Style)Resources["SceneBarStyle"] };
            Bars.Children.Add(_bars[index]);
        }
        var pulse = new DoubleAnimation { From = 1, To = 0.35, Duration = TimeSpan.FromMilliseconds(650) };
        Storyboard.SetTarget(pulse, Bars);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        _working.Children.Add(pulse);
        PaintBars();
        Unloaded += (_, _) => _working.Stop();
    }

    private bool Animated => _uiSettings.AnimationsEnabled;

    internal void Render(DictationShortcut shortcut, string prompt, ScenePill pill)
    {
        if (_shortcut != shortcut) BuildCaps(shortcut);
        Prompt.Text = prompt;
        if (_pill == pill) return;
        _pill = pill;
        ListeningRing.OpacityTransition = Animated ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) } : null;
        ListeningRing.Opacity = pill == ScenePill.Listening ? 1 : 0;
        if (pill != ScenePill.Listening) _level = 0;
        _levelClock.Restart();
        PaintBars();
        if (pill == ScenePill.Working && Animated) _working.Begin();
        else _working.Stop();
    }

    /// <summary>A key the lesson saw go down or up. Modifier left and right variants map to the same cap.</summary>
    internal void KeyChanged(VirtualKey key, bool down)
    {
        if (down) _down.Add(key);
        else
        {
            _down.Remove(key);
            // A key released while another window had focus never reports KeyUp here.
            _down.RemoveWhere(held => !InputKeyboardSource.GetKeyStateForCurrentThread(held).HasFlag(CoreVirtualKeyStates.Down));
        }
        RenderCaps();
    }

    /// <summary>
    /// The global shortcut's own press and release. RegisterHotKey swallows the primary key's down, so the lesson
    /// never sees it; this holds the whole chord down until the hotkey reports release.
    /// </summary>
    internal void ChordChanged(bool held)
    {
        _chordHeld = held;
        RenderCaps();
    }

    /// <summary>Real microphone energy from the session. Shown only while the pill is listening.</summary>
    internal void ShowLevel(double pcmLevel)
    {
        if (_pill != ScenePill.Listening) return;
        var measured = FloatingPreviewPresentation.MeterLevel(pcmLevel);
        var elapsed = _levelClock.Elapsed.TotalSeconds;
        _levelClock.Restart();
        _level = Animated ? FloatingPreviewPresentation.Release(_level, measured, elapsed) : measured;
        PaintBars();
    }

    /// <summary>Lights the field after a real practice insertion.</summary>
    internal void ShowInserted()
    {
        _fill?.Stop();
        _fill = null;
        LineFill1.ScaleX = LineFill2.ScaleX = LineFill3.ScaleX = 1;
        if (!Animated) return;
        var fill = new Storyboard();
        foreach (var (line, index) in new[] { LineFill1, LineFill2, LineFill3 }.Select((line, index) => (line, index)))
        {
            var grow = new DoubleAnimation
            {
                From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(260), BeginTime = TimeSpan.FromMilliseconds(90 * index),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(grow, line);
            Storyboard.SetTargetProperty(grow, "ScaleX");
            fill.Children.Add(grow);
        }
        _fill = fill;
        fill.Begin();
    }

    /// <summary>Returns the scene to rest: no keys held, an empty field, and a resting pill.</summary>
    internal void Reset()
    {
        _down.Clear();
        _chordHeld = false;
        RenderCaps();
        _fill?.Stop();
        _fill = null;
        LineFill1.ScaleX = LineFill2.ScaleX = LineFill3.ScaleX = 0;
    }

    private void BuildCaps(DictationShortcut shortcut)
    {
        _shortcut = shortcut;
        _caps.Clear();
        Keys.Children.Clear();
        foreach (var (label, keys) in CapsFor(shortcut))
        {
            if (Keys.Children.Count > 0) Keys.Children.Add(new TextBlock { Text = "+", Style = (Style)Resources["ScenePlusStyle"] });
            var cap = new Keycap(label, keys, Resources);
            _caps.Add(cap);
            Keys.Children.Add(cap.Root);
        }
        RenderCaps();
    }

    // DisplayText order: Ctrl, Win, Shift, Alt, then the primary key, whose label DisplayText owns.
    private static IEnumerable<(string Label, VirtualKey[] Keys)> CapsFor(DictationShortcut shortcut)
    {
        if ((shortcut.Modifiers & 0x2) != 0) yield return ("Ctrl", [VirtualKey.Control, VirtualKey.LeftControl, VirtualKey.RightControl]);
        if ((shortcut.Modifiers & 0x8) != 0) yield return ("Win", [VirtualKey.LeftWindows, VirtualKey.RightWindows]);
        if ((shortcut.Modifiers & 0x4) != 0) yield return ("Shift", [VirtualKey.Shift, VirtualKey.LeftShift, VirtualKey.RightShift]);
        if ((shortcut.Modifiers & 0x1) != 0) yield return ("Alt", [VirtualKey.Menu, VirtualKey.LeftMenu, VirtualKey.RightMenu]);
        if (!shortcut.IsModifierOnly) yield return (shortcut.DisplayText.Split(" + ")[^1], [(VirtualKey)shortcut.Key]);
    }

    private void RenderCaps()
    {
        foreach (var cap in _caps) cap.SetPressed(_chordHeld || cap.Keys.Any(_down.Contains), Animated);
    }

    private void PaintBars()
    {
        for (var index = 0; index < _bars.Length; index++)
            _bars[index].Height = FloatingPreviewPresentation.BarHeight(_pill == ScenePill.Listening ? _level : 0, index);
    }

    /// <summary>
    /// One keycap in the shared keycap style. A press nudges the cap down and fades its accent layer and label in
    /// over the resting ones, so a press reads even with reduced motion.
    /// </summary>
    private sealed class Keycap
    {
        private const float Travel = 2;
        private readonly Grid _face;
        private readonly Border _lit;
        private readonly TextBlock _label;
        private readonly TextBlock _litLabel;
        private bool _pressed;

        public Keycap(string text, VirtualKey[] keys, ResourceDictionary styles)
        {
            Keys = keys;
            _lit = new Border { Style = (Style)styles["SceneCapLitStyle"], Opacity = 0 };
            _label = new TextBlock { Text = text, Style = (Style)styles["SceneCapLabelStyle"] };
            _litLabel = new TextBlock { Text = text, Style = (Style)styles["SceneCapLitLabelStyle"], Opacity = 0 };
            _face = new Grid { MinWidth = 52, Height = 44 };
            _face.Children.Add(new Border { Style = (Style)styles["SceneCapFaceStyle"] });
            _face.Children.Add(_lit);
            _face.Children.Add(_label);
            _face.Children.Add(_litLabel);
            ElementCompositionPreview.SetIsTranslationEnabled(_face, true);
            Root = _face;
        }

        public FrameworkElement Root { get; }
        public VirtualKey[] Keys { get; }

        public void SetPressed(bool pressed, bool animate)
        {
            if (_pressed == pressed) return;
            _pressed = pressed;
            var fade = animate ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(pressed ? 70 : 180) } : null;
            _lit.OpacityTransition = _label.OpacityTransition = _litLabel.OpacityTransition = fade;
            _lit.Opacity = _litLabel.Opacity = pressed ? 1 : 0;
            _label.Opacity = pressed ? 0 : 1;
            var visual = ElementCompositionPreview.GetElementVisual(_face);
            if (!animate)
            {
                visual.StopAnimation("Translation");
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
                return;
            }
            // A firm press down, then an underdamped spring back up past rest.
            var spring = visual.Compositor.CreateSpringVector3Animation();
            spring.FinalValue = new Vector3(0, pressed ? Travel : 0, 0);
            spring.DampingRatio = pressed ? 0.9f : 0.4f;
            spring.Period = TimeSpan.FromMilliseconds(pressed ? 25 : 45);
            visual.StartAnimation("Translation", spring);
        }
    }
}
