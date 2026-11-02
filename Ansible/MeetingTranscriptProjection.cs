using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ansible.Core;

namespace Ansible;

/// <summary>Retains presentation rows by position while the recognizer revises earlier turns.</summary>
internal sealed class MeetingTranscriptProjection
{
    public ObservableCollection<MeetingTranscriptRow> Rows { get; } = [];

    internal bool Reconcile(MeetingDocument document)
    {
        var changed = false;
        for (var index = 0; index < document.Turns.Count; index++)
        {
            var turn = document.Turns[index];
            var showSpeaker = index == 0 || document.Turns[index - 1].SpeakerId != turn.SpeakerId;
            var speakerName = document.GetSpeakerName(turn.SpeakerId);
            if (index == Rows.Count)
            {
                Rows.Add(new MeetingTranscriptRow(turn, speakerName, showSpeaker));
                changed = true;
            }
            else changed |= Rows[index].Update(turn, speakerName, showSpeaker);
        }
        while (Rows.Count > document.Turns.Count)
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
    private MeetingTurn _turn;
    private string _speakerName;
    private bool _showSpeaker;
    private bool _isOriginalExpanded;

    internal MeetingTranscriptRow(MeetingTurn turn, string speakerName, bool showSpeaker)
    {
        _turn = turn;
        _speakerName = speakerName;
        _showSpeaker = showSpeaker;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string SpeakerId => _turn.SpeakerId;
    public string SpeakerName => _speakerName;
    public string RenameSpeakerName => $"Rename speaker {_speakerName}";
    public bool ShowSpeaker => _showSpeaker;
    public string Text => _turn.Text;
    public string OriginalText => _turn.OriginalText ?? "";
    public bool HasCleanup => _turn.OriginalText is not null && _turn.OriginalText != _turn.Text;
    public string TimingText => $"{FormatTurnTime(_turn)}{(_turn.IsProvisional ? " · provisional" : "")}{(HasCleanup ? " · wording edited" : "")}";

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

    internal bool Update(MeetingTurn turn, string speakerName, bool showSpeaker)
    {
        if (_turn == turn && _speakerName == speakerName && _showSpeaker == showSpeaker) return false;
        var previous = _turn;
        var previousHasCleanup = HasCleanup;
        var previousTiming = TimingText;
        _turn = turn;
        if (previous.SpeakerId != turn.SpeakerId) Notify(nameof(SpeakerId));
        if (_speakerName != speakerName)
        {
            _speakerName = speakerName;
            Notify(nameof(SpeakerName));
            Notify(nameof(RenameSpeakerName));
        }
        if (_showSpeaker != showSpeaker)
        {
            _showSpeaker = showSpeaker;
            Notify(nameof(ShowSpeaker));
        }
        // Do not assign unchanged TextBlock.Text. It would discard the reader's selection.
        if (previous.Text != turn.Text) Notify(nameof(Text));
        if (previous.OriginalText != turn.OriginalText) Notify(nameof(OriginalText));
        if (previousHasCleanup != HasCleanup) Notify(nameof(HasCleanup));
        if (previousTiming != TimingText) Notify(nameof(TimingText));
        // A revised boundary can put different speech at this index. Do not transfer the
        // expanded state to that speech. Cleanup and speaker corrections keep it.
        if (previous.StartSeconds != turn.StartSeconds || previous.Source != turn.Source ||
            (previous.OriginalText ?? previous.Text) != (turn.OriginalText ?? turn.Text))
            IsOriginalExpanded = false;
        return true;
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private static string FormatTurnTime(MeetingTurn turn) => turn.TimingQuality switch
    {
        MeetingTimingQuality.Aligned => $"{FormatTime(turn.StartSeconds)} · ASR-aligned",
        MeetingTimingQuality.Chunk => $"≈ {FormatTime(turn.StartSeconds)} · approximate chunk time",
        _ => "Time unavailable"
    };
}
