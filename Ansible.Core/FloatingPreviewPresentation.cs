namespace Ansible.Core;

public sealed record FloatingPreviewPresentation(
    string Status, string Hint, bool IsTerminal)
{
    public const double WaveformWidth = 220;
    public const double WaveformHeight = 56;
    public const int WaveformBarCount = 24;
    public const double BarMinHeight = 4;
    public const double BarMaxHeight = 28;

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

    public static double BarHeight(double meterLevel) =>
        BarMinHeight + Math.Clamp(meterLevel, 0, 1) * (BarMaxHeight - BarMinHeight);

    public static PreviewPlacement Place(
        int workX, int workY, int workWidth, int workHeight, double scale, double contentWidth, double contentHeight)
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
        var bottomGap = (int)Math.Min(workHeight - height, Math.Ceiling(16 * scale));
        return new(workX + (workWidth - width) / 2, workY + workHeight - height - bottomGap, width, height);
    }
}

public readonly record struct PreviewPlacement(int X, int Y, int Width, int Height);
