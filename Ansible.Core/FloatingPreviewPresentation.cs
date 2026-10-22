namespace Ansible.Core;

public sealed record FloatingPreviewPresentation(
    string Status, string Hint, bool IsTerminal)
{
    // Logical pixels. The pill is a compact capsule; the bar row is centred inside it.
    public const double WaveformWidth = 128;
    public const double WaveformHeight = 32;
    public const int WaveformBarCount = 15;
    public const double BarMinHeight = 3;
    public const double BarMaxHeight = 20;
    // Physical-pixel gap between the caret line and the pill, before scaling.
    public const double CaretGap = 8;
    // Bars fall at most this much of the full meter range per second, so a loud syllable
    // decays visibly instead of snapping to silence between audio packets.
    public const double ReleasePerSecond = 4;

    public static FloatingPreviewPresentation From(SessionSnapshot state)
    {
        var error = state.Notice.Kind == NoticeKind.Error || state.Phase == DictationPhase.RecoveryRequired;
        var status = state.Phase switch
        {
            DictationPhase.Preparing => "Preparing",
            DictationPhase.Recording => state.IsReplay ? "Replaying WAV" : "Listening",
            DictationPhase.Finishing => "Finishing",
            DictationPhase.Cancelling => "Cancelling",
            DictationPhase.Closing => "Releasing resources",
            _ when error => "Recognition failed",
            _ => state.Notice.Title
        };
        var hint = state.Phase switch
        {
            DictationPhase.Cancelling => "Waiting for the native step to release resources.",
            DictationPhase.Closing => "Joining owned work before closing.",
            _ when error => state.Notice.Message,
            DictationPhase.Preparing => "Loading the model before audio starts.",
            DictationPhase.Finishing => "Recognizing audio. Waiting for final text.",
            DictationPhase.Recording when state.SelectedModel?.Preview is "final-only" or "buffered" =>
                state.IsReplay ? "Text appears after the WAV replay." : "Text appears after Finish.",
            DictationPhase.Recording => "Waiting for the model's first text.",
            _ when state.Notice.Kind == NoticeKind.Success => state.Transcript.Length == 0
                ? "No speech text returned." : "Recognition complete. Speech stays on this device.",
            _ => state.Notice.Message
        };
        var terminal = state.IsIdle || state.Phase == DictationPhase.RecoveryRequired;
        return new(status, hint, terminal);
    }

    public static double MeterLevel(double pcmLevel) =>
        double.IsFinite(pcmLevel) ? Math.Clamp(pcmLevel * 8, 0, 1) : 0;

    /// <summary>
    /// Smooths a measured meter level for display. Rises follow the measurement at once; falls are
    /// bounded by <see cref="ReleasePerSecond"/> over the elapsed time so the shape decays instead of flickering.
    /// </summary>
    public static double Release(double shown, double measured, double elapsedSeconds)
    {
        measured = Math.Clamp(double.IsFinite(measured) ? measured : 0, 0, 1);
        shown = Math.Clamp(double.IsFinite(shown) ? shown : 0, 0, 1);
        if (measured >= shown || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) { return measured; }
        return Math.Max(measured, shown - ReleasePerSecond * elapsedSeconds);
    }

    /// <summary>
    /// Fixed per-bar weight: the centre bar carries the full level and the outer bars a smaller
    /// share, so the whole row breathes with voice activity instead of scrolling through time.
    /// </summary>
    public static double BarWeight(int index)
    {
        if (index < 0 || index >= WaveformBarCount) { throw new ArgumentOutOfRangeException(nameof(index)); }
        var centre = (WaveformBarCount - 1) / 2.0;
        var distance = Math.Abs(index - centre) / centre;
        // Raised cosine from 1 at the centre to 0.3 at the edges.
        return 0.3 + 0.7 * (0.5 + 0.5 * Math.Cos(Math.PI * distance));
    }

    public static double BarHeight(double meterLevel, int index) =>
        BarMinHeight + Math.Clamp(meterLevel, 0, 1) * BarWeight(index) * (BarMaxHeight - BarMinHeight);

    /// <summary>
    /// Positions the pill in physical pixels. With a caret, the pill sits centred under the caret line,
    /// flips above it when the work area ends, and clamps horizontally. Without one it rests at the
    /// bottom centre of the work area.
    /// </summary>
    public static PreviewPlacement Place(
        int workX, int workY, int workWidth, int workHeight, double scale, double contentWidth, double contentHeight,
        CaretBounds? caret = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workHeight);
        if (!double.IsFinite(scale) || scale <= 0) { throw new ArgumentOutOfRangeException(nameof(scale)); }
        if (!double.IsFinite(contentWidth) || contentWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentWidth));
        }
        if (!double.IsFinite(contentHeight) || contentHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentHeight));
        }
        var width = (int)Math.Min(workWidth, Math.Ceiling(contentWidth * scale));
        var height = (int)Math.Min(workHeight, Math.Ceiling(contentHeight * scale));
        if (caret is { } anchor && anchor.IsUsable)
        {
            var gap = (int)Math.Ceiling(CaretGap * scale);
            var x = Math.Clamp(anchor.Left + anchor.Width / 2 - width / 2, workX, workX + workWidth - width);
            var below = anchor.Top + anchor.Height + gap;
            if (below + height <= workY + workHeight) { return new(x, below, width, height); }
            var above = anchor.Top - gap - height;
            if (above >= workY) { return new(x, above, width, height); }
        }
        var bottomGap = (int)Math.Min(workHeight - height, Math.Ceiling(16 * scale));
        return new(workX + (workWidth - width) / 2, workY + workHeight - height - bottomGap, width, height);
    }
}

public readonly record struct PreviewPlacement(int X, int Y, int Width, int Height);

/// <summary>Screen rectangle of the destination text caret in physical pixels.</summary>
public readonly record struct CaretBounds(int Left, int Top, int Width, int Height)
{
    // A caret taller than this is not a text line; a provider returned a page or control bound instead.
    public const int MaxHeight = 400;

    // A wide rectangle is a selection; the pill still anchors on its centre line.
    public bool IsUsable => Width >= 0 && Height > 0 && Height <= MaxHeight;
}
