using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class DiarizedMeetingSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"Ansible-diarized-{Guid.NewGuid():N}");
    private static readonly AudioModel Nemotron = new()
    {
        Id = "nemotron-asr-0.6b-q8", Family = "nemotron_asr", Task = "asr", Mode = "streaming"
    };
    private static readonly AudioModel Kroko = new()
    {
        Id = "kroko-en-64l-q8", Family = "kroko_asr", Task = "asr", Mode = "streaming", SupportsCustomDictionary = true
    };
    private static readonly AudioModel VibeVoice = new()
    {
        Id = "vibevoice", Family = "vibevoice_asr_streaming", Mode = "streaming", SupportsCustomDictionary = true
    };

    [Fact]
    public async Task NewMeetingPrefersNemotronRatherThanCatalogOrder()
    {
        var engine = new Engine([VibeVoice, Kroko, Nemotron]);
        await using var session = Create(engine, new());
        await session.StartAsync(new(MeetingPlaybackSource.SystemAudio));
        Assert.Equal(Nemotron, engine.Model);
        await session.EndAsync();
    }

    [Theory]
    [InlineData("kroko-en-64l-q8")]
    [InlineData("vibevoice")]
    public async Task ExplicitMeetingChoiceRetainsCustomVocabulary(string id)
    {
        var engine = new Engine([Nemotron, Kroko, VibeVoice]);
        await using var session = Create(engine, new(), () => id);
        await session.StartAsync(new(MeetingPlaybackSource.SystemAudio));
        Assert.Equal(id, engine.Model!.Id);
        Assert.Equal("Mother Nature", engine.Options!.CustomDictionary);
        await session.EndAsync();
    }

    [Fact]
    public async Task MissingExplicitChoiceDoesNotSilentlySwitchModelsOrOpenCapture()
    {
        var capture = new CaptureFactory();
        await using var session = Create(new([Nemotron]), capture, () => Kroko.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(new(MeetingPlaybackSource.SystemAudio)));
        Assert.False(capture.Started);
        Assert.False(session.IsActive);
    }

    [Fact]
    public async Task StructuredTurnsKeepNativeTimesAndReceiveLaterSpeakerCorrections()
    {
        var engine = new Engine([Nemotron]);
        var capture = new CaptureFactory();
        var store = new MeetingStore(_directory);
        await using var session = Create(engine, capture);
        await session.StartAsync(new(MeetingPlaybackSource.SystemAudio));
        capture.ReportActivity(new(0, 16000, 0, 0.2));
        await capture.Input.Writer.WriteAsync(new byte[32000]);
        await engine.Consumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        engine.Report(new("This display text is not a speaker protocol.", false, "Hello")
        {
            ProcessedSamples = 16000,
            SpeakerTurns = [new("Unknown speaker", 0.2, 0.8, "Hello", true) { TimingQuality = MeetingTimingQuality.Aligned }]
        });
        Assert.Equal("Unknown speaker", Assert.Single(session.Document!.Turns).SpeakerId);
        Assert.Equal(MeetingAudioSource.Playback, Assert.Single(session.Document.Turns).Source);
        engine.Report(new("Still not parsed.", false, "Hello")
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

    [Fact]
    public async Task TimedTurnOutsideConsumedAudioIsRejected()
    {
        var engine = new Engine([Nemotron]);
        await using var session = Create(engine, new());
        await session.StartAsync(new(MeetingPlaybackSource.SystemAudio));
        Assert.Throws<InvalidDataException>(() => engine.Report(new("", false, "Hello")
        {
            ProcessedSamples = 0,
            SpeakerTurns = [new("Speaker 0", 0, 1, "Hello")]
        }));
        await session.EndAsync();
    }

    private MeetingSession Create(Engine engine, CaptureFactory capture, Func<string?>? modelId = null) =>
        new(() => engine, capture, new(_directory), () => new(CustomDictionary: "Mother Nature", Backend: NativeBackend.Cpu),
            getModelId: modelId);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class Engine(IReadOnlyList<AudioModel> models) : IMeetingRecognitionEngine
    {
        private IProgress<TranscriptUpdate>? _progress;
        private TranscriptUpdate _last = new("", false, "") { SpeakerTurns = [] };
        public AudioModel? Model { get; private set; }
        public RecognitionOptions? Options { get; private set; }
        public TaskCompletionSource Consumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            await foreach (var packet in audio.ReadAllAsync(token)) Consumed.TrySetResult();
            progress.Report(_last with { IsFinal = true,
                SpeakerTurns = _last.SpeakerTurns?.Select(turn => turn with { IsProvisional = false }).ToArray() });
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
