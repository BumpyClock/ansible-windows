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
        foreach (var placement in ShortcutKeyboardLayout.Create(_shortcut))
        {
            var highlighted = placement.Highlighted;
            var motion = new TranslateTransform();
            var key = new Border
            {
                Width = placement.Width, Height = ShortcutKeyboardLayout.KeyHeight, CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(highlighted ? 2 : 1),
                BorderBrush = Brush(highlighted ? "AccentGraphicBrush" : "AppKeyBorderBrush"),
                Background = Brush(highlighted ? "AccentSoftBrush" : "AppControlBrush"),
                RenderTransform = motion,
                Child = new TextBlock
                {
                    Text = placement.Label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush(highlighted ? "AccentForegroundBrush" : "AppSecondaryTextBrush")
                }
            };
            Canvas.SetLeft(key, placement.X);
            Canvas.SetTop(key, placement.Y);
            Keys.Children.Add(key);
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
        if (IsLoaded && _uiSettings.AnimationsEnabled) _animation.Begin();
    }

    private static Brush Brush(string name) => (Brush)Application.Current.Resources[name];
}
