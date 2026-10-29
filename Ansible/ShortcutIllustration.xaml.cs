using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Ansible;

public sealed partial class ShortcutIllustration : UserControl
{
    private readonly UISettings _uiSettings = new();
    private readonly Storyboard _animation = new();
    private DictationShortcut _shortcut = DictationShortcut.Default;

    public ShortcutIllustration()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _uiSettings.AnimationsEnabledChanged += AnimationPreferenceChanged;
            DrawKeyboard();
        };
        Unloaded += (_, _) =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _uiSettings.AnimationsEnabledChanged -= AnimationPreferenceChanged;
            _animation.Stop();
        };
        ActualThemeChanged += (_, _) => { if (IsLoaded) DrawKeyboard(); };
    }

    internal void SetShortcut(DictationShortcut shortcut)
    {
        if (_shortcut == shortcut && Keys.Children.Count > 0) return;
        _shortcut = shortcut;
        DrawKeyboard();
    }

    private void AnimationPreferenceChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (IsLoaded) DrawKeyboard(); });

    private void DrawKeyboard()
    {
        _animation.Stop();
        _animation.Children.Clear();
        Keys.Children.Clear();
        var selected = _shortcut.DisplayText.Split(" + ").ToHashSet(StringComparer.Ordinal);
        string[][] rows =
        [
            ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11"],
            ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"],
            ["A", "S", "D", "F", "G", "H", "J", "K", "L"],
            ["Z", "X", "C", "V", "B", "N", "M"],
            ["Ctrl", "Win", "Alt", "Space", "Alt"]
        ];
        for (var row = 0; row < rows.Length; row++)
        {
            var x = row == 2 ? 12.0 : row == 3 ? 36.0 : 0;
            foreach (var label in rows[row])
            {
                var width = label == "Space" ? 190 : row == 4 ? 65 : row == 0 ? 38 : 43;
                var highlighted = selected.Contains(label);
                var motion = new TranslateTransform();
                var key = new Border
                {
                    Width = width, Height = 34, CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(highlighted ? 2 : 1),
                    BorderBrush = Brush(highlighted ? "AccentGraphicBrush" : "AppKeyBorderBrush"),
                    Background = Brush(highlighted ? "AccentSoftBrush" : "AppControlBrush"),
                    RenderTransform = motion,
                    Child = new TextBlock
                    {
                        Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brush(highlighted ? "AccentForegroundBrush" : "AppSecondaryTextBrush")
                    }
                };
                Canvas.SetLeft(key, x);
                Canvas.SetTop(key, row * 40);
                Keys.Children.Add(key);
                x += width + 5;
                if (!highlighted) continue;
                var press = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                press.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
                press.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(0.5), Value = 4 });
                press.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(1.7), Value = 4 });
                press.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(2.1), Value = 0 });
                press.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(3.2), Value = 0 });
                Storyboard.SetTarget(press, motion);
                Storyboard.SetTargetProperty(press, "Y");
                _animation.Children.Add(press);
            }
        }
        if (IsLoaded && _uiSettings.AnimationsEnabled) _animation.Begin();
    }

    private static Brush Brush(string name) => (Brush)Application.Current.Resources[name];
}
