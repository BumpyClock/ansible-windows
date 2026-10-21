using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class DiarizedMeetingSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Ansible-diarized-" + Guid.NewGuid().ToString("N"));
    private static readonly MeetingCaptureRequest Request = new(MeetingPlaybackSource.SystemAudio);
    private static readonly AudioModel Nemotron = new()
    {
        Id = AudioModelDefaults.RecognitionModelId, Family = "nemotron_asr", Mode = "streaming"
    };
    private static readonly AudioModel Kroko = new()
    {
        Id = AudioModelDefaults.KrokoModelId, Family = "kroko_asr", Mode = "streaming", SupportsCustomDictionary = true
    };
    private static readonly AudioModel Diarizer = new()
    {
        Id = AudioModelDefaults.DiarizationModelId, Family = "nemotron_3_diar", Task = "diar", Mode = "streaming"
    };
    private static readonly AudioModel VibeVoice = new()
    {
        Id = "vibevoice-streaming-1.5b-q4", Family = "vibevoice_asr_streaming", Mode = "streaming"
    };

    [Fact]
    public async Task DefaultMeetingPrefersPairedNemotronRegardlessOfCatalogOrder()
    {
        var engine = new Engine([VibeVoice, Kroko, Diarizer, Nemotron]);
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        Assert.Equal(Nemotron, engine.Model);
        Assert.Empty(engine.Options!.CustomDictionary);
        await session.EndAsync();
    }

    [Fact]
    public async Task DefaultMeetingKeepsVibeVoiceWhenCompanionIsNotInstalled()
    {
        var engine = new Engine([Nemotron, VibeVoice]);
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        Assert.Equal(VibeVoice, engine.Model);
        await session.EndAsync();
    }

    [Theory]
    [InlineData("kroko-en-64l-q8")]
    [InlineData("vibevoice-streaming-1.5b-q4")]
    public async Task ExplicitMeetingChoiceSelectsRequestedModel(string id)
    {
        var engine = new Engine([Nemotron, Kroko, Diarizer, VibeVoice]);
        await using var session = Create(engine, new(), () => id);
        await session.StartAsync(Request);
        Assert.Equal(id, engine.Model!.Id);
        Assert.Equal(id == Kroko.Id ? "Mother Nature" : "", engine.Options!.CustomDictionary);
        await session.EndAsync();
    }

    [Fact]
    public async Task MissingExplicitModelDoesNotOpenCaptureOrSilentlySwitch()
    {
        var capture = new CaptureFactory();
        await using var session = Create(new([Nemotron, Diarizer]), capture, () => Kroko.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Request));
        Assert.False(capture.Started);
        Assert.False(session.IsActive);
    }

    [Fact]
    public async Task NativeSpeakerCorrectionsKeepTimesAndUpdateAudioSource()
    {
        var engine = new Engine([Nemotron, Diarizer]);
        var capture = new CaptureFactory();
        var store = new MeetingStore(_directory);
        await using var session = Create(engine, capture);
        await session.StartAsync(Request);
        capture.ReportActivity(new(0, 16000, 0, 0.2));
        await capture.Input.Writer.WriteAsync(new byte[32000]);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (engine.ReadBytes < 32000) await Task.Delay(5, timeout.Token);
        engine.Report(new("Display text is not a speaker protocol.", false, "Hello")
        {
            ProcessedSamples = 16000,
            SpeakerTurns = [new("Unknown speaker", 0.2, 0.8, "Hello", true) { TimingQuality = MeetingTimingQuality.Aligned }]
        });
        Assert.Equal("Unknown speaker", Assert.Single(session.Document!.Turns).SpeakerId);
        engine.Report(new("Still not a speaker protocol.", false, "Hello")
        {
            ProcessedSamples = 16000,
            SpeakerTurns = [new("Speaker 2", 0.2, 0.8, "Hello", true) { TimingQuality = MeetingTimingQuality.Aligned }]
        });
        var turn = Assert.Single(session.Document!.Turns);
        Assert.Equal("Speaker 2", turn.SpeakerId);
        Assert.Equal(MeetingAudioSource.Playback, turn.Source);
        Assert.Equal(0.2, turn.StartSeconds);
        Assert.Equal(0.8, turn.EndSeconds);
        await session.EndAsync();
        var saved = Assert.Single((await store.LoadAsync(session.Document.Id)).Turns);
        Assert.Equal(turn with { IsProvisional = false }, saved);
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(0, 0, true)]
    [InlineData(0.8, 0.2, false)]
    [InlineData(double.NaN, 0.2, false)]
    public async Task InvalidTimedTurnIsRejected(double start, double end, bool final)
    {
        var engine = new Engine([Nemotron, Diarizer]);
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        Assert.Throws<InvalidDataException>(() => engine.Report(new("Hello", final, "Hello")
        {
            ProcessedSamples = 0,
            SpeakerTurns = [new("Speaker 0", start, end, "Hello", final) { TimingQuality = MeetingTimingQuality.Aligned }]
        }));
        Assert.Null(session.Document!.Turns.SingleOrDefault());
        await session.EndAsync();
    }

    private MeetingSession Create(Engine engine, CaptureFactory capture, Func<string?>? modelId = null) =>
        new(() => engine, capture, new(_directory),
            () => new RecognitionOptions(CustomDictionary: "Mother Nature", Backend: NativeBackend.Cpu), getModelId: modelId);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Engine(IReadOnlyList<AudioModel> models) : IMeetingRecognitionEngine
    {
        private IProgress<TranscriptUpdate>? _progress;
        private TranscriptUpdate _last = new("", false, "") { SpeakerTurns = [] };
        public AudioModel? Model { get; private set; }
        public RecognitionOptions? Options { get; private set; }
        private long _readBytes;
        public long ReadBytes => Interlocked.Read(ref _readBytes);
        public Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken token) => Task.FromResult(models);
        public Task PrepareMeetingAsync(AudioModel model, RecognitionOptions options, CancellationToken token)
        {
            Model = model;
            Options = options;
            return Task.CompletedTask;
        }
        public async Task StreamMeetingAsync(AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
            IProgress<TranscriptUpdate> progress, CancellationToken token, Action? onReady = null)
        {
            _progress = progress;
            onReady?.Invoke();
            await foreach (var packet in audio.ReadAllAsync(token)) Interlocked.Add(ref _readBytes, packet.Length);
            progress.Report(_last with
            {
                IsFinal = true,
                SpeakerTurns = _last.SpeakerTurns?.Select(turn => turn with { IsProvisional = false }).ToArray()
            });
        }
        public void Report(TranscriptUpdate update)
        {
            _progress!.Report(update);
            _last = update;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CaptureFactory : IMeetingCaptureFactory
    {
        private Capture? _capture;
        public Channel<byte[]> Input { get; } = Channel.CreateUnbounded<byte[]>();
        public bool Started { get; private set; }
        public void ReportActivity(MeetingAudioActivity activity) => _capture!.ReportActivity(activity);
        public Task<IAudioCapture> StartAsync(MeetingCaptureRequest request, CancellationToken token)
        {
            Started = true;
            _capture = new(Input);
            return Task.FromResult<IAudioCapture>(_capture);
        }
    }

    private sealed class Capture(Channel<byte[]> input) : IAudioCapture, IMeetingAudioSourceMetadata
    {
        public ChannelReader<byte[]> Audio => input.Reader;
        public long ClockSamplePosition => 0;
        public double CapturedSeconds => 0;
        public bool IsReleased { get; private set; }
        public event Action<MeetingAudioActivity>? Activity;
        public void ReportActivity(MeetingAudioActivity activity) => Activity?.Invoke(activity);
        public event Action<double>? LevelChanged { add { } remove { } }
        public Task StopAsync() { input.Writer.TryComplete(); IsReleased = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsReleased = true; return ValueTask.CompletedTask; }
    }
}
