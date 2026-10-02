using System.Threading.Channels;

namespace DictationPoc.Core;

public sealed class DictationSession
{
    private readonly object _gate = new();
    private readonly Func<string, IRecognitionEngine> _engineFactory;
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly IAudioInputReader _input;
    private readonly IUsageStore _usageStore;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private IRecognitionEngine? _engine;
    private Operation? _operation;
    private Operation? _recovery;
    private Task? _closeTask;
    private bool _closing;
    private long _version;
    private TimeSpan _lastElapsed;
    private SessionSnapshot _state;

    public DictationSession(
        Func<string, IRecognitionEngine> engineFactory, IAudioCaptureFactory captureFactory,
        IAudioInputReader input, IUsageStore usageStore, string modelsDirectory,
        TimeProvider? time = null, TimeSpan? operationTimeout = null)
    {
        _engineFactory = engineFactory;
        _captureFactory = captureFactory;
        _input = input;
        _usageStore = usageStore;
        _time = time ?? TimeProvider.System;
        _timeout = operationTimeout ?? TimeSpan.FromMinutes(5);
        _state = new SessionSnapshot
        {
            Version = 0, Phase = DictationPhase.Disconnected, Activity = SessionActivity.None,
            Notice = new(NoticeKind.Information, "On-device", "Recognition runs locally through the native backend."),
            Models = [], SelectedIndex = -1, ModelsDirectory = modelsDirectory,
            BackendVersion = "", Language = "", Transcript = ""
        };
    }

