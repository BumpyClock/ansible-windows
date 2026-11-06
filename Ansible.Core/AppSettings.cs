using System.Text.Json.Serialization;

namespace Ansible.Core;

/// <summary>How dictated text reaches the destination field.</summary>
public enum TextInsertionMethod
{
    /// <summary>Place text on the clipboard and send the selected paste shortcut, leaving it available for delayed readers.</summary>
    Paste,
    /// <summary>Send each character as a Unicode keystroke, separated by the typing gap.</summary>
    Type
}

public enum PasteShortcut
{
    ControlV,
    ShiftInsert,
    ControlShiftV
}

public sealed record AppSettings
{
    public const int MaxTypingGapMilliseconds = 200;

    public string? ModelId { get; init; }
    public string Language { get; init; } = "";
    public NativeBackend Backend { get; init; } = NativeBackend.Auto;
    public string? ModelsDirectory { get; init; }
    public DictationShortcut Shortcut { get; init; } = DictationShortcut.Default;
    public bool RecordChordEnabled { get; init; }
    public bool RecordToggleMode { get; init; }
    public DictationShortcut RecordShortcut { get; init; } = DictationShortcut.RecordDefault;
    public DictationShortcut RecordPasteShortcut { get; init; } = DictationShortcut.RecordPasteDefault;
    public bool IncludeVideoLink { get; init; } = true;
    public bool PushToTalk { get; init; } = true;
    public bool WelcomeCompleted { get; init; }
    public bool CollectUsage { get; init; } = true;
    public bool LocalDiagnostics { get; init; }
    public bool HideFromCapture { get; init; }
    public bool AutoDetectMeetings { get; init; } = true;
    public MeetingCleanupSettings MeetingCleanup { get; init; } = new();
    /// <summary>Show the dictation overlay under the destination caret when Windows reports its position; otherwise at the bottom of the screen.</summary>
    public bool OverlayFollowsCaret { get; init; } = true;
    public string CustomDictionary { get; init; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<TextInsertionMethod>))]
    public TextInsertionMethod InsertionMethod { get; init; } = TextInsertionMethod.Paste;
    [JsonConverter(typeof(JsonStringEnumConverter<PasteShortcut>))]
    public PasteShortcut PasteShortcut { get; init; } = PasteShortcut.ControlV;
    // 20 ms was the shortest tested gap that typed correctly into Windows 11 Notepad; 5 ms dropped a character.
    public int TypingGapMilliseconds { get; init; } = 20;
    public int MicrophoneBoostDecibels { get; init; }

    public void Validate()
    {
        if (MeetingCleanup is null) throw new InvalidDataException("Transcript cleanup settings are missing.");
        MeetingCleanup.Validate();
        if (Language is null || CustomDictionary is null || ModelId is not null && string.IsNullOrWhiteSpace(ModelId) ||
            ModelsDirectory is not null && !Path.IsPathFullyQualified(ModelsDirectory) ||
            !Shortcut.IsValid || !RecordShortcut.IsValid || !RecordPasteShortcut.IsValid ||
            Backend is not NativeBackend.Auto and not NativeBackend.Cpu and not NativeBackend.Vulkan ||
            !Enum.IsDefined(InsertionMethod) || !Enum.IsDefined(PasteShortcut) ||
            TypingGapMilliseconds is < 0 or > MaxTypingGapMilliseconds ||
            MicrophoneBoostDecibels is < 0 or > MicrophoneBoost.MaxDecibels)
            throw new InvalidDataException("The app settings contain invalid values.");
        if (RecordPasteShortcut == new DictationShortcut(2, 0x56))
            throw new InvalidDataException("Ctrl + V is used to deliver capture text and pictures. Choose another Paste capture shortcut.");
        if (IsDeliveryShortcut(Shortcut) || RecordChordEnabled &&
            (IsDeliveryShortcut(RecordShortcut) || IsDeliveryShortcut(RecordPasteShortcut)))
            throw new InvalidDataException("An activation shortcut cannot use a paste key sent by dictation or capture delivery. Choose another activation shortcut or voice paste key.");
        if (RecordChordEnabled && ActivationShortcutsOverlap)
            throw new InvalidDataException("Dictation, record, and paste capture shortcuts must not contain each other's keys. Choose separate chords.");
        if (CustomVocabulary.Normalize(CustomDictionary) != CustomDictionary)
            throw new InvalidDataException("The saved dictionary must contain normalized, unique entries.");
    }

    internal bool ActivationShortcutsOverlap => Shortcut.ConflictsWith(RecordShortcut) ||
        Shortcut.ConflictsWith(RecordPasteShortcut) || RecordShortcut.ConflictsWith(RecordPasteShortcut);

    internal bool IsDeliveryShortcut(DictationShortcut shortcut) =>
        RecordChordEnabled && shortcut == new DictationShortcut(2, 0x56) ||
        InsertionMethod == TextInsertionMethod.Paste &&
        (PasteShortcut == PasteShortcut.ControlV && shortcut == new DictationShortcut(2, 0x56) ||
         PasteShortcut == PasteShortcut.ControlShiftV && shortcut == new DictationShortcut(6, 0x56) ||
         PasteShortcut == PasteShortcut.ShiftInsert && shortcut == new DictationShortcut(4, 0x2D));
}

public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
