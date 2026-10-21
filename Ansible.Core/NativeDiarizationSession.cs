using System.Runtime.InteropServices;

namespace Ansible.Core;

internal sealed class NativeDiarizationSession : IDisposable
{
    internal const string LatencyProfile = "low";
    private NativeAudioHandle? _model;
    private NativeAudioHandle? _session;
    private FileStream? _lease;
    private bool _started;
    private readonly List<SpeakerActivity> _activities = [];
    private IReadOnlyList<MeetingTimedToken> _tokens = [];
    private readonly bool _wholeWords;

    public NativeDiarizationSession(nint registry, AudioModel model, NativeModelEntry entry,
        NativeModelIntegrity integrity, RecognitionOptions options, int threads, int headroom, string asrFamily, CancellationToken token)
    {
        _wholeWords = asrFamily == "kroko_asr";
        Backend = options.Backend;
        DeviceIndex = options.DeviceIndex;
        NativeMemory.CheckBudget(entry.Bytes, 0, headroom);
        _lease = integrity.OpenVerified(model.ModelPath!, entry, token);
        var family = Marshal.StringToCoTaskMemUTF8(model.Family!);
        var backendName = Marshal.StringToCoTaskMemUTF8(options.Backend == NativeBackend.Vulkan ? "vulkan" : "cpu");
        var sessionOptions = NativeAudioApi.OptionsCreate();
        try
        {
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
        _tokens = [];
        using var request = new NativeAudioHandle(NativeAudioApi.RequestCreate(), NativeHandleKind.Request);
        NativeAudioApi.Check(NativeAudioApi.SetAudio(request.DangerousGetHandle(), null, 0, 16000, 1),
            "declare the diarization audio format");
        _started = true;
        NativeAudioApi.Check(NativeAudioApi.StreamStart(_session!.DangerousGetHandle(), request.DangerousGetHandle()),
            "start live speaker diarization");
        token.ThrowIfCancellationRequested();
    }

    public unsafe void Push(float* samples, int frames, long offset, IProgress<TranscriptUpdate>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = _session!.DangerousGetHandle();
        NativeAudioApi.Check(NativeAudioApi.StreamPush(session, samples, (nuint)frames, 16000, 1, offset, out var streamEvent),
            "process live speaker audio");
        var changed = AppendEvent(streamEvent, token);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.StreamNextEvent(session, out streamEvent), "read live speaker events");
            if (streamEvent == 0) break;
            changed |= AppendEvent(streamEvent, token);
        }
        if (changed && _tokens.Count > 0) Publish(offset + frames, false, progress, token);
    }

    private bool AppendEvent(nint pointer, CancellationToken token)
    {
        if (pointer == 0) return false;
        using var owned = new NativeAudioHandle(pointer, NativeHandleKind.Event);
        var result = NativeAudioApi.EventResult(pointer);
        return AppendActivities(result, token);
    }

    private unsafe bool AppendActivities(nint result, CancellationToken token)
    {
        var count = NativeAudioApi.SpeakerTurnCount(result);
        if (count > 100000 || (ulong)_activities.Count + count > 100000)
            throw new InvalidDataException("The live diarizer returned too many speaker turns.");
        for (nuint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            long start = 0, end = 0;
            nint speaker = 0;
            NativeAudioApi.Check(NativeAudioApi.SpeakerTurn(result, index, (nint)(&start), (nint)(&end),
                (nint)(&speaker), 0, out _), "read timed speaker activity");
            _activities.Add(new(Marshal.PtrToStringUTF8(speaker)
                ?? throw new InvalidDataException("A diarization turn has no speaker ID."), start, end));
        }
        return count > 0;
    }

    public void ReadAsr(nint result, long processedSamples, IProgress<TranscriptUpdate>? progress, CancellationToken token)
    {
        var count = NativeAudioApi.WordCount(result);
        if (count == 0) return;
        ReadTokens(result, count, token);
        Publish(processedSamples, false, progress, token);
    }

    private void ReadTokens(nint result, nuint count, CancellationToken token)
    {
        if (count > 262144) throw new InvalidDataException("Meeting recognition returned too many token timestamps.");
        var tokens = new MeetingTimedToken[(int)count];
        for (nuint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.Word(result, index, out var text, out var start, out var end, 0),
                "read live speech timestamps");
            var piece = Marshal.PtrToStringUTF8(text)
                ?? throw new InvalidDataException("A live speech timestamp has no text.");
            // Kroko emits whole words without spaces; Nemotron emits decoded subword pieces.
            tokens[(int)index] = new(_wholeWords && index > 0 ? " " + piece : piece, start, end);
        }
        _tokens = tokens;
    }

    public void Finish(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NativeAudioApi.Check(NativeAudioApi.StreamFinish(_session!.DangerousGetHandle(), out var pointer),
            "finish live speaker diarization");
        using var owned = new NativeAudioHandle(pointer, NativeHandleKind.Result);
        _activities.Clear();
        AppendActivities(pointer, token);
    }

    public void PublishFinal(nint result, string speech, long samples, IProgress<TranscriptUpdate>? progress, CancellationToken token)
    {
        ReadTokens(result, NativeAudioApi.WordCount(result), token);
        if (!string.Equals(string.Concat(_tokens.Select(value => value.Text)).Trim(), speech.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException("Speech and timestamp text disagree. The meeting cannot assign speakers safely.");
        Publish(samples, true, progress, token);
    }

    private void Publish(long samples, bool final, IProgress<TranscriptUpdate>? progress, CancellationToken token)
    {
        var update = NemotronSpeakerAttribution.Create(_tokens, _activities, samples, final, token);
        progress?.Report(update);
    }

    public void Reset()
    {
        if (!_started) return;
        NativeAudioApi.Check(NativeAudioApi.StreamReset(_session!.DangerousGetHandle()), "reset live speaker diarization");
        _started = false;
        _tokens = [];
        _activities.Clear();
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _model?.Dispose();
        _model = null;
        _lease?.Dispose();
        _lease = null;
        _tokens = [];
        _activities.Clear();
    }
}
