namespace DictationPoc.Core;

public sealed record FloatingPreviewPresentation(
    string Status, string Hint, string Transcript, string Ribbon, bool ShowRibbon, bool ShowHintWithTranscript,
    bool IsTerminal, bool AutoDismiss)
{
    public const double Width = 400;
    public const double WaveformWidth = 160;

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
        var ribbon = (state.SelectedModel?.Preview == "live" && state.IsLiveOperation ||
            terminal || state.Phase is DictationPhase.Cancelling or DictationPhase.Closing) &&
            !string.IsNullOrWhiteSpace(state.Transcript);
        return new(status, hint, state.Transcript, ribbon ? LastWords(state.Transcript) : "", ribbon,
            error || state.Phase is DictationPhase.Cancelling or DictationPhase.Closing,
            terminal, terminal && state.Notice.Kind == NoticeKind.Success && !error);
    }

    public static int[] ReuseWords(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        if (previous.Count > 6) { throw new ArgumentOutOfRangeException(nameof(previous)); }
        if (current.Count > 6) { throw new ArgumentOutOfRangeException(nameof(current)); }
        var lengths = new int[previous.Count + 1, current.Count + 1];
        for (var old = 1; old <= previous.Count; old++)
        {
            for (var next = 1; next <= current.Count; next++)
            {
                lengths[old, next] = previous[old - 1] == current[next - 1]
                    ? lengths[old - 1, next - 1] + 1 : Math.Max(lengths[old - 1, next], lengths[old, next - 1]);
            }
        }
        var result = Enumerable.Repeat(-1, current.Count).ToArray();
        var oldIndex = previous.Count;
        var newIndex = current.Count;
        while (oldIndex > 0 && newIndex > 0)
        {
            if (previous[oldIndex - 1] == current[newIndex - 1])
            {
                result[--newIndex] = --oldIndex;
            }
            else if (lengths[oldIndex - 1, newIndex] >= lengths[oldIndex, newIndex - 1]) { oldIndex--; }
            else { newIndex--; }
        }
        if (previous.Count > 0 && !result.Contains(previous.Count - 1))
        {
            var partial = previous[^1];
            for (var index = 0; index < current.Count; index++)
            {
                if (result[index] < 0 && (current[index].StartsWith(partial, StringComparison.Ordinal) ||
                    partial.StartsWith(current[index], StringComparison.Ordinal)))
                {
                    result[index] = previous.Count - 1;
                    break;
                }
            }
        }
        if (previous.Count == current.Count)
        {
            for (var index = 0; index < current.Count; index++)
            {
                if (result[index] < 0 && !result.Contains(index) &&
                    (current[index].StartsWith(previous[index], StringComparison.Ordinal) ||
                     previous[index].StartsWith(current[index], StringComparison.Ordinal)))
                {
                    result[index] = index;
                }
            }
        }
        return result;
    }

    public static string LastWords(string text)
    {
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) { end--; }
        var start = end;
        for (var words = 0; words < 6 && start > 0; words++)
        {
            while (start > 0 && !char.IsWhiteSpace(text[start - 1])) { start--; }
            if (words == 5) { break; }
            while (start > 0 && char.IsWhiteSpace(text[start - 1])) { start--; }
        }
        var tail = new System.Text.StringBuilder(Math.Min(end - start, 256));
        for (var index = start; index < end; index++)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                if (tail.Length > 0 && tail[^1] != ' ') { tail.Append(' '); }
            }
            else { tail.Append(text[index]); }
        }
        return tail.ToString();
    }

    public static double MeterLevel(double pcmLevel) =>
        double.IsFinite(pcmLevel) ? Math.Clamp(pcmLevel * 8, 0, 1) : 0;

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
