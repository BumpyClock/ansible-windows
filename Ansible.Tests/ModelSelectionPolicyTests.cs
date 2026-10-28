using Ansible.Core;

namespace Ansible.Tests;

public sealed class ModelSelectionPolicyTests
{
    private static readonly AudioModel Nemotron = new()
    {
        Id = AudioModelDefaults.RecognitionModelId, Family = "nemotron_asr", Task = "asr", Mode = "streaming"
    };

    private static readonly AudioModel Kroko = new()
    {
        Id = AudioModelDefaults.KrokoModelId, Family = "kroko_asr", Task = "asr", Mode = "streaming", SupportsCustomDictionary = true
    };

    private static readonly AudioModel VibeVoice = new()
    {
        Id = "vibevoice-streaming-1.5b-q4", Family = "vibevoice_asr_streaming", Mode = "streaming"
    };

    private static readonly AudioModel Companion = new()
    {
        Id = AudioModelDefaults.DiarizationModelId, Family = "nemotron_3_diar", Task = "diar", Mode = "streaming"
    };

    private static readonly AudioModel Moonshine = new() { Id = "moonshine-tiny", Family = "moonshine_asr", Mode = "streaming" };

    private static readonly AudioModel OfflineNemotron = Nemotron with { Id = "nemotron-offline", Mode = "offline" };

    [Theory]
    [InlineData(null, true, false)]
    [InlineData("asr", true, false)]
    [InlineData("diar", false, true)]
    [InlineData("other", false, false)]
    public void TaskClassificationTreatsOmittedTaskAsAsr(string? task, bool dictation, bool diarization)
    {
        Assert.Equal(dictation, ModelSelectionPolicy.IsDictationTask(task));
        Assert.Equal(diarization, ModelSelectionPolicy.IsDiarizationTask(task));
    }

    [Fact]
    public void DictationModelsExcludeDiarizationCompanionsAndKeepOrder()
    {
        var models = ModelSelectionPolicy.DictationModels([Companion, Moonshine, Nemotron, Kroko with { Task = "other" }]);

        Assert.Equal(new[] { Moonshine, Nemotron }, models);
    }

    [Fact]
    public void UnconfiguredDictationPrefersTheDefaultRecognitionModel()
    {
        var choice = ModelSelectionPolicy.ChooseDictationModel([Moonshine, Kroko, Nemotron], null);

        Assert.Equal(new DictationModelChoice(2, null), choice);
    }

    [Fact]
    public void UnconfiguredDictationWithoutTheDefaultUsesTheFirstModel()
    {
        var choice = ModelSelectionPolicy.ChooseDictationModel([Moonshine, Kroko], null);

        Assert.Equal(new DictationModelChoice(0, null), choice);
    }

    [Fact]
    public void InstalledSavedModelWinsOverTheDefault()
    {
        var choice = ModelSelectionPolicy.ChooseDictationModel([Nemotron, Moonshine], Moonshine.Id);

        Assert.Equal(new DictationModelChoice(1, null), choice);
    }

    [Fact]
    public void MissingSavedModelUsesTheFirstModelAndReportsTheSavedId()
    {
        var choice = ModelSelectionPolicy.ChooseDictationModel([Moonshine, Nemotron], "absent");

        Assert.Equal(new DictationModelChoice(0, "absent"), choice);
    }

    [Fact]
    public void MissingSavedModelWithoutModelsSelectsNothingAndReportsTheSavedId()
    {
        Assert.Equal(new DictationModelChoice(-1, "absent"), ModelSelectionPolicy.ChooseDictationModel([], "absent"));
    }

    [Fact]
    public void EmptyDictationListSelectsNothing()
    {
        Assert.Equal(new DictationModelChoice(-1, null), ModelSelectionPolicy.ChooseDictationModel([], null));
    }

    [Fact]
    public void SavedDiarizationIdIsMissingAfterDictationFiltering()
    {
        var models = ModelSelectionPolicy.DictationModels([Companion, Moonshine]);

        Assert.Equal(new DictationModelChoice(0, Companion.Id),
            ModelSelectionPolicy.ChooseDictationModel(models, Companion.Id));
    }

    [Theory]
    [InlineData(true, "Mother Nature", "Mother Nature")]
    [InlineData(false, "Mother Nature", "")]
    [InlineData(true, "", "")]
    public void EffectiveDictionaryOnlyReachesDictionaryCapableModels(bool supported, string dictionary, string expected)
    {
        var model = Moonshine with { SupportsCustomDictionary = supported };

        Assert.Equal(expected, ModelSelectionPolicy.EffectiveDictionary(model, dictionary));
    }

    [Theory]
    [InlineData("nemotron_asr", null, "streaming", true)]
    [InlineData("kroko_asr", "asr", "streaming", true)]
    [InlineData("vibevoice_asr_streaming", null, "streaming", true)]
    [InlineData("nemotron_asr", "asr", "offline", false)]
    [InlineData("nemotron_asr", "diar", "streaming", false)]
    [InlineData("moonshine_asr", null, "streaming", false)]
    [InlineData("vibevoice_asr", null, "offline", false)]
    [InlineData("nemotron_3_diar", "diar", "streaming", false)]
    public void MeetingSupportRequiresStreamingNemotronKrokoOrVibeVoice(string family, string? task, string mode, bool expected)
    {
        var model = new AudioModel { Id = "model", Family = family, Task = task, Mode = mode };

        Assert.Equal(expected, ModelSelectionPolicy.SupportsMeeting(model));
    }

