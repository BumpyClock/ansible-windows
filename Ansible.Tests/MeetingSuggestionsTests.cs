using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    private static readonly MeetingDetectionCandidate SuggestedTeams = new(42,
        new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public Task MeetingSuggestionsRequireStableObservations() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.StartAsync();
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(null);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Equal(SuggestedTeams, harness.Suggestions.Offer);
        Assert.All(harness.CallbackThreads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task MeetingSuggestionsGateChangesRequireFreshObservations(bool disable) => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        var oldRead = await harness.Detector.NextAsync();
        harness.SetGate(disable, true);
        Assert.Null(harness.Suggestions.Offer);
        harness.SetGate(disable, false);
        oldRead.Complete(SuggestedTeams);
        await Task.Yield();
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Equal(SuggestedTeams, harness.Suggestions.Offer);
    });

    [Fact]
    public Task MeetingSuggestionsDisabledDoesNotScanOrRearmDismissal() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        harness.Suggestions.Dismiss();
        await harness.ObserveAsync(null);
        var reads = harness.Detector.Reads;
        harness.SetGate(disable: true, blocked: true);
        harness.Clock.Advance(TimeSpan.FromSeconds(20));
        await Task.Yield();
        Assert.Equal(reads, harness.Detector.Reads);
        harness.SetGate(disable: true, blocked: false);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
    });

    [Fact]
    public Task MeetingSuggestionsFailureHidesWarnsPerStreakAndRetries() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        await harness.FailAsync();
        Assert.Null(harness.Suggestions.Offer);
        harness.Suggestions.Refresh();
        Assert.Null(harness.Suggestions.Offer);
        await harness.FailAsync();
        Assert.Single(harness.Warnings);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Equal(SuggestedTeams, harness.Suggestions.Offer);
        await harness.FailAsync();
        Assert.Equal(2, harness.Warnings.Count);
    });

    [Fact]
    public Task MeetingSuggestionsFailedScanIsNotDismissedAbsence() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        harness.Suggestions.Dismiss();
        await harness.ObserveAsync(null);
        await harness.FailAsync();
        await harness.ObserveAsync(null, seconds: 20);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(null);
        await harness.ObserveAsync(null, seconds: 10);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Equal(SuggestedTeams, harness.Suggestions.Offer);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MeetingSuggestionsActiveMeetingDismissesEvenWithoutCurrentOffer(bool failedScan) => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        if (failedScan) await harness.FailAsync();
        harness.Active = SuggestedTeams;
        harness.Blocked = true;
        harness.Suggestions.Refresh();
        Assert.Null(harness.Suggestions.Offer);
        harness.Active = null;
        harness.Blocked = false;
        harness.Suggestions.Refresh();
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
    });

    [Fact]
    public Task MeetingSuggestionsScanWhileBusyToSuppressAlreadyActiveMeeting() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness { Blocked = true, Active = SuggestedTeams };
        await harness.StartAsync();
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
        harness.Active = null;
        harness.Blocked = false;
        harness.Suggestions.Refresh();
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
    });

    [Fact]
    public Task MeetingSuggestionsStaleConfirmationCannotOpenOrReshow() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        var confirming = harness.Suggestions.ConfirmAsync(harness.Open);
        var read = await harness.Detector.NextAsync();
        Assert.True(harness.Suggestions.IsConfirming);
        Assert.Equal(MeetingSuggestionOutcome.Skipped, await harness.Suggestions.ConfirmAsync(harness.Open));
        read.Complete(SuggestedTeams with { ProcessStartedUtc = SuggestedTeams.ProcessStartedUtc.AddMinutes(1) });
        Assert.Equal(MeetingSuggestionOutcome.Stale, await confirming);
        Assert.Empty(harness.Opened);
        harness.Suggestions.Refresh();
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Equal(SuggestedTeams, harness.Suggestions.Offer);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task MeetingSuggestionsConfirmationRechecksGateAfterRead(bool disable) => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        var confirming = harness.Suggestions.ConfirmAsync(harness.Open);
        var read = await harness.Detector.NextAsync();
        harness.SetGate(disable, true);
        harness.SetGate(disable, false);
        read.Complete(SuggestedTeams);
        Assert.Equal(MeetingSuggestionOutcome.Stale, await confirming);
        Assert.Empty(harness.Opened);
        Assert.Null(harness.Suggestions.Offer);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MeetingSuggestionsRejectedOrFailedOpenInvalidatesPendingPoll(bool throws) => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        var oldPoll = await harness.Detector.NextAsync();
        var confirming = harness.Suggestions.ConfirmAsync(_ => throws ? throw new IOException("Cannot open.") : false);
        (await harness.Detector.NextAsync()).Complete(SuggestedTeams);
        Assert.Equal(throws ? MeetingSuggestionOutcome.Failed : MeetingSuggestionOutcome.Rejected, await confirming);
        oldPoll.Complete(SuggestedTeams);
        await Task.Yield();
        harness.Suggestions.Refresh();
        Assert.Null(harness.Suggestions.Offer);
        Assert.Single(harness.Warnings);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Equal(SuggestedTeams, harness.Suggestions.Offer);
    });

    [Fact]
    public Task MeetingSuggestionsSuccessfulOpenDismissesUntilObservedAbsence() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        var confirming = harness.Suggestions.ConfirmAsync(harness.Open);
        (await harness.Detector.NextAsync()).Complete(SuggestedTeams);
        Assert.Equal(MeetingSuggestionOutcome.Opened, await confirming);
        Assert.Equal([SuggestedTeams], harness.Opened);
        Assert.False(harness.Suggestions.IsConfirming);
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(SuggestedTeams, seconds: 30);
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(null);
        await harness.ObserveAsync(null, seconds: 10);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Equal(SuggestedTeams, harness.Suggestions.Offer);
    });

    [Fact]
    public Task MeetingSuggestionsShutdownCancelsAndJoinsPollAndConfirmationBeforeRestart() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        harness.Detector.HonorCancellation = false;
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        var poll = await harness.Detector.NextAsync();
        var confirming = harness.Suggestions.ConfirmAsync(harness.Open);
        var confirm = await harness.Detector.NextAsync();
        harness.Suggestions.BeginShutdown();
        Assert.True(poll.Token.IsCancellationRequested);
        Assert.True(confirm.Token.IsCancellationRequested);
        Assert.Null(harness.Suggestions.Offer);
        var joined = harness.Suggestions.JoinAsync();
        Assert.False(joined.IsCompleted);
        Assert.Throws<InvalidOperationException>(harness.Suggestions.ResumeAfterFailedShutdown);
        poll.Complete(SuggestedTeams);
        await Task.Yield();
        Assert.False(joined.IsCompleted);
        confirm.Complete(SuggestedTeams);
        Assert.Equal(MeetingSuggestionOutcome.Skipped, await confirming);
        await joined;
        Assert.Empty(harness.Opened);
        Assert.Empty(harness.Warnings);
        harness.Suggestions.ResumeAfterFailedShutdown();
        var restarted = await harness.Detector.NextAsync();
        Assert.False(restarted.Token.IsCancellationRequested);
        restarted.Complete(SuggestedTeams);
        await Task.Yield();
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Equal(SuggestedTeams, harness.Suggestions.Offer);
    });

    [Fact]
    public Task MeetingSuggestionsShutdownCancelsCooperativeReadWithoutWarning() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        harness.Suggestions.Start();
        var read = await harness.Detector.NextAsync();
        harness.Suggestions.BeginShutdown();
        await harness.Suggestions.JoinAsync();
        Assert.True(read.Token.IsCancellationRequested);
        Assert.Empty(harness.Warnings);
    });

    [Fact]
    public Task MeetingSuggestionsRecoveryDoesNotStartUninitializedMonitor() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        harness.Suggestions.BeginShutdown();
        await harness.Suggestions.JoinAsync();
        harness.Suggestions.ResumeAfterFailedShutdown();
        harness.Clock.Advance(TimeSpan.FromSeconds(20));
        await Task.Yield();
        Assert.Equal(0, harness.Detector.Reads);
        await harness.StartAsync();
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Equal(SuggestedTeams, harness.Suggestions.Offer);
    });

    [Fact]
    public Task MeetingSuggestionsConfirmationFailureIsReportedAndJoined() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        var confirming = harness.Suggestions.ConfirmAsync(harness.Open);
        (await harness.Detector.NextAsync()).Fail();
        Assert.Equal(MeetingSuggestionOutcome.Failed, await confirming);
        Assert.Empty(harness.Opened);
        Assert.Single(harness.Warnings);
        harness.Suggestions.Refresh();
        Assert.Null(harness.Suggestions.Offer);
        harness.Suggestions.BeginShutdown();
        await harness.Suggestions.JoinAsync();
    });

    [Fact]
    public Task MeetingSuggestionsRestartPreservesDismissalAndDoesNotCountStoppedTime() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.OfferAsync();
        harness.Suggestions.Dismiss();
        await harness.ObserveAsync(null);
        harness.Suggestions.BeginShutdown();
        await harness.Suggestions.JoinAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        harness.Suggestions.ResumeAfterFailedShutdown();
        (await harness.Detector.NextAsync()).Complete(null);
        await Task.Yield();
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        await harness.ObserveAsync(SuggestedTeams);
        Assert.Null(harness.Suggestions.Offer);
    });

    [Fact]
    public Task MeetingSuggestionsRepeatedStartAndStartDuringShutdownDoNotCreateReaders() => UiThread.RunAsync(async () =>
    {
        await using var harness = new SuggestionsHarness();
        await harness.StartAsync();
        harness.Suggestions.Start();
        await Task.Yield();
        Assert.Equal(1, harness.Detector.Reads);
        harness.Suggestions.BeginShutdown();
        await harness.Suggestions.JoinAsync();
        harness.Suggestions.Start();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();
        Assert.Equal(1, harness.Detector.Reads);
    });

    private sealed class SuggestionsHarness : IAsyncDisposable
    {
        public ManualTime Clock { get; } = new();
        public FakeMeetingDetector Detector { get; } = new();
        public bool Enabled { get; set; } = true;
        public bool Blocked { get; set; }
        public MeetingDetectionCandidate? Active { get; set; }
        public List<(string Title, string Message)> Warnings { get; } = [];
        public List<MeetingDetectionCandidate> Opened { get; } = [];
        public List<int> CallbackThreads { get; } = [];
        public MeetingSuggestions Suggestions { get; }

        public SuggestionsHarness()
        {
            Suggestions = new(Detector, () => Enabled, () => Blocked, candidate => candidate == Active,
                (title, message) => Warnings.Add((title, message)), Clock);
            Suggestions.Changed += () => CallbackThreads.Add(Environment.CurrentManagedThreadId);
        }

        public bool Open(MeetingDetectionCandidate candidate)
        {
            CallbackThreads.Add(Environment.CurrentManagedThreadId);
            Opened.Add(candidate);
            return true;
        }

        public void SetGate(bool disable, bool blocked)
        {
            if (disable) Enabled = !blocked;
            else Blocked = blocked;
            Suggestions.Refresh();
        }

        public async Task StartAsync()
        {
            Suggestions.Start();
            (await Detector.NextAsync()).Complete(SuggestedTeams);
            await Task.Yield();
        }

        public async Task OfferAsync()
        {
            await StartAsync();
            await ObserveAsync(SuggestedTeams);
            await ObserveAsync(SuggestedTeams);
            Assert.Equal(SuggestedTeams, Suggestions.Offer);
        }

        public async Task ObserveAsync(MeetingDetectionCandidate? candidate, int seconds = 2)
        {
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            (await Detector.NextAsync()).Complete(candidate);
            await Task.Yield();
        }

        public async Task FailAsync()
        {
            Clock.Advance(TimeSpan.FromSeconds(2));
            (await Detector.NextAsync()).Fail();
            await Task.Yield();
        }

        public async ValueTask DisposeAsync()
        {
            Suggestions.BeginShutdown();
            Detector.CancelOutstanding();
            await Suggestions.JoinAsync();
            Suggestions.Dispose();
        }
    }

    private sealed class FakeMeetingDetector : IMeetingDetector
    {
        private readonly Queue<DetectionRead> _pending = [];
        private readonly List<DetectionRead> _all = [];
        private readonly SemaphoreSlim _available = new(0);
        public int Reads { get; private set; }
        public bool HonorCancellation { get; set; } = true;

        public async Task<MeetingDetectionCandidate?> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            var read = new DetectionRead(cancellationToken);
            _all.Add(read);
            _pending.Enqueue(read);
            _available.Release();
            using var registration = HonorCancellation ? cancellationToken.Register(read.Cancel) : default;
            return await read.Result.Task;
        }

        public async Task<DetectionRead> NextAsync()
        {
            Assert.True(await _available.WaitAsync(TimeSpan.FromSeconds(5)), "No detector read started.");
            return _pending.Dequeue();
        }

        public void CancelOutstanding()
        {
            foreach (var read in _all) read.Cancel();
        }
    }

    private sealed class DetectionRead(CancellationToken token)
    {
        public CancellationToken Token => token;
        public TaskCompletionSource<MeetingDetectionCandidate?> Result { get; } = new();
        public void Complete(MeetingDetectionCandidate? candidate) => Result.TrySetResult(candidate);
        public void Fail() => Result.TrySetException(new MeetingDetectionUnavailableException("Capture metadata unavailable."));
        public void Cancel() => Result.TrySetCanceled(token);
    }
}
