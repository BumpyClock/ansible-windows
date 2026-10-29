using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingSpeakerEvidenceTests
{
    [Fact]
    public async Task RefinementClipsTheRingAndOnlyReplacesNewAlignedTurnsInStreamCoordinates()
    {
        var aligner = new FakeWordAligner
        {
            Align = turns => turns.Select(turn => turn with
                { StartSeconds = 6, EndSeconds = 8, TimingQuality = MeetingTimingQuality.Aligned }).ToArray()
        };
        await using var evidence = Create(aligner: aligner);
        evidence.Append(new byte[45 * 32000]);
        var old = Turn(0, 5);
        var anchor = Turn(10, 16, MeetingTimingQuality.Aligned);
        var provisional = Turn(20, 25) with { IsProvisional = true };
        MeetingTurn[] turns = [old, anchor, Turn(20, 25), provisional];

        evidence.RefineChunkTurns(turns, [], 45 * 16000);

        Assert.Equal(30 * 16000, aligner.SampleCounts[^1]);
        Assert.Equal(2, aligner.Inputs[^1].Count);
        Assert.Equal(0, aligner.Inputs[^1][0].StartSeconds);
        Assert.Equal(1, aligner.Inputs[^1][0].EndSeconds);
        Assert.Equal(5, aligner.Inputs[^1][1].StartSeconds);
        Assert.Equal(10, aligner.Inputs[^1][1].EndSeconds);
        Assert.Equal(old, turns[0]);
        Assert.Equal(anchor, turns[1]);
        Assert.Equal(21, turns[2].StartSeconds);
        Assert.Equal(23, turns[2].EndSeconds);
        Assert.Equal(MeetingTimingQuality.Aligned, turns[2].TimingQuality);
        Assert.Equal(provisional, turns[3]);
        evidence.RefineChunkTurns(turns, [], 45 * 16000);
        Assert.Equal(1, aligner.AlignCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15 * 16000)]
    [InlineData(46 * 16000)]
    public async Task MissingClipDoesNotCreateAnAligner(long processed)
    {
        var creations = 0;
        await using var evidence = new MeetingSpeakerEvidence(null, () => { creations++; return new FakeWordAligner(); },
            null, (_, _, _) => { }, default);
        evidence.Append(new byte[45 * 32000]);
        evidence.RefineChunkTurns([Turn(20, 25)], [], processed);
        Assert.Equal(0, creations);
        Assert.False(evidence.HasResources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlignmentFailureLatchesAndReportsOnceWithoutChangingChunkTimes(bool failInitialization)
    {
        var error = new InvalidOperationException("Optional alignment unavailable");
        var aligner = new FakeWordAligner
        {
            Initialize = _ => failInitialization ? Task.FromException(error) : Task.CompletedTask,
            Align = _ => throw error
        };
        var reports = new List<EvidenceReport>();
        await using var evidence = Create(aligner: aligner, reports: reports);
        evidence.Append(new byte[5 * 32000]);
        var turn = Turn(0, 3);
        MeetingTurn[] turns = [turn];

        evidence.RefineChunkTurns(turns, [], 5 * 16000);
        evidence.RefineChunkTurns(turns, [], 5 * 16000);

        Assert.Equal(turn, Assert.Single(turns));
        Assert.Equal(1, aligner.InitializeCalls);
        Assert.Equal(failInitialization ? 0 : 1, aligner.AlignCalls);
        var failed = Assert.Single(reports, report => !report.Available);
        Assert.Equal(MeetingDiagnosticKind.RefinementAvailability, failed.Kind);
        Assert.Same(error, failed.Error);
        Assert.Equal(failInitialization ? 1 : 2, reports.Count);
    }

    [Fact]
    public async Task AlignmentAvailabilityIsReportedOnceAndMissingFactoryIsUnavailable()
    {
        var reports = new List<EvidenceReport>();
        await using (var absent = Create(reports: reports))
        {
            Assert.Equal(new(MeetingDiagnosticKind.RefinementAvailability, false), Assert.Single(reports));
            Assert.False(absent.HasResources);
        }
        reports.Clear();
        var aligner = new FakeWordAligner();
        await using var evidence = Create(aligner: aligner, reports: reports);
        evidence.Append(new byte[5 * 32000]);
        evidence.RefineChunkTurns([Turn(0, 3)], [], 5 * 16000);
        evidence.RefineChunkTurns([Turn(1, 4)], [], 5 * 16000);
        Assert.Equal(new(MeetingDiagnosticKind.RefinementAvailability, true), Assert.Single(reports));
        Assert.True(evidence.HasResources);
        Assert.Equal(2, aligner.AlignCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedAlignmentCancellationIsSilentAndKeepsChunkTimes(bool cancelInitialization)
    {
        using var cancellation = new CancellationTokenSource();
        var aligner = new FakeWordAligner
        {
            Initialize = token =>
            {
                if (!cancelInitialization) return Task.CompletedTask;
                cancellation.Cancel();
                return Task.FromCanceled(token);
            },
            Align = _ =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation was ignored");
            }
        };
        var reports = new List<EvidenceReport>();
        await using var evidence = Create(aligner: aligner, reports: reports, token: cancellation.Token);
        evidence.Append(new byte[5 * 32000]);
        var original = Turn(0, 3);
        MeetingTurn[] turns = [original];
        evidence.RefineChunkTurns(turns, [], 5 * 16000);
        Assert.Equal(original, Assert.Single(turns));
        Assert.DoesNotContain(reports, report => !report.Available);
        Assert.Equal(cancelInitialization ? 0 : 1, reports.Count);
        Assert.Equal(cancelInitialization ? 0 : 1, aligner.AlignCalls);
    }

    [Fact]
    public async Task AlignerFactoryFailureIsOptionalAndIsNotRetried()
    {
        var reports = new List<EvidenceReport>();
        var creations = 0;
        var error = new InvalidOperationException("Cannot create optional aligner");
        await using var evidence = new MeetingSpeakerEvidence(null, () => { creations++; throw error; },
            null, (kind, available, failure) => reports.Add(new(kind, available, failure)), default);
        evidence.Append(new byte[5 * 32000]);
        var turn = Turn(0, 3);
        MeetingTurn[] turns = [turn];
        evidence.RefineChunkTurns(turns, [], 5 * 16000);
        evidence.RefineChunkTurns(turns, [], 5 * 16000);
        Assert.Equal(1, creations);
        Assert.Equal(turn, Assert.Single(turns));
        Assert.Same(error, Assert.Single(reports).Error);
        Assert.False(evidence.HasResources);
    }

    [Fact]
    public async Task DisposalRunsAlignerOffTheCallerContextAndRetainsItUntilRetrySucceeds()
    {
        var aligner = new FakeWordAligner { DisposeFailures = 1 };
        var evidence = Create(aligner: aligner);
        evidence.Append(new byte[5 * 32000]);
        evidence.RefineChunkTurns([Turn(0, 3)], [], 5 * 16000);
        var previous = SynchronizationContext.Current;
        Task disposing;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            disposing = evidence.DisposeAsync().AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => disposing);
        Assert.Null(aligner.DisposalContext);
        Assert.True(evidence.HasResources);
        await evidence.DisposeAsync();
        Assert.False(evidence.HasResources);
        await evidence.DisposeAsync();
        Assert.Equal(2, aligner.DisposeCalls);
    }

    [Fact]
    public async Task SourceTagsRetainPriorAlignedBoundsButDoNotInferMissingOrChunkEvidence()
    {
        await using var evidence = Create();
        MeetingTurn[] previous = [
            Turn(0, 1, MeetingTimingQuality.Aligned) with { Source = MeetingAudioSource.Playback },
            Turn(2, 3, MeetingTimingQuality.Aligned) with { Source = MeetingAudioSource.Microphone },
            Turn(4, 5) with { Source = MeetingAudioSource.Playback }
        ];
        MeetingTurn[] turns = [
            Turn(0, 1, MeetingTimingQuality.Aligned) with { Text = "Revised" },
            Turn(2, 4, MeetingTimingQuality.Aligned),
            Turn(4, 5)
        ];

        evidence.RefineTimedTurns(turns, previous);

        Assert.Equal(MeetingAudioSource.Playback, turns[0].Source);
        Assert.Equal(MeetingAudioSource.Mixed, turns[1].Source);
        Assert.Equal(MeetingAudioSource.Mixed, turns[2].Source);
    }

    [Fact]
    public async Task MissingObservationsRemoveAutomaticNamesButKeepManualNames()
    {
        await using var evidence = Create();
        var document = new MeetingDocument
        {
            Turns = [Turn(0, 1, MeetingTimingQuality.Aligned)],
            AutoSpeakerNames = new Dictionary<string, string> { ["Speaker 0"] = "Automatic" },
            SpeakerNames = new Dictionary<string, string> { ["Speaker 0"] = "Manual" }
        };
        var refined = evidence.NameSpeakers(document);
        Assert.Empty(refined.AutoSpeakerNames);
        Assert.Equal("Manual", refined.GetSpeakerName("Speaker 0"));
    }

    [Fact]
    public async Task AttachedObserverReadsStreamPositionsAndRebasesOnEachCapture()
    {
        var observer = new FakeSpeakerObserver();
        var creations = 0;
        var reports = new List<EvidenceReport>();
        await using var evidence = Create(reports: reports, observer: () => { creations++; return observer; });
        var first = new EvidenceSource { Clock = 16000 };
        await evidence.AttachAsync(first, 2 * 16000);
        Assert.Equal(42, observer.ProcessId);
        Assert.Equal(3 * 16000, observer.Position!());
        first.Clock = 2 * 16000;
        Assert.Equal(4 * 16000, observer.Position!());
        await evidence.DetachAsync(() => Task.CompletedTask);
        Assert.Equal(1, observer.Stops);

        var second = new EvidenceSource();
        await evidence.AttachAsync(second, 5 * 16000);
        Assert.Equal(5 * 16000, observer.Position!());
        Assert.Equal(1, creations);
        Assert.Equal(2, observer.Starts);
        Assert.Equal(2, reports.Count(report => report == new EvidenceReport(MeetingDiagnosticKind.NamingAvailability, true)));
        second.Raise(new(0, 16000, 0, 0.2));
        MeetingTurn[] turns = [Turn(5, 6, MeetingTimingQuality.Aligned)];
        evidence.RefineTimedTurns(turns, []);
        Assert.Equal(MeetingAudioSource.Playback, turns[0].Source);
    }

    [Fact]
    public async Task DetachMarksNamesUnavailableBeforeDrainAndKeepsTailActivityUntilDrainEnds()
    {
        var observer = new FakeSpeakerObserver();
        await using var evidence = Create(observer: () => observer);
        var source = new EvidenceSource();
        await evidence.AttachAsync(source, 0);
        source.Raise(new(0, 9 * 16000, 0, 0.2));
        for (var second = 0; second <= 7; second++) observer.Report(second * 16000, "Alex");
        observer.Report(8 * 16000, "Blair");
        source.Clock = 9 * 16000;
        MeetingTurn[] turns = [
            Turn(0, 1, MeetingTimingQuality.Aligned),
            Turn(2, 3, MeetingTimingQuality.Aligned),
            Turn(7, 9, MeetingTimingQuality.Aligned)
        ];
        evidence.RefineTimedTurns(turns, []);
        var document = new MeetingDocument { Turns = turns };
        Assert.Equal("Alex", evidence.NameSpeakers(document).GetSpeakerName("Speaker 0"));

        await evidence.DetachAsync(() =>
        {
            Assert.False(observer.Running);
            source.Clock = 100 * 16000;
            source.Raise(new(9 * 16000, 16000, 0, 0.2));
            return Task.CompletedTask;
        });

        Assert.Empty(evidence.NameSpeakers(document).AutoSpeakerNames);
        Assert.Equal(0, source.Subscribers);
        MeetingTurn[] tail = [Turn(9, 10, MeetingTimingQuality.Aligned)];
        evidence.RefineTimedTurns(tail, []);
        Assert.Equal(MeetingAudioSource.Playback, tail[0].Source);
    }

    [Fact]
    public async Task FailedDrainKeepsTheCaptureAttachedUntilARetrySucceeds()
    {
        var observer = new FakeSpeakerObserver();
        await using var evidence = Create(observer: () => observer);
        var source = new EvidenceSource();
        await evidence.AttachAsync(source, 0);
        var error = new InvalidOperationException("Capture stop failed");

        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(
            () => evidence.DetachAsync(() => Task.FromException(error))));
        Assert.Equal(1, source.Subscribers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => evidence.AttachAsync(new EvidenceSource(), 0));
        await evidence.DetachAsync(() => Task.CompletedTask);

        Assert.Equal(0, source.Subscribers);
        Assert.Equal(2, observer.Stops);
        Assert.Equal(1, observer.Starts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task MissingProcessReportsNamingUnavailableWithoutAnObserver(int? processId)
    {
        var creations = 0;
        var reports = new List<EvidenceReport>();
        await using var evidence = Create(reports: reports, observer: () => { creations++; return new FakeSpeakerObserver(); },
            processId: processId);
        var source = new EvidenceSource();
        await evidence.AttachAsync(source, 0);
        Assert.Equal(new(MeetingDiagnosticKind.NamingAvailability, false), Assert.Single(reports, report => report.Kind == MeetingDiagnosticKind.NamingAvailability));
        Assert.Equal(1, source.Subscribers);
        await evidence.DetachAsync(() => Task.CompletedTask);
        Assert.Equal(0, creations);
        Assert.False(evidence.HasResources);
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public async Task ExpectedObserverStartFailureIsOptionalButOtherFailuresPropagate()
    {
        var expected = new InvalidOperationException("Teams is not observable");
        var reports = new List<EvidenceReport>();
        var observer = new FakeSpeakerObserver { StartFailure = expected };
        await using var evidence = Create(reports: reports, observer: () => observer);
        var source = new EvidenceSource();
        await evidence.AttachAsync(source, 0);
        var naming = Assert.Single(reports, report => report.Kind == MeetingDiagnosticKind.NamingAvailability);
        Assert.False(naming.Available);
        Assert.Same(expected, naming.Error);
        Assert.True(evidence.HasResources);
        await evidence.DetachAsync(() => Task.CompletedTask);

        observer.StartFailure = new NotSupportedException("Unexpected");
        await Assert.ThrowsAsync<NotSupportedException>(() => evidence.AttachAsync(source, 0));
        Assert.Single(reports, report => report.Kind == MeetingDiagnosticKind.NamingAvailability);
        await evidence.DetachAsync(() => Task.CompletedTask);
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public async Task DisposalReleasesActivityAndObserver()
    {
        var observer = new FakeSpeakerObserver();
        var evidence = Create(observer: () => observer);
        var source = new EvidenceSource();
        await evidence.AttachAsync(source, 0);
        await evidence.DisposeAsync();
        Assert.Equal(0, source.Subscribers);
        Assert.True(observer.Disposed);
        Assert.False(evidence.HasResources);
    }

    [Fact]
    public async Task DisposedEvidenceKeepsChunkTimingWithoutCreatingResourcesAndRejectsAttachment()
    {
        var creations = 0;
        var evidence = new MeetingSpeakerEvidence(null, () => { creations++; return new FakeWordAligner(); },
            null, (_, _, _) => { }, default);
        evidence.Append(new byte[45 * 32000]);
        await evidence.DisposeAsync();
        var turn = Turn(20, 25);
        MeetingTurn[] turns = [turn];

        evidence.RefineChunkTurns(turns, [], 45 * 16000);

        Assert.Equal(turn, turns[0]);
        Assert.Equal(0, creations);
        Assert.False(evidence.HasResources);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => evidence.AttachAsync(new EvidenceSource(), 0));
    }

    [Fact]
    public async Task DisposalDuringAlignerCreationDisposesTheCreatedAligner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var aligner = new FakeWordAligner();
        aligner.Initialize = async _ =>
        {
            await aligner.Disposal.Task;
            throw new ObjectDisposedException("aligner");
        };
        var reports = new List<EvidenceReport>();
        var creations = 0;
        await using var evidence = new MeetingSpeakerEvidence(null, () =>
        {
            Interlocked.Increment(ref creations);
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Factory was not released");
            return aligner;
        }, null, (kind, available, failure) => reports.Add(new(kind, available, failure)), default);
        evidence.Append(new byte[5 * 32000]);
        var original = Turn(0, 3);
        MeetingTurn[] turns = [original];
        var refining = Task.Run(() => evidence.RefineChunkTurns(turns, [], 5 * 16000));
        Task disposing;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposing = Task.Run(() => evidence.DisposeAsync().AsTask());
            await Task.WhenAny(disposing, Task.Delay(100));
        }
        finally { release.Set(); }
        await Task.WhenAll(refining, disposing).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, aligner.DisposeCalls);
        Assert.False(evidence.HasResources);
        Assert.DoesNotContain(reports, report => !report.Available);
        Assert.Equal(original, Assert.Single(turns));
        Assert.Equal(1, creations);
    }

    [Fact]
    public async Task FailureAfterDisposalStartsIsSilent()
    {
        var aligner = new FakeWordAligner
        {
            Align = turns => turns.Select(turn => turn with
                { StartSeconds = 1, EndSeconds = 2, TimingQuality = MeetingTimingQuality.Aligned }).ToArray()
        };
        var reports = new List<EvidenceReport>();
        await using var evidence = Create(aligner: aligner, reports: reports);
        evidence.Append(new byte[5 * 32000]);
        evidence.RefineChunkTurns([Turn(0, 3)], [], 5 * 16000);
        Assert.Equal(1, aligner.InitializeCalls);
        Assert.Equal(1, aligner.AlignCalls);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        aligner.Align = _ =>
        {
            entered.TrySetResult();
            if (!aligner.Disposal.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Aligner was not disposed");
            throw new ObjectDisposedException("aligner");
        };
        var original = Turn(2, 4) with { Text = "New speech" };
        MeetingTurn[] turns = [original];

        var refining = Task.Run(() => evidence.RefineChunkTurns(turns, [], 5 * 16000));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await evidence.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await refining.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, aligner.AlignCalls);
        Assert.DoesNotContain(reports, report =>
            report.Kind == MeetingDiagnosticKind.RefinementAvailability && !report.Available);
        Assert.Equal(original, Assert.Single(turns));
    }

    [Fact]
    public async Task ObserverStopFailureStillDrainsReleasesActivityAndRemovesAutomaticNames()
    {
        var error = new InvalidOperationException("worker faulted");
        var observer = new FakeSpeakerObserver { StopFailure = error };
        var reports = new List<EvidenceReport>();
        await using var evidence = Create(reports: reports, observer: () => observer);
        var source = new EvidenceSource();
        await evidence.AttachAsync(source, 0);
        source.Raise(new(0, 9 * 16000, 0, 0.2));
        for (var second = 0; second <= 7; second++) observer.Report(second * 16000, "Alex");
        observer.Report(8 * 16000, "Blair");
        source.Clock = 9 * 16000;
        MeetingTurn[] turns = [
            Turn(0, 1, MeetingTimingQuality.Aligned),
            Turn(2, 3, MeetingTimingQuality.Aligned),
            Turn(7, 9, MeetingTimingQuality.Aligned)
        ];
        evidence.RefineTimedTurns(turns, []);
        var document = new MeetingDocument { Turns = turns };
        Assert.Equal("Alex", evidence.NameSpeakers(document).GetSpeakerName("Speaker 0"));
        var drained = false;

        await evidence.DetachAsync(() =>
        {
            Assert.False(observer.Running);
            drained = true;
            return Task.CompletedTask;
        });

        Assert.True(drained);
        Assert.Equal(0, source.Subscribers);
        Assert.Empty(evidence.NameSpeakers(document).AutoSpeakerNames);
        var failure = Assert.Single(reports, report =>
            report.Kind == MeetingDiagnosticKind.NamingAvailability && !report.Available);
        Assert.Same(error, failure.Error);
        observer.StopFailure = null;
        var next = new EvidenceSource();
        await evidence.AttachAsync(next, 9 * 16000);
        Assert.Equal(2, observer.Starts);
        Assert.Equal(1, next.Subscribers);
        await evidence.DetachAsync(() => Task.CompletedTask);
        Assert.Equal(0, next.Subscribers);
    }

    [Fact]
    public async Task ObserverDisposeFailureIsContained()
    {
        var observer = new FakeSpeakerObserver { DisposeFailure = new InvalidOperationException("Dispose failed") };
        var evidence = Create(observer: () => observer);
        var source = new EvidenceSource();
        await evidence.AttachAsync(source, 0);
        await evidence.DetachAsync(() => Task.CompletedTask);

        await evidence.DisposeAsync();

        Assert.False(evidence.HasResources);
        Assert.True(observer.Disposed);
        Assert.Equal(0, source.Subscribers);
    }

    [Theory]
    [InlineData("wrong count")]
    [InlineData("changed text")]
    [InlineData("changed speaker")]
    [InlineData("null item")]
    [InlineData("NaN start")]
    [InlineData("NaN end")]
    [InlineData("infinite start")]
    [InlineData("infinite end")]
    [InlineData("end beyond clip")]
    [InlineData("reversed bounds")]
    [InlineData("negative start")]
    public async Task InvalidAlignerOutputKeepsChunkTimingAndLatches(string invalid)
    {
        var aligner = new FakeWordAligner
        {
            Align = candidates =>
            {
                if (invalid == "wrong count") return [];
                var aligned = candidates.Select(turn => turn with
                    { StartSeconds = 1, EndSeconds = 2, TimingQuality = MeetingTimingQuality.Aligned }).ToArray();
                aligned[1] = invalid switch
                {
                    "changed text" => aligned[1] with { Text = "Wrong text" },
                    "changed speaker" => aligned[1] with { SpeakerId = "Speaker 1" },
                    "null item" => null!,
                    "NaN start" => aligned[1] with { StartSeconds = double.NaN },
                    "NaN end" => aligned[1] with { EndSeconds = double.NaN },
                    "infinite start" => aligned[1] with { StartSeconds = double.PositiveInfinity },
                    "infinite end" => aligned[1] with { EndSeconds = double.PositiveInfinity },
                    "end beyond clip" => aligned[1] with { EndSeconds = 10 },
                    "reversed bounds" => aligned[1] with { StartSeconds = 3 },
                    "negative start" => aligned[1] with { StartSeconds = -1 },
                    _ => throw new ArgumentOutOfRangeException(nameof(invalid))
                };
                return aligned;
            }
        };
        var reports = new List<EvidenceReport>();
        await using var evidence = Create(aligner: aligner, reports: reports);
        evidence.Append(new byte[5 * 32000]);
        MeetingTurn[] originals = [Turn(0, 2), Turn(2, 4) with { Text = "Second speech" }];
        var turns = originals.ToArray();

        evidence.RefineChunkTurns(turns, [], 5 * 16000);
        Assert.Equal(originals, turns);
        evidence.RefineChunkTurns(turns, [], 5 * 16000);

        Assert.Equal(originals, turns);
        Assert.Equal(1, aligner.AlignCalls);
        var failure = Assert.Single(reports, report => !report.Available);
        Assert.Equal(MeetingDiagnosticKind.RefinementAvailability, failure.Kind);
        Assert.IsType<InvalidDataException>(failure.Error);
    }

    [Fact]
    public async Task ChunkRefinementCarriesForwardMatchingAlignedTurnsWithoutRealigning()
    {
        var aligner = new FakeWordAligner
        {
            Align = candidates => candidates.Select(turn => turn with
                { StartSeconds = 3, EndSeconds = 4, TimingQuality = MeetingTimingQuality.Aligned }).ToArray()
        };
        await using var evidence = Create(aligner: aligner);
        evidence.Append(new byte[5 * 32000]);
        var previous = Turn(1, 2, MeetingTimingQuality.Aligned) with { Source = MeetingAudioSource.Playback };
        var changed = Turn(2, 5) with { Text = "Changed speech" };
        MeetingTurn[] turns = [Turn(0, 3) with { IsProvisional = true }, changed];

        evidence.RefineChunkTurns(turns, [previous], 5 * 16000);

        Assert.Equal(previous with { IsProvisional = true }, turns[0]);
        Assert.Equal(1, aligner.AlignCalls);
        Assert.Equal(changed, Assert.Single(Assert.Single(aligner.Inputs)));
        Assert.Equal(changed with
            { StartSeconds = 3, EndSeconds = 4, TimingQuality = MeetingTimingQuality.Aligned }, turns[1]);
    }

    private static MeetingSpeakerEvidence Create(FakeWordAligner? aligner = null,
        List<EvidenceReport>? reports = null, CancellationToken token = default,
        Func<IMeetingSpeakerObserver>? observer = null, int? processId = 42) =>
        new(observer, aligner is null ? null : () => aligner, processId, (kind, available, error) => reports?.Add(new(kind, available, error)), token);

    private static MeetingTurn Turn(double start, double end, MeetingTimingQuality quality = MeetingTimingQuality.Chunk) =>
        new("Speaker 0", start, end, "Speech") { TimingQuality = quality };
}
