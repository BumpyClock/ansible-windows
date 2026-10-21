namespace Ansible.Core;

public enum DictationPhase
{
    Disconnected, Connecting, Ready, Preparing, Recording, Finishing,
    Transcribing, Cancelling, MaintainingModels, RecoveryRequired, Closing, Closed
}

public enum SessionActivity { None, Connecting, Dictation, Replay, File, ModelMaintenance }
public enum NoticeKind { Information, Success, Warning, Error }
public enum SessionOutcomeKind { Completed, Cancelled, TimedOut, Failed }
public sealed record SessionNotice(NoticeKind Kind, string Title, string Message, bool ShowInStatus = false, bool IsDismissed = false)
{
    public bool ShouldDisplay => !IsDismissed && (ShowInStatus || Kind is NoticeKind.Warning or NoticeKind.Error);
}
public sealed record SessionOutcome(SessionOutcomeKind Kind, RecognitionResult? Result = null, Exception? Error = null);

public sealed record SessionSnapshot
{
    public required long Version { get; init; }
    public Guid? OperationId { get; init; }
    public SessionOutcomeKind? Outcome { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool MeetingActive { get; init; }
    public bool IsCaptureOpen { get; init; }
    public required DictationPhase Phase { get; init; }
    public required SessionActivity Activity { get; init; }
    public required SessionNotice Notice { get; init; }
    public required IReadOnlyList<AudioModel> Models { get; init; }
    public required int SelectedIndex { get; init; }
    public required string ModelsDirectory { get; init; }
    public required string BackendVersion { get; init; }
    public string DeviceSelectionSummary { get; init; } = "";
    public DeviceComparisonSnapshot? DeviceComparison { get; init; }
    public AppSettings Settings { get; init; } = new();
    public string Language => Settings.Language;
    public required string Transcript { get; init; }
    public RecognitionResult? Result { get; init; }
    public UsageDocument? Usage { get; init; }
    public string? UsageError { get; init; }
    public AudioModel? SelectedModel => SelectedIndex >= 0 && SelectedIndex < Models.Count ? Models[SelectedIndex] : null;
    public bool IsIdle => !MeetingActive && Phase is DictationPhase.Ready or DictationPhase.Disconnected;
    public bool IsReplay => Activity == SessionActivity.Replay;
    public bool CanStart => !MeetingActive && Phase == DictationPhase.Ready && SelectedModel?.SupportsMicrophone == true;
    public bool CanFinish => Phase == DictationPhase.Recording && Activity == SessionActivity.Dictation;
    public bool CanCancel => Phase is DictationPhase.Preparing or DictationPhase.Recording or DictationPhase.Finishing or DictationPhase.Transcribing;
    public bool IsLiveOperation => Activity is SessionActivity.Dictation or SessionActivity.Replay &&
        Phase is DictationPhase.Preparing or DictationPhase.Recording or DictationPhase.Finishing or DictationPhase.Cancelling;
    public int? Words => Result?.SpokenWords;
}
