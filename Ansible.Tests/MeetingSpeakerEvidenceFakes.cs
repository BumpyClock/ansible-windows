using Ansible.Core;

namespace Ansible.Tests;

internal sealed record EvidenceReport(MeetingDiagnosticKind Kind, bool Available, Exception? Error = null);

internal sealed class EvidenceSource : IMeetingAudioSourceMetadata
{
    private Action<MeetingAudioActivity>? _activity;
    public long Clock { get; set; }
    public long ClockSamplePosition => Clock;
    public int Subscribers => _activity?.GetInvocationList().Length ?? 0;
    public event Action<MeetingAudioActivity>? Activity { add => _activity += value; remove => _activity -= value; }
    public void Raise(MeetingAudioActivity activity) => _activity?.Invoke(activity);
}

internal sealed class FakeSpeakerObserver : IMeetingSpeakerObserver
{
    private Action<MeetingSpeakerObservation>? _report;
    public Exception? StartFailure { get; set; }
    public Exception? StopFailure { get; set; }
    public Exception? DisposeFailure { get; set; }
    public int? ProcessId { get; private set; }
    public Func<long>? Position { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public bool Running { get; private set; }
    public bool Disposed { get; private set; }

    public Task StartAsync(int processId, Func<long> position, Action<MeetingSpeakerObservation> report, CancellationToken token)
    {
        Assert.False(Running);
        if (StartFailure is { } failure) return Task.FromException(failure);
        ProcessId = processId;
        Position = position;
        _report = report;
        Running = true;
        Starts++;
        return Task.CompletedTask;
    }

    public void Report(long sample, string name) => _report!(new(sample, [name], null, MeetingSpeakerReadStatus.Readable));

    public Task StopAsync()
    {
        Running = false;
        Stops++;
        return StopFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Running = false;
        Disposed = true;
        return DisposeFailure is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
    }
}

internal sealed class FakeWordAligner : IMeetingWordAligner
{
    public Func<CancellationToken, Task>? Initialize { get; set; }
    public Func<IReadOnlyList<MeetingTurn>, IReadOnlyList<MeetingTurn>>? Align { get; set; }
    public int InitializeCalls { get; private set; }
    public int AlignCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public int DisposeFailures { get; set; }
    public List<IReadOnlyList<MeetingTurn>> Inputs { get; } = [];
    public List<int> SampleCounts { get; } = [];
    public SynchronizationContext? DisposalContext { get; private set; }
    public TaskCompletionSource Disposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task InitializeAsync(CancellationToken token)
    {
        InitializeCalls++;
        return Initialize?.Invoke(token) ?? Task.CompletedTask;
    }

    public Task<IReadOnlyList<MeetingTurn>> AlignAsync(WaveAudio audio, IReadOnlyList<MeetingTurn> turns, CancellationToken token)
    {
        AlignCalls++;
        Inputs.Add(turns.ToArray());
        SampleCounts.Add(audio.Samples.Length);
        return Task.FromResult(Align?.Invoke(turns) ?? turns);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        DisposalContext = SynchronizationContext.Current;
        if (DisposeFailures-- > 0) return ValueTask.FromException(new InvalidOperationException("Still owned"));
        Disposal.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
