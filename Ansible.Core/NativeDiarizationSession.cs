using System.Runtime.InteropServices;
using System.Text;

namespace Ansible.Core;

internal sealed class NativeDiarizationSession : IDisposable
{
    internal const string LatencyProfile = "low";
    private NativeAudioHandle? _model;
    private NativeAudioHandle? _session;
    private FileStream? _lease;
    private bool _started;
    private bool _finished;
    private long _processedSamples;
    private readonly List<SpeakerActivity> _activities = [];
    private readonly HashSet<SpeakerActivity> _seenActivities = [];
    private IReadOnlyList<MeetingTimedToken> _tokens = [];
    private readonly bool _wholeWords;

    public NativeDiarizationSession(nint registry, AudioModel model, NativeModelEntry entry,
        NativeModelIntegrity integrity, RecognitionOptions options, int threads, int headroom,
        string asrFamily, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _wholeWords = asrFamily switch
        {
            "kroko_asr" => true,
            "nemotron_asr" => false,
            _ => throw new NotSupportedException("Paired speaker attribution requires Nemotron or Kroko ASR.")
        };
        Backend = options.Backend;
        DeviceIndex = options.DeviceIndex;
        nint family = 0, backendName = 0, sessionOptions = 0;
        try
        {
            NativeMemory.CheckBudget(entry.Bytes, 0, headroom);
            _lease = integrity.OpenVerified(model.ModelPath!, entry, token);
            family = Marshal.StringToCoTaskMemUTF8(model.Family!);
            backendName = Marshal.StringToCoTaskMemUTF8(options.Backend == NativeBackend.Vulkan ? "vulkan" : "cpu");
            sessionOptions = NativeAudioApi.OptionsCreate();
            if (sessionOptions == 0) throw new InvalidOperationException("Cannot create diarization options.");
            var config = new NativeAudioApi.ModelConfig { Family = family };
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.ModelLoad(registry, model.ModelPath!, in config, 0, out var pointer),
                "load the Nemotron speaker diarizer");
            _model = new(pointer, NativeHandleKind.Model);
            token.ThrowIfCancellationRequested();
            if (NativeAudioApi.Supports(pointer, "diar", "streaming") == 0)
                throw new NotSupportedException("The installed Nemotron diarizer does not support streaming.");
            NativeAudioApi.Check(NativeAudioApi.OptionsSet(sessionOptions, "nemotron_3_diar.latency_profile", LatencyProfile),
                "set the live speaker latency profile");
            var backend = new NativeAudioApi.BackendConfig { Backend = backendName, Device = options.DeviceIndex, Threads = threads };
            NativeMemory.CheckBudget(entry.Bytes, 0, headroom);
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.SessionCreate(pointer, "diar", "streaming", in backend, sessionOptions, out var session),
                "create the live speaker session");
            _session = new(session, NativeHandleKind.Session);
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (sessionOptions != 0) NativeAudioApi.OptionsFree(sessionOptions);
            Marshal.FreeCoTaskMem(family);
            Marshal.FreeCoTaskMem(backendName);
        }
    }

    public NativeBackend Backend { get; }
    public int DeviceIndex { get; }
    public bool HasSpeakerActivity => _activities.Count > 0;

    public unsafe void Start(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _activities.Clear();
        _seenActivities.Clear();
        _tokens = [];
        _processedSamples = 0;
        _finished = false;
        using var request = new NativeAudioHandle(NativeAudioApi.RequestCreate(), NativeHandleKind.Request);
        NativeAudioApi.Check(NativeAudioApi.SetAudio(request.DangerousGetHandle(), null, 0, 16000, 1),
            "declare the diarization audio format");
        // A failed or cancelled start still requires the owner's reset/teardown path.
        _started = true;
        NativeAudioApi.Check(NativeAudioApi.StreamStart(_session!.DangerousGetHandle(), request.DangerousGetHandle()),
            "start live speaker diarization");
        token.ThrowIfCancellationRequested();
    }

    public unsafe void Push(float* samples, int frames, long offset, IProgress<TranscriptUpdate>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_started || _finished) throw new InvalidOperationException("Start speaker diarization before pushing audio.");
        if (samples == null || frames <= 0 || offset != _processedSamples ||
            frames > NemotronSpeakerAttribution.MaximumSamples - offset)
            throw new InvalidDataException("Speaker audio must be contiguous and within the two-hour meeting limit.");
        _processedSamples = offset + frames;
        var session = _session!.DangerousGetHandle();
        NativeAudioApi.Check(NativeAudioApi.StreamPush(session, samples, (nuint)frames, 16000, 1, offset, out var streamEvent),
            "process live speaker audio");
        var changed = AppendEvent(streamEvent, token);
        var events = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.StreamNextEvent(session, out streamEvent), "read live speaker events");
            if (streamEvent == 0) break;
            changed |= AppendEvent(streamEvent, token);
            if (++events > NemotronSpeakerAttribution.MaximumActivities)
                throw new InvalidDataException("The live diarizer returned too many queued events.");
        }
        if (changed && _tokens.Count > 0) Publish(false, progress, token);
    }

    private bool AppendEvent(nint pointer, CancellationToken token)
    {
        if (pointer == 0) return false;
        using var owned = new NativeAudioHandle(pointer, NativeHandleKind.Event);
        // Diarizer events contain new, closed intervals at cumulative sample positions, not snapshots.
        return AppendActivities(NativeAudioApi.EventResult(pointer), token);
    }

    private unsafe bool AppendActivities(nint result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var count = NativeAudioApi.SpeakerTurnCount(result);
        if (count > NemotronSpeakerAttribution.MaximumActivities)
            throw new InvalidDataException("The live diarizer returned too many speaker turns.");
        for (nuint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            long start = 0, end = 0;
            nint speaker = 0;
            NativeAudioApi.Check(NativeAudioApi.SpeakerTurn(result, index, (nint)(&start), (nint)(&end),
                (nint)(&speaker), 0, out _), "read timed speaker activity");
            var activity = new SpeakerActivity(ReadUtf8(speaker, 9), start, end);
            _ = NemotronSpeakerAttribution.GetSpeakerIndex(activity, _processedSamples);
            if (_seenActivities.Contains(activity)) continue;
            if (_activities.Count == NemotronSpeakerAttribution.MaximumActivities)
                throw new InvalidDataException("The live diarizer returned too many unique speaker turns.");
            _seenActivities.Add(activity);
            _activities.Add(activity);
        }
        return count > 0;
    }

    public void ReadAsr(nint result, long processedSamples, IProgress<TranscriptUpdate>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (processedSamples != _processedSamples)
            throw new InvalidDataException("Speech and speaker audio must share cumulative sample positions.");
        var count = NativeAudioApi.WordCount(result);
        if (count == 0) return;
        // The native timestamp patch publishes the entire ASR token history, including a revisable tail.
        ReadTokens(result, count, token);
        Publish(false, progress, token);
    }

    private void ReadTokens(nint result, nuint count, CancellationToken token)
    {
        if (count > NemotronSpeakerAttribution.MaximumTokens)
            throw new InvalidDataException("Meeting recognition returned too many token timestamps.");
        var tokens = new MeetingTimedToken[(int)count];
        var remaining = NemotronSpeakerAttribution.MaximumTextCharacters;
        for (nuint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.Word(result, index, out var text, out var start, out var end, 0),
                "read live speech timestamps");
            var piece = ReadUtf8(text, remaining);
            // Kroko emits space-separated whole words; Nemotron emits decoded subword pieces.
            if (_wholeWords && index > 0) piece = " " + piece;
            remaining -= piece.Length;
            if (remaining < 0) throw new InvalidDataException("Meeting speech exceeds the supported text limit.");
            tokens[(int)index] = new(piece, start, end);
        }
        _tokens = tokens;
    }

    private static unsafe string ReadUtf8(nint pointer, int maximumCharacters)
    {
        if (pointer == 0) throw new InvalidDataException("Native speaker metadata has no text.");
        var bytes = (byte*)pointer;
        var maximumBytes = checked(maximumCharacters * 4);
        var length = 0;
        while (length <= maximumBytes && bytes[length] != 0) length++;
        if (length > maximumBytes) throw new InvalidDataException("Native speaker metadata exceeds the text limit.");
        var text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(bytes, length));
        if (text.Length > maximumCharacters) throw new InvalidDataException("Native speaker metadata exceeds the text limit.");
        return text;
    }

    public void Finish(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NativeAudioApi.Check(NativeAudioApi.StreamFinish(_session!.DangerousGetHandle(), out var pointer),
            "finish live speaker diarization");
        using var owned = new NativeAudioHandle(pointer, NativeHandleKind.Result);
        _finished = true;
        // Finalization includes open turns and replaces, rather than extends, streamed activity.
        _activities.Clear();
        _seenActivities.Clear();
        AppendActivities(pointer, token);
    }

    public void PublishFinal(nint result, string speech, long samples, IProgress<TranscriptUpdate>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (samples != _processedSamples)
            throw new InvalidDataException("Speech and speaker audio must share cumulative sample positions.");
        ReadTokens(result, NativeAudioApi.WordCount(result), token);
        var update = NemotronSpeakerAttribution.Create(_tokens, _activities, samples, true, token);
        if (!string.Equals(update.SpeechText, speech.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException("Speech and timestamp text disagree. The meeting cannot assign speakers safely.");
        token.ThrowIfCancellationRequested();
        // The ASR final is authoritative, including its original outer whitespace.
        progress?.Report(update with { Text = speech, SpeechText = speech });
    }

    private void Publish(bool final, IProgress<TranscriptUpdate>? progress, CancellationToken token) =>
        progress?.Report(NemotronSpeakerAttribution.Create(_tokens, _activities, _processedSamples, final, token));

    public void Reset()
    {
        if (!_started) return;
        NativeAudioApi.Check(NativeAudioApi.StreamReset(_session!.DangerousGetHandle()), "reset live speaker diarization");
        _started = false;
        _finished = false;
        _processedSamples = 0;
        _tokens = [];
        _activities.Clear();
        _seenActivities.Clear();
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _model?.Dispose();
        _model = null;
        _lease?.Dispose();
        _lease = null;
        _started = false;
        _tokens = [];
        _activities.Clear();
        _seenActivities.Clear();
    }
}