    public event Action<SessionSnapshot>? StateChanged;
    public event Action<double>? AudioLevelChanged;
    public SessionSnapshot State { get { lock (_gate) { return _state; } } }
    public string UsagePath => _usageStore.Path;
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _operation?.StartedAt is { } start ? _time.GetElapsedTime(start) : _lastElapsed;
            }
        }
    }

    public Task<SessionOutcome> InitializeAsync() =>
        Begin(SessionActivity.Connecting, State.ModelsDirectory, true);
    public Task<SessionOutcome> ConnectAsync(string directory) =>
        Begin(SessionActivity.Connecting, directory, false);
    public Task<SessionOutcome> StartDictationAsync() => Begin(SessionActivity.Dictation);
    public Task<SessionOutcome> ReplayAsync(string path) => Begin(SessionActivity.Replay, path);
    public Task<SessionOutcome> TranscribeFileAsync(string path) => Begin(SessionActivity.File, path);
    public Task<SessionOutcome> SetUsageEnabledAsync(bool enabled) =>
        Begin(SessionActivity.Preferences, preference: enabled);

    public void SelectModel(int index)
    {
        SessionSnapshot state;
        lock (_gate)
        {
            RequireIdle();
            if (_state.Phase != DictationPhase.Ready || index < 0 || index >= _state.Models.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Choose an installed model.");
            }
            state = SetState(_state with
            {
                SelectedIndex = index,
                Notice = new(NoticeKind.Information, "Model selected",
                    _state.Models[index].Mode == "offline" ? "Use WAV verification with this offline model." :
                    _state.Models[index].Preview == "final-only" ? "This adapter returns text after Finish." :
                    "Actual model updates appear in the floating preview.")
            });
        }
        Publish(state);
    }

    public void SetLanguage(string language)
    {
        SessionSnapshot state;
        lock (_gate)
        {
            RequireIdle();
            state = SetState(_state with { Language = language.Trim() });
        }
        Publish(state);
    }

    public void Notify(NoticeKind kind, string title, string message)
    {
        SessionSnapshot state;
        lock (_gate)
        {
            if (_closing) { return; }
            state = SetState(_state with { Notice = new(kind, title, message) });
        }
        Publish(state);
    }

    public void ReportUiError(Exception error) => Notify(NoticeKind.Error, "Operation failed", error.Message);

    private Task<SessionOutcome> Begin(
        SessionActivity activity, string? path = null, bool initializeUsage = false, bool? preference = null)
    {
        Operation operation;
        SessionSnapshot state;
        lock (_gate)
        {
            RequireIdle();
            var model = _state.SelectedModel;
            if (activity is SessionActivity.Dictation or SessionActivity.Replay or SessionActivity.File)
            {
                if (_engine is null || model is null ||
                    activity is SessionActivity.Dictation or SessionActivity.Replay && model.Mode != "streaming")
                {
                    throw new InvalidOperationException("Choose an installed model that supports this recognition mode.");
                }
            }
            operation = new Operation(activity, model, _state.Language, path, initializeUsage, preference, _timeout, _time);
            _operation = operation;
            _lastElapsed = TimeSpan.Zero;
            state = SetState(_state with
            {
                Activity = activity,
                Phase = activity switch
                {
                    SessionActivity.Connecting => DictationPhase.Connecting,
                    SessionActivity.File => DictationPhase.Transcribing,
                    SessionActivity.Preferences => DictationPhase.UpdatingPreferences,
                    _ => DictationPhase.Preparing
                },
                Transcript = activity is SessionActivity.Connecting or SessionActivity.Preferences ? _state.Transcript : "",
                Result = activity is SessionActivity.Connecting or SessionActivity.Preferences ? _state.Result : null,
                Notice = new(NoticeKind.Information, "Preparing operation",
                    "New work is admitted only when the current session has released its resources.")
            });
            operation.Work = Task.Run(() => ExecuteAsync(operation));
        }
        Publish(state);
        return operation.Completion.Task;
    }

    private async Task ExecuteAsync(Operation operation)
    {
        var errors = new List<Exception>();
        var kind = SessionOutcomeKind.Completed;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            switch (operation.Activity)
            {
                case SessionActivity.Connecting: await ConnectCoreAsync(operation); break;
                case SessionActivity.Preferences: await UpdatePreferenceAsync(operation); break;
                case SessionActivity.File: await RecognizeFileAsync(operation); break;
                default: await RecognizeLiveAsync(operation); break;
            }
        }
        catch (CaptureOwnershipException error)
        {
            operation.Capture = error.Capture;
            errors.Add(error);
            kind = SessionOutcomeKind.Failed;
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            kind = operation.CancellationRequested ? SessionOutcomeKind.Cancelled : SessionOutcomeKind.TimedOut;
        }
        catch (Exception error)
        {
            errors.Add(error);
            kind = SessionOutcomeKind.Failed;
        }

        await ReleaseOperationAsync(operation, errors);
        if (operation.CommandError is not null) { AddError(errors, operation.CommandError); }
        if (errors.Count != 0) { kind = SessionOutcomeKind.Failed; }
        var elapsed = operation.StartedAt is { } start ? _time.GetElapsedTime(start) : TimeSpan.Zero;
        if (kind == SessionOutcomeKind.Completed && operation.Result is not null)
        {
            await RecordUsageAsync(operation, elapsed);
        }
        var errorResult = errors.Count switch
        {
            0 => null,
            1 => errors[0],
            _ => new AggregateException("Recognition and cleanup reported failures.", errors)
        };
        var outcome = new SessionOutcome(kind, operation.Result, errorResult);
        SessionSnapshot state;
        lock (_gate)
        {
            if (!ReferenceEquals(_operation, operation))
            {
                throw new InvalidOperationException("The operation owner changed before terminal publication.");
            }
            _lastElapsed = elapsed;
            if (operation.Capture?.IsReleased == false || operation.Candidate is not null || operation.Previous is not null)
            {
                _recovery = operation;
            }
            _operation = null;
            var notice = kind switch
            {
                SessionOutcomeKind.Cancelled => new SessionNotice(NoticeKind.Warning, "Cancelled", "Partial text is retained; this attempt is not counted."),
                SessionOutcomeKind.TimedOut => new(NoticeKind.Warning, "Timed out", "The operation exceeded its deadline and is not counted."),
                SessionOutcomeKind.Failed => new(NoticeKind.Error, "Operation failed", errorResult?.Message ?? "The operation failed."),
                _ when operation.UsageWarning is not null => new(NoticeKind.Warning, "Transcript ready; statistics unavailable", operation.UsageWarning),
                _ when operation.Result is not null => new(NoticeKind.Success, "Transcript ready", "Recognition completed and owned resources were released."),
                _ when operation.Activity == SessionActivity.Connecting => new(NoticeKind.Success, "Native backend ready", $"{_state.Models.Count} installed models; one resident model at a time."),
                _ => new(NoticeKind.Success, "Preference saved", "Local collection preferences were updated.")
            };
            state = SetState(_state with
            {
                Phase = _closing ? DictationPhase.Closing : _recovery is not null ? DictationPhase.RecoveryRequired :
                    _engine is null ? DictationPhase.Disconnected : DictationPhase.Ready,
                Notice = _recovery is not null ? new(NoticeKind.Error, "Cleanup requires recovery",
                    "Native resources remain owned. Close the app to retry release; new work is blocked.") : notice
            });
        }
        if (_recovery is null) { operation.Dispose(); }
        operation.Completion.TrySetResult(outcome);
        Publish(state);
        AudioLevelChanged?.Invoke(0);
    }

    private async Task ConnectCoreAsync(Operation operation)
    {
        if (operation.InitializeUsage)
        {
            try
            {
                var usage = await _usageStore.LoadAsync(operation.Token);
                UpdateCurrent(operation, state => state with { Usage = usage, UsageError = null });
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                UpdateCurrent(operation, state => state with { UsageError = $"Local statistics could not be read: {error.Message}" });
            }
        }
        operation.Token.ThrowIfCancellationRequested();
        var candidate = _engineFactory(operation.Path!);
        operation.Candidate = candidate;
        var models = await candidate.ConnectAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        IRecognitionEngine? previous;
        lock (_gate)
        {
            EnsureCurrent(operation);
            previous = _engine;
            operation.Previous = previous;
            _engine = null;
        }
        if (previous is not null)
        {
            await previous.DisposeAsync();
            operation.Previous = null;
        }
        operation.Token.ThrowIfCancellationRequested();
        SessionSnapshot state;
        lock (_gate)
        {
            EnsureCurrent(operation);
            _engine = candidate;
            operation.Candidate = null;
            state = SetState(_state with
            {
                Models = Array.AsReadOnly(models.ToArray()), SelectedIndex = models.Count == 0 ? -1 : 0,
                ModelsDirectory = candidate.ModelsDirectory, BackendVersion = candidate.Version
            });
        }
        Publish(state);
    }

    private async Task UpdatePreferenceAsync(Operation operation)
    {
        var usage = await _usageStore.SetEnabledAsync(operation.Preference!.Value, operation.Token);
        UpdateCurrent(operation, state => state with { Usage = usage, UsageError = null });
    }

    private async Task RecognizeFileAsync(Operation operation)
    {
        var recording = await _input.ReadRecordingAsync(operation.Path!, operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        operation.StartedAt = _time.GetTimestamp();
        operation.Result = await _engine!.TranscribeAsync(
            operation.Model!, recording, operation.Language, Progress(operation), operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        UpdateResult(operation);
    }

    private async Task RecognizeLiveAsync(Operation operation)
    {
        byte[]? replay = null;
        if (operation.Activity == SessionActivity.Replay)
        {
            replay = await _input.ReadReplayAsync(operation.Path!, operation.Token);
        }
        operation.Token.ThrowIfCancellationRequested();
        operation.Pipe = Channel.CreateBounded<byte[]>(16);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.Inference = _engine!.StreamAsync(operation.Model!, operation.Pipe.Reader, operation.Language,
            Progress(operation), operation.Token, () => ready.TrySetResult());
        if (await Task.WhenAny(ready.Task, operation.Inference) == operation.Inference)
        {
            await operation.Inference;
            throw new InvalidOperationException("The native stream completed before audio input was admitted.");
        }
        await ready.Task.WaitAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        lock (_gate) { EnsureCurrent(operation); }
        if (replay is null)
        {
            operation.Capture = await _captureFactory.StartAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            lock (_gate) { EnsureCurrent(operation); }
            operation.LevelHandler = value => PublishLevel(operation, value);
            operation.Capture.LevelChanged += operation.LevelHandler;
            operation.Producer = ForwardAsync(operation.Capture.Audio, operation.Pipe.Writer, operation.Token);
        }
        else
        {
            operation.Producer = ReplayAsync(operation, replay);
        }
        operation.StartedAt = _time.GetTimestamp();
        UpdateCurrent(operation, state => state with
        {
            Phase = DictationPhase.Recording,
            Notice = new(NoticeKind.Information, replay is null ? "Listening" : "Live verification",
                replay is null ? "Audio stays local. Finish when you are done." : "A WAV recording feeds the native stream; no microphone is open.")
        });
        operation.Result = await operation.Inference;
        operation.Token.ThrowIfCancellationRequested();
        await operation.Producer;
        operation.RecordingSeconds = operation.Capture?.CapturedSeconds ?? 0;
        UpdateResult(operation);
    }

    private async Task ReleaseOperationAsync(Operation operation, List<Exception> errors)
    {
        operation.Cancellation.Cancel();
        operation.Pipe?.Writer.TryComplete();
        if (operation.Capture is not null)
        {
            if (operation.LevelHandler is not null) { operation.Capture.LevelChanged -= operation.LevelHandler; }
            try { await operation.Capture.DisposeAsync(); }
            catch (Exception error) { AddError(errors, error); }
        }
        await ObserveAsync(operation.Producer, operation.Token, errors);
        await ObserveAsync(operation.Inference, operation.Token, errors);
        if (operation.Candidate is not null)
        {
            try { await operation.Candidate.DisposeAsync(); operation.Candidate = null; }
            catch (Exception error) { AddError(errors, error); }
        }
        if (operation.Previous is not null)
        {
            try { await operation.Previous.DisposeAsync(); operation.Previous = null; }
            catch (Exception error) { AddError(errors, error); }
        }
    }

    private async Task RecordUsageAsync(Operation operation, TimeSpan elapsed)
    {
        if (State.Usage?.Enabled != true) { return; }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var usage = await _usageStore.RecordAsync(new UsageEntry
            {
                Id = operation.Id, CompletedAt = _time.GetUtcNow(), ModelId = operation.Model!.Id,
                Source = operation.Activity == SessionActivity.Dictation ? UsageSource.Dictation : UsageSource.File,
                Words = operation.Result!.SpokenWords, RecordingSeconds = operation.RecordingSeconds,
                ElapsedSeconds = elapsed.TotalSeconds
            }, deadline.Token);
            UpdateFacts(state => state with { Usage = usage, UsageError = null });
        }
        catch (Exception error)
        {
            operation.UsageWarning = $"The transcript succeeded, but local counts could not be saved: {error.Message}";
            UpdateFacts(state => state with { UsageError = operation.UsageWarning });
        }
    }

    public async Task FinishAsync()
    {
        Operation? operation;
        SessionSnapshot state;
        lock (_gate)
        {
            operation = _operation;
            if (_closing || operation?.Activity != SessionActivity.Dictation || !_state.CanFinish) { return; }
            state = SetState(_state with
            {
                Phase = DictationPhase.Finishing,
                Notice = new(NoticeKind.Information, "Finishing", "Capture stops while the final native step completes.")
            });
        }
        Publish(state);
        try { if (operation.Capture is not null) { await operation.Capture.StopAsync(); } }
        catch (Exception error) { operation.CommandError = error; operation.RequestCancellation(); }
    }

    public async Task CancelAsync()
    {
        Operation? operation;
        SessionSnapshot? state = null;
        lock (_gate)
        {
            operation = _operation;
            if (operation is not null && !_closing)
            {
                state = SetState(_state with
                {
                    Phase = DictationPhase.Cancelling,
                    Notice = new(NoticeKind.Warning, "Cancellation requested",
                        "The current native step must return before its state can be released.")
                });
            }
        }
        operation?.RequestCancellation();
        if (state is not null) { Publish(state); }
        if (operation?.Capture is not null)
        {
            try { await operation.Capture.StopAsync(); }
            catch (Exception error) { operation.CommandError = error; }
        }
    }

    public Task CloseAsync()
    {
        Operation? operation;
        SessionSnapshot? state = null;
        Task close;
        lock (_gate)
        {
            if (_closeTask is not null) { return _closeTask; }
            _closing = true;
            operation = _operation;
            state = SetState(_state with { Phase = DictationPhase.Closing,
                Notice = new(NoticeKind.Information, "Closing", "Joining owned work and releasing native resources.") });
            close = _closeTask = Task.Run(CloseCoreAsync);
        }
        operation?.RequestCancellation();
        Publish(state);
        return close;
    }

    private async Task CloseCoreAsync()
    {
        try
        {
            Operation? operation;
            lock (_gate) { operation = _operation; }
            if (operation is not null) { await operation.Completion.Task; }
            Operation? recovery;
            lock (_gate) { recovery = _recovery; }
            if (recovery?.Capture is not null)
            {
                await recovery.Capture.DisposeAsync();
                if (!recovery.Capture.IsReleased) { throw new InvalidOperationException("Capture resources remain owned after release."); }
            }
            if (recovery?.Candidate is not null)
            {
                await recovery.Candidate.DisposeAsync();
                recovery.Candidate = null;
            }
            if (recovery?.Previous is not null)
            {
                await recovery.Previous.DisposeAsync();
                recovery.Previous = null;
            }
            if (recovery is not null)
            {
                recovery.Dispose();
                lock (_gate) { _recovery = null; }
            }
            if (_engine is not null) { await _engine.DisposeAsync(); _engine = null; }
            SessionSnapshot state;
            lock (_gate) { state = SetState(_state with { Phase = DictationPhase.Closed, Activity = SessionActivity.None }); }
            Publish(state);
        }
        catch (Exception error)
        {
            SessionSnapshot state;
            lock (_gate)
            {
                _closeTask = null;
                state = SetState(_state with
                {
                    Notice = new(NoticeKind.Error, "Close requires recovery",
                        $"Native resources remain owned. Close again to retry release. {error.Message}")
                });
            }
            Publish(state);
            throw;
        }
    }

    private static async Task ObserveAsync(Task? task, CancellationToken token, List<Exception> errors)
    {
        if (task is null) { return; }
        try { await task; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { AddError(errors, error); }
    }

    private static void AddError(List<Exception> errors, Exception error)
    {
        if (!errors.Contains(error)) { errors.Add(error); }
    }

    private static async Task ForwardAsync(ChannelReader<byte[]> input, ChannelWriter<byte[]> output, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            await foreach (var bytes in input.ReadAllAsync(token)) { await output.WriteAsync(bytes, token); }
        }
        catch (Exception error) { failure = error; throw; }
        finally { output.TryComplete(failure); }
    }

    private async Task ReplayAsync(Operation operation, byte[] pcm)
    {
        Exception? failure = null;
        try
        {
            for (var offset = 0; offset < pcm.Length; offset += 1280)
            {
                var bytes = pcm.AsSpan(offset, Math.Min(1280, pcm.Length - offset)).ToArray();
                await operation.Pipe!.Writer.WriteAsync(bytes, operation.Token);
                PublishLevel(operation, AudioMeter.Pcm16Level(bytes));
                await Task.Delay(TimeSpan.FromMilliseconds(40), _time, operation.Token);
            }
            UpdateCurrent(operation, state => state with { Phase = DictationPhase.Finishing });
        }
        catch (Exception error) { failure = error; throw; }
        finally { operation.Pipe!.Writer.TryComplete(failure); }
    }

    private IProgress<TranscriptUpdate> Progress(Operation operation) => new InlineProgress<TranscriptUpdate>(update =>
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_operation, operation) || _closing || operation.Token.IsCancellationRequested || operation.HasFinal) { return; }
            operation.HasFinal = update.IsFinal;
        }
        UpdateCurrent(operation, state => state with { Transcript = update.Text });
    });

    private void UpdateResult(Operation operation) =>
        UpdateCurrent(operation, state => state with { Transcript = operation.Result!.DisplayText, Result = operation.Result });

    private void PublishLevel(Operation operation, double level)
    {
        lock (_gate)
        {
            if (_closing || !ReferenceEquals(_operation, operation) || _state.Phase != DictationPhase.Recording) { return; }
        }
        AudioLevelChanged?.Invoke(level);
    }

    private void UpdateCurrent(Operation operation, Func<SessionSnapshot, SessionSnapshot> update)
    {
        SessionSnapshot state;
        lock (_gate) { EnsureCurrent(operation); state = SetState(update(_state)); }
        Publish(state);
    }

    private void UpdateFacts(Func<SessionSnapshot, SessionSnapshot> update)
    {
        SessionSnapshot state;
        lock (_gate) { state = SetState(update(_state)); }
        Publish(state);
    }

    private void EnsureCurrent(Operation operation)
    {
        operation.Token.ThrowIfCancellationRequested();
        if (_closing || !ReferenceEquals(_operation, operation)) { throw new OperationCanceledException(operation.Token); }
    }

    private void RequireIdle()
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        if (_operation is not null || _recovery is not null)
        {
            throw new InvalidOperationException("The current operation must release its resources before new work starts.");
        }
    }

    private SessionSnapshot SetState(SessionSnapshot state) => _state = state with { Version = ++_version };
    private void Publish(SessionSnapshot state) => StateChanged?.Invoke(state);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class Operation : IDisposable
    {
        private readonly object _cancellationGate = new();
        private bool _disposed;
        private int _cancellationRequested;
        public Operation(
            SessionActivity activity, AudioModel? model, string language, string? path,
            bool initializeUsage, bool? preference, TimeSpan timeout, TimeProvider time)
        {
            Activity = activity; Model = model; Language = language; Path = path;
            InitializeUsage = initializeUsage; Preference = preference;
            Cancellation = new CancellationTokenSource(timeout, time);
        }

        public Guid Id { get; } = Guid.NewGuid();
        public SessionActivity Activity { get; }
        public AudioModel? Model { get; }
        public string Language { get; }
        public string? Path { get; }
        public bool InitializeUsage { get; }
        public bool? Preference { get; }
        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token => Cancellation.Token;
        public bool CancellationRequested => Volatile.Read(ref _cancellationRequested) != 0;
        public TaskCompletionSource<SessionOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Work { get; set; }
        public IRecognitionEngine? Candidate { get; set; }
        public IRecognitionEngine? Previous { get; set; }
        public IAudioCapture? Capture { get; set; }
        public Action<double>? LevelHandler { get; set; }
        public Channel<byte[]>? Pipe { get; set; }
        public Task? Producer { get; set; }
        public Task<RecognitionResult>? Inference { get; set; }
        public RecognitionResult? Result { get; set; }
        public bool HasFinal { get; set; }
        public long? StartedAt { get; set; }
        public double RecordingSeconds { get; set; }
        public string? UsageWarning { get; set; }
        public Exception? CommandError { get; set; }
        public void RequestCancellation()
        {
            lock (_cancellationGate)
            {
                if (_disposed) { return; }
                Interlocked.Exchange(ref _cancellationRequested, 1);
                Cancellation.Cancel();
            }
        }
        public void Dispose()
        {
            lock (_cancellationGate)
            {
                if (_disposed) { return; }
                Cancellation.Dispose();
                _disposed = true;
            }
        }
    }
}
