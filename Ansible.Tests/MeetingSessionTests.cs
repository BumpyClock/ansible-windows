using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Ansible-meeting-session-" + Guid.NewGuid().ToString("N"));
    private static readonly MeetingCaptureRequest Request = new(MeetingPlaybackSource.SystemAudio);

    [Fact]
    public async Task PauseFreezesUpdatesAndCaptureResumeUsesSameStreamAndEndSavesFinal()
    {
        var engine = new Engine();
        var capture = new CaptureFactory();
        var reserved = 0;
        var released = 0;
        var store = new MeetingStore(_directory);
        await using var session = new MeetingSession(() => engine, capture, store, new(),
            () => { reserved++; return Task.CompletedTask; }, () => { released++; return Task.CompletedTask; });
        await session.StartAsync(Request);
        capture.Captures[0].Send(3200);
        await WaitUntilAsync(() => engine.ReadBytes == 3200);
        engine.Report("Speaker 0: First");
        await session.PauseAsync();
        Assert.True(capture.Captures[0].IsReleased);
        Assert.True(session.IsActive);
        engine.Report("Speaker 0: First Speaker 1: Pending");
        Assert.Equal("First", Assert.Single(session.Document!.Turns).Text);
        await session.ResumeAsync();
        Assert.Equal(2, session.Document!.Turns.Count);
        Assert.Equal(1, engine.StreamCalls);
        Assert.Equal(2, capture.Captures.Count);
        capture.Captures[1].Send(6400);
        await session.EndAsync();
        Assert.Equal(9600, engine.ReadBytes);
        Assert.Equal(MeetingSessionPhase.Completed, session.State.Phase);
        Assert.False(session.IsActive);
        Assert.True(engine.Disposed);
        Assert.Equal(1, reserved);
        Assert.Equal(1, released);
        Assert.All(session.Document!.Turns, turn => Assert.False(turn.IsProvisional));
        var saved = await store.LoadAsync(session.Document.Id);
        Assert.Equal(MeetingStatus.Completed, saved.Status);
        Assert.Equal("Pending", saved.Turns[1].Text);
    }

    [Fact]
    public async Task PausedBacklogRemainsUnreadUntilResume()
    {
        var engine = new Engine { BlockReads = true };
        var captures = new CaptureFactory();
        await using var session = Create(engine, captures);
        await session.StartAsync(Request);
        captures.Captures[0].Send(6400);
        await WaitUntilAsync(() => session.State.CapturedSeconds == 0.2);
        await session.PauseAsync();
        engine.AllowReads.TrySetResult();
        await Task.Delay(30);
        Assert.Equal(0, engine.ReadBytes);
        await session.ResumeAsync();
        await WaitUntilAsync(() => engine.ReadBytes == 6400);
        await session.EndAsync();
    }

    [Fact]
    public async Task NewCumulativeUpdateOnResumeSupersedesOlderPausedUpdate()
    {
        var engine = new Engine();
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        engine.Report("Speaker 0: First");
        await session.PauseAsync();
        engine.Report("Speaker 0: First older pending");
        var injected = false;
        session.StateChanged += state =>
        {
            if (state.Phase != MeetingSessionPhase.Recording || injected) return;
            injected = true;
            engine.Report("Speaker 0: First older pending newest words");
        };
        await session.ResumeAsync();
        Assert.Equal("First older pending newest words", Assert.Single(session.Document!.Turns).Text);
        await session.EndAsync();
    }

    [Fact]
    public async Task DiscardCancelsRecognitionAndKeepsAlreadySavedTranscript()
    {
        var engine = new Engine();
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        engine.Report("Speaker 0: Preserve this");
        await session.DiscardAsync();
        Assert.True(engine.Canceled);
        Assert.False(session.IsActive);
        Assert.Equal(MeetingStatus.Interrupted, session.Document!.Status);
        Assert.Equal("Preserve this", Assert.Single((await new MeetingStore(_directory).LoadAsync(session.Document.Id)).Turns).Text);
    }

    [Fact]
    public async Task DiscardWhilePausedDoesNotPublishPendingSpeech()
    {
        var engine = new Engine();
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        engine.Report("Speaker 0: Saved speech");
        await session.PauseAsync();
        engine.Report("Speaker 0: Saved speech Speaker 1: Pending speech");
        await session.DiscardAsync();
        Assert.Equal("Saved speech", Assert.Single(session.Document!.Turns).Text);
    }

    [Fact]
    public async Task StartupFailureReleasesEngineAndDictationExclusion()
    {
        var engine = new Engine { ConnectFailure = new InvalidOperationException("Model missing") };
        var releases = 0;
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), new(_directory), new(),
            release: () => { releases++; return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Request));
        Assert.Equal("Model missing", error.Message);
        Assert.False(session.IsActive);
        Assert.True(engine.Disposed);
        Assert.Equal(1, releases);
        Assert.Equal(MeetingSessionPhase.Failed, session.State.Phase);
    }

    [Fact]
    public async Task EndWhilePausedFinishesPendingUpdateWithoutRestartingCapture()
    {
        var engine = new Engine();
        var captures = new CaptureFactory();
        await using var session = Create(engine, captures);
        await session.StartAsync(Request);
        engine.Report("Speaker 0: First");
        await session.PauseAsync();
        engine.Report("Speaker 0: First Speaker 1: Pending");
        await session.EndAsync();
        Assert.Single(captures.Captures);
        Assert.Equal("Pending", session.Document!.Turns[1].Text);
        Assert.All(session.Document.Turns, turn => Assert.False(turn.IsProvisional));
    }

    [Fact]
    public async Task FailedCaptureStartupRetainsOwnershipUntilRetryReleasesResources()
    {
        var retained = new Capture { BlockRelease = true };
        var captures = new CaptureFactory { StartupFailure = new CaptureOwnershipException("Capture failed", retained, new IOException("Device lost")) };
        var releases = 0;
        var engine = new Engine();
        await using var session = new MeetingSession(() => engine, captures, new(_directory), new(),
            release: () => { releases++; return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync(Request));
        Assert.True(session.IsActive);
        Assert.Equal(0, releases);
        Assert.False(engine.Disposed);
        retained.BlockRelease = false;
        await session.EndAsync();
        Assert.True(retained.IsReleased);
        Assert.False(session.IsActive);
        Assert.Equal(1, releases);
        Assert.Equal(MeetingStatus.Failed, session.Document!.Status);
    }

    [Fact]
    public async Task BoundedBacklogFailsAndPreservesText()
    {
        var engine = new Engine { BlockReads = true };
        var captures = new CaptureFactory();
        await using var session = Create(engine, captures);
        await session.StartAsync(Request);
        engine.Report("Speaker 0: Before overload");
        captures.Captures[0].Send(3200 * 301);
        await WaitUntilAsync(() => session.State.Phase == MeetingSessionPhase.Failed);
        Assert.Contains("30 seconds", session.State.Error);
        engine.AllowReads.TrySetResult();
        await WaitUntilAsync(() => !session.IsActive);
        Assert.True(captures.Captures[0].IsReleased);
        Assert.Equal("Before overload", Assert.Single(session.Document!.Turns).Text);
    }

    [Fact]
    public async Task TranscriptUpdatesDoNotOverwriteTitleOrSpeakerEdits()
    {
        var engine = new Engine();
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        engine.Report("Speaker 0: First");
        await session.RenameAsync("Team planning");
        await session.RenameSpeakerAsync("Speaker 0", "Alex");
        engine.Report("Speaker 0: First continued Speaker 1: Reply");
        await session.EndAsync();
        var saved = await new MeetingStore(_directory).LoadAsync(session.Document!.Id);
        Assert.Equal("Team planning", saved.Title);
        Assert.Equal("Alex", saved.GetSpeakerName("Speaker 0"));
        Assert.Equal("First continued", saved.Turns[0].Text);
    }

    private MeetingSession Create(Engine engine, CaptureFactory captures) => new(() => engine, captures, new(_directory), new());

    [Fact]
    public async Task TimingRefinementRetainsAnchorsAndCleanupPreservesOriginalAndNames()
    {
        var engine = new Engine();
        var captures = new CaptureFactory();
        var aligner = new Aligner();
        await using var session = new MeetingSession(() => engine, captures, new(_directory), new(), alignerFactory: () => aligner);
        await session.StartAsync(Request);
        captures.Captures[0].Send(16000 * 2 * 3);
        await WaitUntilAsync(() => engine.ReadBytes == 96000);
        engine.Report("Speaker 0: Hello there");
        var turn = Assert.Single(session.Document!.Turns);
        Assert.Equal(MeetingTimingQuality.Chunk, turn.TimingQuality);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyCleanupAsync(["Hello."]));
        await session.RenameSpeakerAsync("Speaker 0", "Alex");
        await session.EndAsync();
        Assert.True(aligner.Disposed);
        Assert.Equal(0.5, session.Document!.Turns[0].StartSeconds);
        await session.ApplyCleanupAsync(["Hello, there."]);
        var saved = await new MeetingStore(_directory).LoadAsync(session.Document.Id);
        Assert.Equal("Hello there", saved.Turns[0].OriginalText);
        Assert.Equal("Hello, there.", saved.Turns[0].Text);
        Assert.Equal("Alex", saved.GetSpeakerName("Speaker 0"));
        Assert.Equal(0.5, saved.Turns[0].StartSeconds);
    }

    [Fact]
    public async Task OptionalAlignmentFailureDoesNotStopLocalTranscription()
    {
        var engine = new Engine();
        var captures = new CaptureFactory();
        var aligner = new Aligner { FailInitialization = true };
        await using var session = new MeetingSession(() => engine, captures, new(_directory), new(), alignerFactory: () => aligner);
        await session.StartAsync(Request);
        captures.Captures[0].Send(32000);
        await WaitUntilAsync(() => engine.ReadBytes == 32000);
        engine.Report("Speaker 0: Local speech");
        await session.EndAsync();
        Assert.Contains("unavailable", session.State.RefinementStatus);
        Assert.Equal(MeetingSessionPhase.Completed, session.State.Phase);
        Assert.Equal("Local speech", session.Document!.Turns[0].Text);
        Assert.Equal(MeetingTimingQuality.Chunk, session.Document.Turns[0].TimingQuality);
        Assert.True(aligner.Disposed);
    }

    private sealed class Aligner : IMeetingWordAligner
    {
        public bool FailInitialization { get; init; }
        public bool Disposed { get; private set; }
        public Task InitializeAsync(CancellationToken token) => FailInitialization
            ? Task.FromException(new InvalidOperationException("Alignment weights missing")) : Task.CompletedTask;
        public Task<IReadOnlyList<MeetingTurn>> AlignAsync(WaveAudio audio, IReadOnlyList<MeetingTurn> turns, CancellationToken token)
            => Task.FromResult<IReadOnlyList<MeetingTurn>>(turns.Select(turn => turn with
                { StartSeconds = 0.5, EndSeconds = 1.5, TimingQuality = MeetingTimingQuality.Aligned }).ToArray());
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task TeamsEvidenceNamesTwoExclusiveAlignedTurnsAndManualNameWins()
    {
        var engine = new Engine();
        var captures = new CaptureFactory();
        var observer = new Observer();
        await using var session = new MeetingSession(() => engine, captures, new(_directory), new(),
            observerFactory: () => observer, alignerFactory: () => new TwoTurnAligner());
        await session.StartAsync(new(MeetingPlaybackSource.MeetingApp, 42));
        captures.Captures[0].PlaybackRms = 0.2;
        captures.Captures[0].Send(9 * 32000);
        await WaitUntilAsync(() => engine.ReadBytes == 9 * 32000);
        for (var second = 0; second <= 8; second++) observer.Report(new(second * 16000, ["Alex"], null, MeetingSpeakerReadStatus.Readable));
        engine.Report("Speaker 0: First words Speaker 0: Second words");
        Assert.Empty(session.Document!.AutoSpeakerNames);
        await session.EndAsync();
        Assert.Equal("Alex", session.Document!.GetSpeakerName("Speaker 0"));
        Assert.All(session.Document.Turns, turn => Assert.Equal(MeetingAudioSource.Playback, turn.Source));
        Assert.True(observer.Disposed);
        await session.RenameSpeakerAsync("Speaker 0", "Corrected name");
        Assert.Equal("Corrected name", session.Document.GetSpeakerName("Speaker 0"));
    }

    private sealed class TwoTurnAligner : IMeetingWordAligner
    {
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<MeetingTurn>> AlignAsync(WaveAudio audio, IReadOnlyList<MeetingTurn> turns, CancellationToken token)
            => Task.FromResult<IReadOnlyList<MeetingTurn>>(turns.Select(turn => turn with
                { StartSeconds = turn.Text.StartsWith("First", StringComparison.Ordinal) ? 1 : 5,
                    EndSeconds = turn.Text.StartsWith("First", StringComparison.Ordinal) ? 3 : 7,
                    TimingQuality = MeetingTimingQuality.Aligned }).ToArray());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Observer : IMeetingSpeakerObserver
    {
        private Action<MeetingSpeakerObservation>? _report;
        public bool Disposed { get; private set; }
        public Task StartAsync(int processId, Func<long> position, Action<MeetingSpeakerObservation> report, CancellationToken token)
        { _report = report; return Task.CompletedTask; }
        public void Report(MeetingSpeakerObservation observation) => _report!(observation);
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task MeetingReservationBlocksDictationAndSecondReservationUntilReleased()
    {
        var dictation = new DictationSession(_ => throw new InvalidOperationException("Engine must remain unused"),
            new UnusedCaptureFactory(), new UnusedAudioInput(), new UsageStore(Path.Combine(_directory, "usage.json")),
            _directory, new AppSettingsStore(Path.Combine(_directory, "settings.json")));
        await dictation.ReserveMeetingAsync();
        Assert.True(dictation.State.MeetingActive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dictation.ReserveMeetingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => dictation.StartDictationAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => dictation.CloseAsync());
        await dictation.ReleaseMeetingAsync();
        Assert.False(dictation.State.MeetingActive);
        await dictation.ReserveMeetingAsync();
        await dictation.ReleaseMeetingAsync();
        await dictation.CloseAsync();
    }

    [Fact]
    public async Task DisposalBlocksNewMeetingAdmissionWhileOwnedCaptureIsStillReleasing()
    {
        var engine = new Engine();
        var captures = new CaptureFactory();
        await using var session = Create(engine, captures);
        await session.StartAsync(Request);
        var capture = captures.Captures[0];
        capture.StopBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposal = session.DisposeAsync().AsTask();
        await capture.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restart = session.StartAsync(Request);
        capture.StopBarrier.TrySetResult();
        await disposal;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => restart);
        Assert.Single(captures.Captures);
        Assert.False(session.IsActive);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class Engine : IMeetingRecognitionEngine
    {
        private IProgress<TranscriptUpdate>? _progress;
        private string _text = "";
        private long _readBytes;
        public long ReadBytes => Interlocked.Read(ref _readBytes);
        public int StreamCalls { get; private set; }
        public bool Disposed { get; private set; }
        public bool Canceled { get; private set; }
        public bool BlockReads { get; init; }
        public Exception? ConnectFailure { get; init; }
        public TaskCompletionSource AllowReads { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken cancellationToken) => ConnectFailure is { } error
            ? Task.FromException<IReadOnlyList<AudioModel>>(error)
            : Task.FromResult<IReadOnlyList<AudioModel>>([new() { Id = "vibevoice", Family = "vibevoice_asr_streaming", Mode = "streaming" }]);
        public async Task StreamMeetingAsync(AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
            IProgress<TranscriptUpdate> progress, CancellationToken cancellationToken, Action? onReady = null)
        {
            StreamCalls++;
            _progress = progress;
            onReady?.Invoke();
            try
            {
                if (BlockReads) await AllowReads.Task.WaitAsync(cancellationToken);
                await foreach (var packet in audio.ReadAllAsync(cancellationToken)) Interlocked.Add(ref _readBytes, packet.Length);
                progress.Report(new(_text, true));
            }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
        public void Report(string text) { _text = text; _progress!.Report(new(text, false)); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class CaptureFactory : IMeetingCaptureFactory
    {
        public List<Capture> Captures { get; } = [];
        public Exception? StartupFailure { get; init; }
        public Task<IAudioCapture> StartAsync(MeetingCaptureRequest request, CancellationToken cancellationToken)
        {
            if (StartupFailure is { } error) return Task.FromException<IAudioCapture>(error);
            var capture = new Capture();
            Captures.Add(capture);
            return Task.FromResult<IAudioCapture>(capture);
        }
    }

    private sealed class Capture : IAudioCapture, IMeetingAudioSourceMetadata
    {
        private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>();
        public ChannelReader<byte[]> Audio => _audio.Reader;
        public double CapturedSeconds { get; private set; }
        public bool IsReleased { get; private set; }
        public bool BlockRelease { get; set; }
        public double PlaybackRms { get; set; }
        public long ClockSamplePosition => (long)(CapturedSeconds * 16000);
        public event Action<MeetingAudioActivity>? Activity;
        public TaskCompletionSource? StopBarrier { get; set; }
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<double>? LevelChanged { add { } remove { } }
        public void Send(int bytes)
        {
            Activity?.Invoke(new(ClockSamplePosition, bytes / 2, 0, PlaybackRms));
            CapturedSeconds += bytes / 32000.0;
            _audio.Writer.TryWrite(new byte[bytes]);
        }
        public async Task StopAsync()
        {
            StopEntered.TrySetResult();
            if (StopBarrier is { } barrier) await barrier.Task;
            _audio.Writer.TryComplete();
        }
        public ValueTask DisposeAsync() { IsReleased = !BlockRelease; return ValueTask.CompletedTask; }
    }

    private sealed class UnusedCaptureFactory : IAudioCaptureFactory
    {
        public Task<IAudioCapture> StartAsync(CancellationToken cancellationToken, int microphoneBoostDecibels = 0) =>
            throw new InvalidOperationException("Dictation capture must remain unused");
    }

    private sealed class UnusedAudioInput : IAudioInputReader
    {
        public Task<WaveAudio> ReadRecordingAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> ReadReplayAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
