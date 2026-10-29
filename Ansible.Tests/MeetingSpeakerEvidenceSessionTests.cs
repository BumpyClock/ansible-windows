using System.Text.Json;
using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingSpeakerEvidenceSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Ansible-meeting-evidence-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TimingRefinementUsesStreamCoordinatesAfterAudioRingRollsOver()
    {
        var engine = new Engine();
        var captures = CreateCaptures();
        var aligner = new FakeWordAligner
        {
            Align = turns => turns.Select(turn => turn with
            {
                StartSeconds = turn.StartSeconds + 1,
                EndSeconds = turn.StartSeconds + 3,
                TimingQuality = MeetingTimingQuality.Aligned
            }).ToArray()
        };
        await using var session = Create(engine, captures, aligner: aligner);
        await session.StartAsync(new());
        await SendAsync(captures.Captures[0], engine, 20, 20);
        engine.Report("Speaker 0: First");
        await SendAsync(captures.Captures[0], engine, 5, 25);
        engine.Report("Speaker 0: First Speaker 1: Recent");
        Assert.Equal(1, session.Document!.Turns[0].StartSeconds);
        await SendAsync(captures.Captures[0], engine, 20, 45);
        engine.Report("Speaker 0: First Speaker 1: Recent Speaker 2: Pending");

        var candidate = Assert.Single(aligner.Inputs[1]);
        Assert.Equal("Recent", candidate.Text);
        Assert.Equal(5, candidate.StartSeconds);
        Assert.Equal(10, candidate.EndSeconds);
        Assert.Equal(30 * 16000, aligner.SampleCounts[1]);
        var refined = session.Document.Turns[1];
        Assert.Equal(21, refined.StartSeconds);
        Assert.Equal(23, refined.EndSeconds);
        Assert.Equal(MeetingTimingQuality.Aligned, refined.TimingQuality);
        Assert.Equal(1, session.Document.Turns[0].StartSeconds);
        await session.EndAsync();
    }

    [Fact]
    public async Task PauseResumeJoinsObserverAndRebasesItsClockOnTheNewCapture()
    {
        var engine = new Engine();
        var captures = CreateCaptures();
        var observer = new FakeSpeakerObserver();
        var observerCreations = 0;
        await using var session = Create(engine, captures, observerFactory: () => { observerCreations++; return observer; });
        await session.StartAsync(new(42));
        await SendAsync(captures.Captures[0], engine, 2, 2);
        captures.Captures[0].ClockSamplePosition = 3 * 16000;
        Assert.Equal(3 * 16000, observer.Position!());
        captures.Captures[0].Stopping = () => Assert.False(observer.Running);
        await session.PauseAsync();
        Assert.Equal(1, observer.Stops);

        await session.ResumeAsync();
        Assert.Equal(1, observerCreations);
        Assert.Equal(2, observer.Starts);
        Assert.Equal(2 * 16000, observer.Position!());
        captures.Captures[1].ClockSamplePosition = 16000;
        Assert.Equal(3 * 16000, observer.Position!());
        captures.Captures[1].Stopping = () => Assert.False(observer.Running);
        await session.EndAsync();
        Assert.True(observer.Disposed);
    }

    [Fact]
    public async Task StopBracketsConflictingTailEvidenceBeforeFinalSpeakerRefinement()
    {
        var engine = new Engine();
        var captures = CreateCaptures();
        var observer = new FakeSpeakerObserver();
        await using var session = Create(engine, captures, observerFactory: () => observer);
        await session.StartAsync(new(42));
        await SendAsync(captures.Captures[0], engine, 9, 9);
        for (var second = 0; second <= 7; second++) observer.Report(second * 16000, "Alex");
        observer.Report(8 * 16000, "Blair");
        engine.ReportTimed([
            Turn(0, 1, "First"),
            Turn(2, 3, "Second"),
            Turn(7, 9, "Conflicting tail")
        ]);
        Assert.Equal("Alex", session.Document!.GetSpeakerName("Speaker 0"));
        captures.Captures[0].Stopping = () =>
        {
            Assert.False(observer.Running);
            captures.Captures[0].ClockSamplePosition = 100 * 16000;
        };

        await session.EndAsync();

        Assert.Empty(session.Document!.AutoSpeakerNames);
        Assert.Equal("Speaker 0", session.Document.GetSpeakerName("Speaker 0"));
        Assert.Equal(MeetingSessionPhase.Completed, session.State.Phase);
    }

    [Fact]
    public async Task ObserverStopFailureDoesNotBlockPauseReleaseOrResume()
    {
        var engine = new Engine();
        var captures = CreateCaptures();
        var observer = new FakeSpeakerObserver { StopFailure = new InvalidOperationException("worker faulted") };
        await using var session = Create(engine, captures, observerFactory: () => observer);
        await session.StartAsync(new(42)).WaitAsync(TimeSpan.FromSeconds(5));
        var firstCapture = captures.Captures[0];
        await SendAsync(firstCapture, engine, 1, 1);

        await session.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MeetingSessionPhase.Paused, session.State.Phase);
        Assert.True(firstCapture.IsReleased);
        observer.StopFailure = null;
        await session.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeetingSessionPhase.Recording, session.State.Phase);
        Assert.Equal(2, observer.Starts);
        await session.EndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeetingSessionPhase.Completed, session.State.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidenceAvailabilityIsLoggedAsMeetingDiagnostics(bool observerStartFails)
    {
        var observer = new FakeSpeakerObserver
        {
            StartFailure = observerStartFails ? new InvalidOperationException("Teams not observable") : null
        };
        await using var log = new LocalDiagnosticLog(_directory);
        log.SetEnabled(true);
        await using var session = Create(new Engine(), CreateCaptures(), observerFactory: () => observer, diagnostics: log);
        await session.StartAsync(new(42)).WaitAsync(TimeSpan.FromSeconds(5));
        await session.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.EndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeetingSessionPhase.Completed, session.State.Phase);
        await log.DisposeAsync();

        var entries = new List<JsonElement>();
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("Family").GetString() == "Meeting")
                entries.Add(json.RootElement.Clone());
        }
        var refinement = Assert.Single(entries, entry => entry.GetProperty("Kind").GetString() == "RefinementAvailability");
        Assert.False(refinement.GetProperty("Available").GetBoolean());
        var naming = entries.Where(entry => entry.GetProperty("Kind").GetString() == "NamingAvailability").ToArray();
        Assert.Equal(2, naming.Length);
        Assert.All(naming, entry => Assert.Equal(!observerStartFails, entry.GetProperty("Available").GetBoolean()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static MeetingTurn Turn(double start, double end, string text) =>
        new("Speaker 0", start, end, text, false) { TimingQuality = MeetingTimingQuality.Aligned };

    private static FakeMeetingCaptureFactory CreateCaptures() => new() { Create = () => new() { PlaybackRms = 0.2 } };

    private static MeetingSession Create(Engine engine, FakeMeetingCaptureFactory captures,
        Func<FakeSpeakerObserver>? observerFactory = null, FakeWordAligner? aligner = null,
        LocalDiagnosticLog? diagnostics = null) =>
        new(() => engine, captures, new(Path.GetTempPath()), () => new(),
            observerFactory: observerFactory, alignerFactory: aligner is null ? null : () => aligner,
            writeMeeting: _ => Task.CompletedTask, diagnostics: diagnostics);

    private static async Task SendAsync(FakeMeetingCapture capture, Engine engine, int seconds, int totalSeconds)
    {
        capture.Send(seconds * 32000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.Samples != totalSeconds * 16000) await Task.Delay(5, timeout.Token);
    }

    private sealed class Engine : IMeetingRecognitionEngine
    {
        private IProgress<TranscriptUpdate>? _progress;
        private TranscriptUpdate _last = new("", false);
        private long _samples;
        public long Samples => Interlocked.Read(ref _samples);
        public Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioModel>>([new() { Id = "vibevoice", Family = "vibevoice_asr_streaming", Mode = "streaming" }]);
        public Task PrepareMeetingAsync(AudioModel model, RecognitionOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task StreamMeetingAsync(AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
            IProgress<TranscriptUpdate> progress, CancellationToken token, Action? onReady = null)
        {
            _progress = progress;
            onReady?.Invoke();
            await foreach (var packet in audio.ReadAllAsync(token)) Interlocked.Add(ref _samples, packet.Length / 2);
            progress.Report(_last with { IsFinal = true });
        }
        public void Report(string text) => Publish(new(text, false));
        public void ReportTimed(IReadOnlyList<MeetingTurn> turns) => Publish(new("", false) { SpeakerTurns = turns });
        private void Publish(TranscriptUpdate update) { _last = update; _progress!.Report(update); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
