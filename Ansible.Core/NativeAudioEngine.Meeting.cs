using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Ansible.Core;

public sealed partial class NativeAudioEngine
{
    public async Task StreamMeetingAsync(AudioModel model, ChannelReader<MeetingAudioPacket> audio, RecognitionOptions options,
        IProgress<TranscriptUpdate> progress, CancellationToken cancellationToken, Action? onReady = null)
    {
        RequireMeetingModel(model);
        options = ValidateOptions(model, options);
        var outcome = await RunExclusiveAsync(() => RunDualMeeting(model, audio, options, progress, cancellationToken, onReady), cancellationToken);
        progress.Report(outcome);
    }

    private TranscriptUpdate RunDualMeeting(AudioModel model, ChannelReader<MeetingAudioPacket> audio, RecognitionOptions requested,
        IProgress<TranscriptUpdate> progress, CancellationToken token, Action? onReady)
    {
        _streamConsumed = false;
        return RunWithDevice(model, "streaming", 4096, NativeMemory.MaximumStreamFrames * sizeof(float), requested, token,
            options => NativeOperation.Run(() => RunDualMeetingCore(model, audio, options, progress, token, onReady), CleanupStream),
            () => _streamConsumed);
    }

    private TranscriptUpdate RunDualMeetingCore(AudioModel model, ChannelReader<MeetingAudioPacket> audio, RecognitionOptions options,
        IProgress<TranscriptUpdate> progress, CancellationToken token, Action? onReady)
    {
        if (ModelSelectionPolicy.RequiresCompanion(model)) EnsurePairedSession(model, options, token);
        else EnsureSession(model, "streaming", NativeMemory.MaximumStreamFrames * sizeof(float), options, token, 4096);
        var playbackSession = _session!.DangerousGetHandle();
        var diarization = _diarization;
        using var microphoneOwner = CreateAdditionalStreamingSession(model, options, token, 4096);
        var microphoneSession = microphoneOwner.DangerousGetHandle();
        using var playbackRequest = CreateRequest(options, token);
        using var microphoneRequest = CreateRequest(options, token);
        SetStreamingAudioContract(playbackRequest);
        SetStreamingAudioContract(microphoneRequest);
        var preferred = StreamingPacketFrames(playbackSession, token);
        var playbackBuffer = new byte[preferred * 2];
        var microphoneBuffer = new byte[preferred * 2];
        var used = 0;
        long offset = 0;
        var playbackTranscript = new StreamingTranscript();
        var microphoneTranscript = new StreamingTranscript();
        var playbackFallback = new MeetingTranscript();
        var microphoneFallback = new CumulativeMeetingChunks();
        TranscriptUpdate? latestPlayback = null, latestMicrophone = null;
        var pushing = false;
        void Publish()
        {
            var playback = latestPlayback is null ? [] : TagPlayback(latestPlayback).SpeakerTurns ?? [];
            var microphone = latestMicrophone is null ? [] : TagMicrophone(latestMicrophone).SpeakerTurns ?? [];
            var turns = playback.Concat(microphone).Where(turn => turn.EndSeconds * 16000 <= offset)
                .OrderBy(turn => turn.StartSeconds).ThenBy(turn => turn.Source).ToArray();
            var speech = MergeSpeech(turns);
            progress.Report(new(speech, false, speech) { ProcessedSamples = offset, SpeakerTurns = turns });
        }
        var playbackProgress = new InlineProgress(update =>
        {
            latestPlayback = diarization is null ? ToPlaybackUpdate(update, playbackFallback) : update;
            if (!pushing) Publish();
        });
        var microphoneProgress = new InlineProgress(update =>
        {
            latestMicrophone = ToMicrophoneUpdate(update, microphoneFallback);
            if (!pushing) Publish();
        });
        diarization?.Start(token);
        _streamStartAttempted = true;
        NativeOperation.Step(token, () => NativeAudioApi.Check(
            NativeAudioApi.StreamStart(playbackSession, playbackRequest.DangerousGetHandle()), "start playback meeting recognition"));
        NativeOperation.Step(token, () => NativeAudioApi.Check(
            NativeAudioApi.StreamStart(microphoneSession, microphoneRequest.DangerousGetHandle()), "start microphone meeting recognition"));
        onReady?.Invoke();
        while (audio.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
            while (audio.TryRead(out var packet))
            {
                var samples = packet.Samples;
                _streamConsumed = true;
                if (offset + samples > 16000L * 120 * 60) throw new InvalidDataException("Meeting audio exceeds the two-hour limit.");
                var index = 0;
                while (index < packet.Playback.Length)
                {
                    var count = Math.Min(playbackBuffer.Length - used, packet.Playback.Length - index);
                    packet.Playback.AsSpan(index, count).CopyTo(playbackBuffer.AsSpan(used));
                    packet.Microphone.AsSpan(index, count).CopyTo(microphoneBuffer.AsSpan(used));
                    used += count;
                    index += count;
                    if (used == playbackBuffer.Length) { PushBoth(used); used = 0; }
                }
            }
        if (used > 0) PushBoth(used);
        token.ThrowIfCancellationRequested();
        diarization?.Finish(token);
        token.ThrowIfCancellationRequested();
        NativeAudioApi.Check(NativeAudioApi.StreamFinish(playbackSession, out var playbackResult), "finish playback meeting recognition");
        using var playbackOwned = new NativeAudioHandle(playbackResult, NativeHandleKind.Result);
        token.ThrowIfCancellationRequested();
        NativeAudioApi.Check(NativeAudioApi.StreamFinish(microphoneSession, out var microphoneResult), "finish microphone meeting recognition");
        using var microphoneOwned = new NativeAudioHandle(microphoneResult, NativeHandleKind.Result);
        token.ThrowIfCancellationRequested();
        var playbackRecognition = ReadResult(playbackResult, model.Family!, token) with { ProcessedSamples = offset };
        var remote = diarization is null
            ? ToPlaybackUpdate(playbackRecognition, playbackFallback, true)
            : diarization.CreateFinal(playbackResult, playbackRecognition.SpeechText
                ?? throw new InvalidDataException("The ASR model returned no authoritative meeting speech."), offset, token);
        var microphoneSpeech = ReadResult(microphoneResult, model.Family!, token) with { ProcessedSamples = offset };
        var local = ToMicrophoneUpdate(microphoneSpeech, ReadSpeechWordTimings(microphoneResult, model.Family!, microphoneSpeech.SpeechText,
            offset, token, meeting: true), microphoneFallback, true);
        var finalTurns = TagPlayback(remote).SpeakerTurns!.Concat(TagMicrophone(local).SpeakerTurns!)
            .OrderBy(turn => turn.StartSeconds).ThenBy(turn => turn.Source).ToArray();
        var finalSpeech = MergeSpeech(finalTurns);
        return new(finalSpeech, true, finalSpeech) { ProcessedSamples = offset, SpeakerTurns = finalTurns };

        void PushBoth(int bytes)
        {
            if (offset + bytes / 2 > 16000L * 120 * 60)
                throw new InvalidDataException("Meeting audio exceeds the two-hour limit.");
            pushing = true;
            try
            {
                Push(playbackSession, playbackBuffer.AsSpan(0, bytes), offset, model.Family!, playbackTranscript, playbackProgress, token, diarization);
                Push(microphoneSession, microphoneBuffer.AsSpan(0, bytes), offset, model.Family!, microphoneTranscript, microphoneProgress, token,
                    meeting: true);
            }
            finally { pushing = false; }
            offset += bytes / 2;
            Publish();
        }
    }

    private NativeAudioHandle CreateAdditionalStreamingSession(AudioModel model, RecognitionOptions options, CancellationToken token, int historySteps)
    {
        token.ThrowIfCancellationRequested();
        var entry = _inventory!.Entries[model.Id];
        var backendName = Marshal.StringToCoTaskMemUTF8(options.Backend == NativeBackend.Vulkan ? "vulkan" : "cpu");
        try
        {
            var backend = new NativeAudioApi.BackendConfig { Backend = backendName, Device = options.DeviceIndex, Threads = _threads };
            var sessionOptions = model.Family == "vibevoice_asr_streaming" ? NativeAudioApi.OptionsCreate() : 0;
            try
            {
                if (model.Family == "vibevoice_asr_streaming" && sessionOptions == 0)
                    throw new InvalidOperationException("Cannot create native microphone meeting options.");
                if (sessionOptions != 0)
                    NativeAudioApi.Check(NativeAudioApi.OptionsSet(sessionOptions, "vibevoice_asr_streaming.max_history_steps",
                        historySteps.ToString(System.Globalization.CultureInfo.InvariantCulture)), "bound microphone meeting recognition history");
                // The playback session and diarizer are already resident. Admit only the
                // additional session's execution weights and workspace against free memory.
                NativeMemory.CheckBudget(entry.Bytes, NativeMemory.MaximumStreamFrames * sizeof(float), _headroom);
                token.ThrowIfCancellationRequested();
                NativeAudioApi.Check(NativeAudioApi.SessionCreate(_model!.DangerousGetHandle(), "asr", "streaming", in backend,
                    sessionOptions, out var session), "create the microphone meeting recognition session");
                var owned = new NativeAudioHandle(session, NativeHandleKind.Session);
                try { token.ThrowIfCancellationRequested(); return owned; }
                catch { owned.Dispose(); throw; }
            }
            finally { if (sessionOptions != 0) NativeAudioApi.OptionsFree(sessionOptions); }
        }
        finally { Marshal.FreeCoTaskMem(backendName); }
    }

    private static int StreamingPacketFrames(nint session, CancellationToken token)
    {
        long frames = 0; double seconds = 0;
        NativeOperation.Step(token, () => NativeAudioApi.Check(NativeAudioApi.StreamPolicy(session, 0, 0, out frames, out seconds), "read the streaming cadence"));
        var preferred = seconds > 0 ? checked((int)Math.Round(seconds * 16000)) : checked((int)frames);
        if (preferred <= 0 || preferred > 16000 * 30) throw new InvalidDataException("The model requests an unsupported streaming chunk size.");
        return preferred;
    }

    private static TranscriptUpdate TagPlayback(TranscriptUpdate update) => update.SpeakerTurns is null ? update : update with
    { SpeakerTurns = update.SpeakerTurns.Select(turn => turn with { Source = MeetingAudioSource.Playback }).ToArray() };

    private static TranscriptUpdate TagMicrophone(TranscriptUpdate update) => update.SpeakerTurns is null ? update : update with
    { SpeakerTurns = update.SpeakerTurns.Select(turn => turn with { SpeakerId = "You", Source = MeetingAudioSource.Microphone }).ToArray() };

    private static TranscriptUpdate ToPlaybackUpdate(RecognitionResult result, MeetingTranscript transcript, bool final) =>
        ToPlaybackUpdate(new(result.DisplayText, final, result.SpeechText) { ProcessedSamples = result.ProcessedSamples }, transcript);

    private static TranscriptUpdate ToPlaybackUpdate(TranscriptUpdate update, MeetingTranscript transcript)
    {
        var turns = string.IsNullOrWhiteSpace(update.SpeechText) ? [] : transcript.Update(update.Text,
            (update.ProcessedSamples ?? 0) / 16000d, update.IsFinal).Select(turn => turn with
            { Source = MeetingAudioSource.Playback }).ToArray();
        ValidateFinalSpeech(update, turns);
        return update with { SpeakerTurns = turns };
    }

    private static TranscriptUpdate ToMicrophoneUpdate(TranscriptUpdate update, CumulativeMeetingChunks fallback)
    {
        if (string.IsNullOrWhiteSpace(update.SpeechText)) return update with { SpeakerTurns = [] };
        var turns = update.WordTimings is { Words.Count: > 0 }
            ? BuildMicrophoneTurns(update.SpeechText, update.WordTimings.Words, update.IsFinal)
            : fallback.Update(update.SpeechText, update.ProcessedSamples ?? 0, update.IsFinal);
        ValidateFinalSpeech(update, turns);
        return update with { SpeakerTurns = turns };
    }

    private static TranscriptUpdate ToMicrophoneUpdate(RecognitionResult result, SpeechWordTimings? words,
        CumulativeMeetingChunks fallback, bool final) => ToMicrophoneUpdate(new(result.DisplayText, final, result.SpeechText)
        { ProcessedSamples = result.ProcessedSamples, WordTimings = words }, fallback);

    internal static MeetingTurn[] BuildMicrophoneTurns(string speech, IReadOnlyList<SpeechWordTiming> words, bool final)
    {
        var turns = new List<MeetingTurn>();
        for (var first = 0; first < words.Count;)
        {
            var last = first;
            while (last + 1 < words.Count && words[last + 1].StartSample - words[first].StartSample <= 10 * 16000 &&
                words[last + 1].StartSample - words[last].EndSample <= 16000)
                last++;
            var start = words[first];
            var end = words[last];
            var next = last + 1;
            var textStart = first == 0 ? 0 : start.TextStart;
            var textEnd = next < words.Count ? words[next].TextStart : speech.Length;
            if (textStart < 0 || textEnd > speech.Length || start.StartSample < 0 || end.EndSample < start.StartSample)
                throw new InvalidDataException("Microphone word timings are outside the recognized speech.");
            var text = speech[textStart..textEnd].Trim();
            if (!string.IsNullOrWhiteSpace(text))
                turns.Add(new MeetingTurn("You", start.StartSample / 16000d, end.EndSample / 16000d, text, !final && last == words.Count - 1)
                    { TimingQuality = MeetingTimingQuality.Aligned, Source = MeetingAudioSource.Microphone });
            first = next;
        }
        return [.. turns];
    }

    private static string MergeSpeech(IEnumerable<MeetingTurn> turns) => string.Join(" ", turns.Select(turn => turn.Text));

    private static void ValidateFinalSpeech(TranscriptUpdate update, IReadOnlyList<MeetingTurn> turns)
    {
        if (!update.IsFinal || string.IsNullOrWhiteSpace(update.SpeechText)) return;
        if (!string.Equals(NormalizeSpeech(MergeSpeech(turns)), NormalizeSpeech(update.SpeechText), StringComparison.Ordinal))
            throw new InvalidDataException("Meeting source turns disagree with authoritative recognized speech.");
    }

    private static string NormalizeSpeech(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class InlineProgress(Action<TranscriptUpdate> report) : IProgress<TranscriptUpdate>
    {
        public void Report(TranscriptUpdate value) => report(value);
    }

    private static void RequireMeetingModel(AudioModel model)
    {
        if (!ModelSelectionPolicy.SupportsMeeting(model))
            throw new NotSupportedException("Meetings require streaming Nemotron or Kroko ASR with its diarization companion, or VibeVoice streaming.");
    }

    private void EnsurePairedSession(AudioModel model, RecognitionOptions options, CancellationToken token)
    {
        if (_registry is null || _inventory is null)
            throw new InvalidOperationException("Connect the native backend and choose an installed model first.");
        if (!_inventory.Models.Contains(model) || model.ModelPath is null)
            throw new InvalidDataException("Meeting recognition requires an unchanged model selection from the connected catalog.");
        var companion = FindDiarizationModel();
        var key = new SessionKey(model.Id, "streaming", options.Backend, options.DeviceIndex, 4096);
        if (_sessionKey != key || _diarization is null || _diarization.Backend != options.Backend ||
            _diarization.DeviceIndex != options.DeviceIndex)
        {
            ReleaseModel();
            NativeMemory.CheckBudget(checked(_inventory.Entries[model.Id].Bytes + _inventory.Entries[companion.Id].Bytes),
                NativeMemory.MaximumStreamFrames * sizeof(float), _headroom);
        }
        EnsureSession(model, "streaming", NativeMemory.MaximumStreamFrames * sizeof(float), options, token, 4096);
        _diarization ??= new NativeDiarizationSession(_registry.DangerousGetHandle(), companion,
            _inventory.Entries[companion.Id], _integrity, options, _threads, _headroom, model.Family!, token);
    }

    private AudioModel FindDiarizationModel() =>
        (_inventory is null ? null : ModelSelectionPolicy.FindCompanion(_inventory.Models))
            ?? throw new InvalidOperationException("Install Nemotron 3 Diarization Q8 before starting a Nemotron or Kroko meeting.");

}
