using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ansible.Core;

namespace Ansible;

/// <summary>Groups adjacent speaker turns without changing the saved recognition boundaries.</summary>
internal sealed class MeetingTranscriptProjection
{
    public ObservableCollection<MeetingTranscriptRow> Rows { get; } = [];

    internal bool Reconcile(MeetingDocument document)
    {
        var changed = false;
        var rowIndex = 0;
        for (var start = 0; start < document.Turns.Count;)
        {
            var speaker = document.Turns[start].SpeakerId;
            var end = start + 1;
            while (end < document.Turns.Count && document.Turns[end].SpeakerId == speaker) end++;
            var speakerName = document.GetSpeakerName(speaker);
            if (rowIndex == Rows.Count)
            {
                Rows.Add(new MeetingTranscriptRow(document.Turns, start, end - start, speakerName));
                changed = true;
            }
            else changed |= Rows[rowIndex].Update(document.Turns, start, end - start, speakerName);
            rowIndex++;
            start = end;
        }
        while (Rows.Count > rowIndex)
        {
            Rows.RemoveAt(Rows.Count - 1);
            changed = true;
        }
        return changed;
    }
}

// Public for the XAML compiler's typed bindings. This type has no WinUI dependency so the
// reconciliation and recycled-row state can be checked without starting a window.
public sealed class MeetingTranscriptRow : INotifyPropertyChanged
{
    private MeetingTurn[] _turns = [];
    private string _speakerName = "";
    private string _text = "";
    private string _originalText = "";
    private string _timingText = "";
    private bool _hasCleanup;
    private bool _isOriginalExpanded;

    internal MeetingTranscriptRow(IReadOnlyList<MeetingTurn> turns, int start, int count, string speakerName)
    {
        Update(turns, start, count, speakerName);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string SpeakerId => _turns[0].SpeakerId;
    public string SpeakerName => _speakerName;
    public string RenameSpeakerName => $"Rename speaker {_speakerName}";
    public string Text => _text;
    public string OriginalText => _originalText;
    public bool HasCleanup => _hasCleanup;
    public string TimingText => _timingText;

    public bool IsOriginalExpanded
    {
        get => _isOriginalExpanded;
        set
        {
            if (_isOriginalExpanded == value) return;
            _isOriginalExpanded = value;
            Notify();
        }
    }

    internal bool Update(IReadOnlyList<MeetingTurn> turns, int start, int count, string speakerName)
    {
        var turnsChanged = count != _turns.Length;
        for (var index = 0; !turnsChanged && index < count; index++)
            turnsChanged = _turns[index] != turns[start + index];
        if (!turnsChanged && _speakerName == speakerName) return false;
        if (_speakerName != speakerName)
        {
            _speakerName = speakerName;
            Notify(nameof(SpeakerName));
            Notify(nameof(RenameSpeakerName));
        }
        if (turnsChanged)
        {
            var previous = _turns.FirstOrDefault();
            var first = turns[start];
            _turns = new MeetingTurn[count];
            for (var index = 0; index < count; index++) _turns[index] = turns[start + index];
            if (previous?.SpeakerId != first.SpeakerId) Notify(nameof(SpeakerId));
            var text = string.Join(" ", _turns.Select(turn => turn.Text));
            var original = _turns.Any(turn => turn.OriginalText is not null)
                ? string.Join(" ", _turns.Select(turn => turn.OriginalText ?? turn.Text)) : "";
            var hasCleanup = _turns.Any(turn => turn.OriginalText is not null && turn.OriginalText != turn.Text);
            var timing = $"{FormatTurnTime(_turns)}{(_turns.Any(turn => turn.IsProvisional) ? " · provisional" : "")}{(hasCleanup ? " · wording edited" : "")}";
            // Unchanged completed blocks keep text selection while the live tail is revised.
            if (_text != text) { _text = text; Notify(nameof(Text)); }
            if (_originalText != original) { _originalText = original; Notify(nameof(OriginalText)); }
            if (_hasCleanup != hasCleanup) { _hasCleanup = hasCleanup; Notify(nameof(HasCleanup)); }
            if (_timingText != timing) { _timingText = timing; Notify(nameof(TimingText)); }
            // Extending or cleaning the same speaker block keeps its expanded original.
            if (previous is not null && (previous.StartSeconds != first.StartSeconds || previous.Source != first.Source ||
                (previous.OriginalText ?? previous.Text) != (first.OriginalText ?? first.Text)))
                IsOriginalExpanded = false;
        }
        return true;
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private static string FormatTurnTime(IReadOnlyList<MeetingTurn> turns)
    {
        if (turns.Any(turn => turn.TimingQuality == MeetingTimingQuality.Unknown)) return "Time unavailable";
        var time = FormatTime(turns[0].StartSeconds);
        if (turns.Count > 1) time += $" to {FormatTime(turns.Max(turn => turn.EndSeconds))}";
        return turns.All(turn => turn.TimingQuality == MeetingTimingQuality.Aligned)
            ? $"{time} · ASR-aligned" : $"≈ {time} · approximate chunk time";
    }
}
