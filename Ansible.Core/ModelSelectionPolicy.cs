namespace Ansible.Core;

/// <summary>The dictation model to use and, when a saved choice is not installed, that missing saved model ID.</summary>
public sealed record DictationModelChoice(int SelectedIndex, string? MissingSavedModelId);

/// <summary>
/// Decides which installed models may dictate or transcribe meetings, and which one is chosen. Pure: no I/O,
/// native code, or session state. Intrinsic per-model capabilities stay on <see cref="AudioModel"/>.
/// </summary>
public static class ModelSelectionPolicy
{
    /// <summary>Omitted task means ASR.</summary>
    public static bool IsDictationTask(string? task) => task is null or "asr";

    public static bool IsDiarizationTask(string? task) => task == "diar";

    /// <summary>Plain ASR models in catalog order; diarization companions are never dictation models.</summary>
    public static IReadOnlyList<AudioModel> DictationModels(IEnumerable<AudioModel> installed) =>
        installed.Where(model => IsDictationTask(model.Task)).ToArray();

    /// <summary>
    /// A saved model ID wins when installed. Unconfigured dictation prefers the default recognition model, then the
    /// first model. A missing saved ID uses the first model for this session and is reported, never replaced.
    /// </summary>
    public static DictationModelChoice ChooseDictationModel(IReadOnlyList<AudioModel> dictationModels, string? savedModelId)
    {
        var preferredId = savedModelId ?? AudioModelDefaults.RecognitionModelId;
        var selectedIndex = -1;
        for (var index = 0; index < dictationModels.Count; index++)
        {
            if (dictationModels[index].Id != preferredId) continue;
            selectedIndex = index;
            break;
        }
        return new(dictationModels.Count == 0 ? -1 : Math.Max(0, selectedIndex),
            savedModelId is not null && selectedIndex < 0 ? savedModelId : null);
    }

    public static string EffectiveDictionary(AudioModel model, string dictionary) =>
        model.SupportsCustomDictionary ? dictionary : "";

    public static bool SupportsMeeting(AudioModel model) => !IsDiarizationTask(model.Task) && model.Mode == "streaming" &&
        model.Family is "nemotron_asr" or "kroko_asr" or "vibevoice_asr_streaming";

    /// <summary>Nemotron and Kroko meetings pair with the Nemotron 3 diarization companion.</summary>
    public static bool RequiresCompanion(AudioModel model) => model.Family is "nemotron_asr" or "kroko_asr";

    public static bool IsDiarizationCompanion(AudioModel model) => model.Id == AudioModelDefaults.DiarizationModelId &&
        model.Family == "nemotron_3_diar" && IsDiarizationTask(model.Task) && model.Mode == "streaming";

    public static AudioModel? FindCompanion(IEnumerable<AudioModel> installed) =>
        installed.FirstOrDefault(IsDiarizationCompanion);

    /// <summary>
    /// A saved model ID overrides meeting selection only when it names the current selected meeting-capable model.
    /// An automatic dictation default is not a meeting override.
    /// </summary>
    public static string? ExplicitMeetingModelId(AudioModel? selected, string? savedModelId) =>
        selected is not null && savedModelId == selected.Id && SupportsMeeting(selected) ? selected.Id : null;

    /// <summary>
    /// Explicit: that model alone, never a substitute. Unconfigured: paired Nemotron, paired Kroko, then VibeVoice
    /// streaming, as documented.
    /// </summary>
    public static IReadOnlyList<AudioModel> MeetingCandidates(IReadOnlyList<AudioModel> installed, string? explicitModelId)
    {
        var companionInstalled = FindCompanion(installed) is not null;
        if (explicitModelId is not null)
        {
            var selected = installed.FirstOrDefault(candidate => candidate.Id == explicitModelId && SupportsMeeting(candidate))
                ?? throw new InvalidOperationException($"The selected meeting model '{explicitModelId}' is not installed or cannot stream meetings.");
            if (RequiresCompanion(selected) && !companionInstalled)
                throw new InvalidOperationException($"Install Nemotron 3 Diarization Q8 before starting a meeting with '{explicitModelId}'.");
            return [selected];
        }
        var candidates = new List<AudioModel>();
        if (companionInstalled)
        {
            foreach (var id in new[] { AudioModelDefaults.RecognitionModelId, AudioModelDefaults.KrokoModelId })
                if (installed.FirstOrDefault(candidate => candidate.Id == id && RequiresCompanion(candidate) && SupportsMeeting(candidate)) is { } paired)
                    candidates.Add(paired);
        }
        if (installed.FirstOrDefault(candidate => candidate.Family == "vibevoice_asr_streaming" && SupportsMeeting(candidate)) is { } vibeVoice)
            candidates.Add(vibeVoice);
        if (candidates.Count == 0)
            throw new InvalidOperationException("Install Nemotron or Kroko ASR with Nemotron 3 Diarization, or VibeVoice ASR Streaming 1.5B before starting a meeting.");
        return candidates;
    }
}