    [Theory]
    [InlineData("nemotron_asr", true)]
    [InlineData("kroko_asr", true)]
    [InlineData("vibevoice_asr_streaming", false)]
    [InlineData("moonshine_asr", false)]
    [InlineData("nemotron_3_diar", false)]
    [InlineData(null, false)]
    public void OnlyNemotronAndKrokoRequireTheCompanion(string? family, bool expected)
    {
        Assert.Equal(expected, ModelSelectionPolicy.RequiresCompanion(new AudioModel { Id = "model", Family = family }));
    }

    [Fact]
    public void DiarizationCompanionMatchesOnlyTheExactStreamingDiarizer()
    {
        Assert.True(ModelSelectionPolicy.IsDiarizationCompanion(Companion));
        Assert.False(ModelSelectionPolicy.IsDiarizationCompanion(Companion with { Id = "other-diarizer" }));
        Assert.False(ModelSelectionPolicy.IsDiarizationCompanion(Companion with { Family = "nemotron_asr" }));
        Assert.False(ModelSelectionPolicy.IsDiarizationCompanion(Companion with { Task = "asr" }));
        Assert.False(ModelSelectionPolicy.IsDiarizationCompanion(Companion with { Mode = "offline" }));
    }

    [Fact]
    public void FindCompanionReturnsTheFirstMatchingDiarizer()
    {
        Assert.Same(Companion, ModelSelectionPolicy.FindCompanion([Nemotron, Companion with { Mode = "offline" }, Companion]));
        Assert.Null(ModelSelectionPolicy.FindCompanion([Nemotron, Companion with { Id = "other-diarizer" }]));
        Assert.Null(ModelSelectionPolicy.FindCompanion([]));
    }

    public static TheoryData<AudioModel?, string?, string?> ExplicitMeetingCases => new()
    {
        { Nemotron, Nemotron.Id, Nemotron.Id },
        { VibeVoice, VibeVoice.Id, VibeVoice.Id },
        { Nemotron, null, null },
        { Nemotron, Kroko.Id, null },
        { Moonshine, Moonshine.Id, null },
        { OfflineNemotron, OfflineNemotron.Id, null },
        { null, Nemotron.Id, null },
        { null, null, null },
    };

    [Theory]
    [MemberData(nameof(ExplicitMeetingCases))]
    public void ExplicitMeetingModelRequiresASavedMeetingCapableSelection(AudioModel? selected, string? saved, string? expected)
    {
        Assert.Equal(expected, ModelSelectionPolicy.ExplicitMeetingModelId(selected, saved));
    }

    [Fact]
    public void ExplicitMeetingModelIsTheOnlyCandidate()
    {
        var candidates = ModelSelectionPolicy.MeetingCandidates([VibeVoice, Kroko, Nemotron, Companion], Kroko.Id);

        Assert.Equal(new[] { Kroko }, candidates);
    }

    [Fact]
    public void ExplicitVibeVoiceDoesNotNeedTheCompanion()
    {
        Assert.Equal(new[] { VibeVoice }, ModelSelectionPolicy.MeetingCandidates([Nemotron, VibeVoice], VibeVoice.Id));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("moonshine-tiny")]
    [InlineData("nemotron-offline")]
    [InlineData(AudioModelDefaults.DiarizationModelId)]
    public void ExplicitMissingOrUnstreamableMeetingModelFailsWithoutSubstitution(string id)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ModelSelectionPolicy.MeetingCandidates([Moonshine, OfflineNemotron, Nemotron, VibeVoice, Companion], id));

        Assert.Equal($"The selected meeting model '{id}' is not installed or cannot stream meetings.", error.Message);
    }

    [Theory]
    [InlineData(AudioModelDefaults.RecognitionModelId)]
    [InlineData(AudioModelDefaults.KrokoModelId)]
    public void ExplicitPairedModelWithoutTheCompanionFails(string id)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ModelSelectionPolicy.MeetingCandidates([Nemotron, Kroko, VibeVoice], id));

        Assert.Equal($"Install Nemotron 3 Diarization Q8 before starting a meeting with '{id}'.", error.Message);
    }

    [Fact]
    public void UnconfiguredMeetingWithCompanionOrdersNemotronKrokoThenVibeVoice()
    {
        var candidates = ModelSelectionPolicy.MeetingCandidates([VibeVoice, Companion, Kroko, Moonshine, Nemotron], null);

        Assert.Equal(new[] { Nemotron, Kroko, VibeVoice }, candidates);
    }

    [Fact]
    public void UnconfiguredMeetingSkipsPairedModelsThatAreNotInstalled()
    {
        Assert.Equal(new[] { Kroko }, ModelSelectionPolicy.MeetingCandidates([Kroko, Companion], null));
    }

    [Fact]
    public void UnconfiguredMeetingWithoutCompanionFallsBackToVibeVoice()
    {
        Assert.Equal(new[] { VibeVoice }, ModelSelectionPolicy.MeetingCandidates([Nemotron, Kroko, VibeVoice], null));
    }

    [Fact]
    public void UnconfiguredMeetingIgnoresNonDefaultPairedModels()
    {
        var otherNemotron = Nemotron with { Id = "nemotron-other" };

        Assert.Equal(new[] { VibeVoice }, ModelSelectionPolicy.MeetingCandidates([otherNemotron, Companion, VibeVoice], null));
    }

    [Theory]
    [MemberData(nameof(NoMeetingModelCases))]
    public void UnconfiguredMeetingWithoutCandidatesFails(AudioModel[] installed)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModelSelectionPolicy.MeetingCandidates(installed, null));

        Assert.Equal("Install Nemotron or Kroko ASR with Nemotron 3 Diarization, or VibeVoice ASR Streaming 1.5B before starting a meeting.",
            error.Message);
    }

    public static TheoryData<AudioModel[]> NoMeetingModelCases => new()
    {
        Array.Empty<AudioModel>(),
        new[] { Nemotron, Kroko },
        new[] { Companion, Moonshine, OfflineNemotron },
    };
}
