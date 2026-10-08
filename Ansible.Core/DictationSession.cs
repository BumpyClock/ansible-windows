using System.Threading.Channels;

namespace Ansible.Core;

public sealed class DictationSession
{
    private readonly object _gate = new();
    private readonly Func<string, IRecognitionEngine> _engineFactory;
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly IAudioInputReader _input;
    private readonly IUsageStore _usageStore;
    private readonly IAppSettingsStore _settingsStore;
    private readonly SessionNotice? _initialSettingsWarning;
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
        IAppSettingsStore settingsStore, TimeProvider? time = null, TimeSpan? operationTimeout = null)
    {
        _engineFactory = engineFactory;
        _captureFactory = captureFactory;
        _input = input;
        _usageStore = usageStore;
        ArgumentNullException.ThrowIfNull(settingsStore);
        _settingsStore = settingsStore;
        _time = time ?? TimeProvider.System;
        _timeout = operationTimeout ?? TimeSpan.FromMinutes(5);
        var settings = new AppSettings();
        try
        {
            var loaded = settingsStore.Load();
            loaded.Validate();
            settings = loaded;
        }
        catch (Exception error)
        {
            _initialSettingsWarning = new(NoticeKind.Warning, "Settings unavailable",
                $"Saved settings could not be read. Defaults apply only to this session; " +
                $"the saved document remains unchanged: {error.Message}");
        }
        _state = new SessionSnapshot
        {
            Version = 0, Phase = DictationPhase.Disconnected, Activity = SessionActivity.None,
            Notice = new(NoticeKind.Information, "On-device", "Recognition runs locally through the native backend."),
            Models = [], SelectedIndex = -1, ModelsDirectory = settings.ModelsDirectory ?? modelsDirectory,
            BackendVersion = "", Settings = settings, Transcript = ""
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
    public Task<SessionOutcome> StartDictationAsync(
        IProgress<TranscriptUpdate>? transcriptUpdates = null) =>
        Begin(SessionActivity.Dictation, transcriptUpdates: transcriptUpdates);
    public Task<SessionOutcome> ReplayAsync(string path) => Begin(SessionActivity.Replay, path);
    public Task<SessionOutcome> TranscribeFileAsync(string path) => Begin(SessionActivity.File, path);
    public void SetUsageEnabled(bool enabled) =>
        ChangeSettings(state => state with { Settings = state.Settings with { CollectUsage = enabled } },
            new(NoticeKind.Success, "Settings saved", "Local usage collection was updated."));
    public void SetShortcut(DictationShortcut shortcut) =>
        ChangeSettings(state => state with { Settings = state.Settings with { Shortcut = shortcut } },
            new(NoticeKind.Success, "Shortcut updated", $"Use {shortcut.DisplayText} in another app."));
    public void SetPushToTalk(bool enabled) =>
        ChangeSettings(state => state with { Settings = state.Settings with { PushToTalk = enabled } },
            new(NoticeKind.Success, "Activation mode updated", enabled ? "Hold the shortcut to dictate." : "Press the shortcut to start and finish."));
    public void SetInsertionMethod(TextInsertionMethod method) =>
        ChangeSettings(state => state with { Settings = state.Settings with { InsertionMethod = method } },
            new(NoticeKind.Success, "Text insertion updated", method == TextInsertionMethod.Paste
                ? "Dictated text is pasted. Your clipboard is restored after each paste."
                : "Dictated text is typed one character at a time."));
    public void SetTypingGap(int milliseconds) =>
        ChangeSettings(state => state with { Settings = state.Settings with { TypingGapMilliseconds = milliseconds } },
            new(NoticeKind.Success, "Typing gap updated", $"Typed characters are sent {milliseconds} ms apart."));
    public Task<SessionOutcome> MaintainModelsAsync(
        Func<CancellationToken, Task> maintenance, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        return Begin(SessionActivity.ModelMaintenance, directory ?? State.ModelsDirectory, maintenance: maintenance);
    }

    public void SelectModel(int index)
    {
        ChangeSettings(state =>
        {
            if (state.Phase != DictationPhase.Ready || index < 0 || index >= state.Models.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Choose an installed model.");
            }
            return state with
            {
                SelectedIndex = index,
                Settings = state.Settings with { ModelId = state.Models[index].Id }
            };
        }, new(NoticeKind.Information, "Model selected", "The selected model applies to the next recognition."));
    }

    public void SetLanguage(string language) =>
        ChangeSettings(state => state with { Settings = state.Settings with { Language = language.Trim() } },
            new(NoticeKind.Success, "Language hint updated", "The language hint applies to the next recognition."));

    public void SetBackend(NativeBackend backend) =>
        ChangeSettings(state => state with { Settings = state.Settings with { Backend = backend } },
            new(NoticeKind.Success, "Recognition processor updated", "The selected processor applies to the next recognition."));

    public void SetCustomDictionary(string text) =>
        ChangeSettings(state => state with
        {
            Settings = state.Settings with { CustomDictionary = CustomVocabulary.Normalize(text) }
        }, new(NoticeKind.Success, "Dictionary updated", "Vocabulary hints apply to the next recognition with a supported model."));

    private void ChangeSettings(Func<SessionSnapshot, SessionSnapshot> change, SessionNotice notice)
    {
        SessionSnapshot state;
        lock (_gate)
        {
            RequireIdle();
            var updated = change(_state);
            updated.Settings.Validate();
            state = SetState(updated with { Notice = SaveSettings(updated.Settings) ?? notice });
        }
        Publish(state);
    }

    private SessionNotice? SaveSettings(AppSettings settings)
    {
        if (_initialSettingsWarning is not null)
            return new(NoticeKind.Warning, "Settings not saved",
                "The change applies only to this session because the saved settings could not be read at startup. " +
                "Resolve the read error and restart the app before saving settings. The saved document remains unchanged.");
        try { _settingsStore.Save(settings); return null; }
        catch (Exception error)
        {
            return new(NoticeKind.Warning, "Settings not saved",
                $"The change applies to this session, but could not be saved for restart: {error.Message}");
        }
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
        SessionActivity activity, string? path = null, bool initializeUsage = false,
        Func<CancellationToken, Task>? maintenance = null,
        IProgress<TranscriptUpdate>? transcriptUpdates = null)
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
            var options = new RecognitionOptions(_state.Language,
                model?.SupportsCustomDictionary == true ? _state.Settings.CustomDictionary : "", _state.Settings.Backend);
            operation = new Operation(activity, model, options, path, initializeUsage, _timeout, _time);
            operation.SettingsWarning = initializeUsage ? _initialSettingsWarning : null;
            operation.Maintenance = maintenance;
            operation.TranscriptUpdates = transcriptUpdates;
            _operation = operation;
            _lastElapsed = TimeSpan.Zero;
            state = SetState(_state with
            {
                Activity = activity,
                OperationId = operation.Id,
                Phase = activity switch
                {
                    SessionActivity.Connecting => DictationPhase.Connecting,
                    SessionActivity.File => DictationPhase.Transcribing,
                    SessionActivity.ModelMaintenance => DictationPhase.MaintainingModels,
                    _ => DictationPhase.Preparing
                },
                Transcript = activity is SessionActivity.Connecting or SessionActivity.ModelMaintenance ? _state.Transcript : "",
                Result = activity is SessionActivity.Connecting or SessionActivity.ModelMaintenance ? _state.Result : null,
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
                case SessionActivity.ModelMaintenance: await MaintainModelsCoreAsync(operation); break;
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
                SessionOutcomeKind.Cancelled => new SessionNotice(NoticeKind.Information, "Cancelled", "Partial text is retained; this attempt is not counted."),
                SessionOutcomeKind.TimedOut => new(NoticeKind.Warning, "Timed out", "The operation exceeded its deadline and is not counted."),
                SessionOutcomeKind.Failed => new(NoticeKind.Error, "Operation failed", errorResult?.Message ?? "The operation failed."),
                _ when operation.UsageWarning is not null => new(NoticeKind.Warning, "Transcript ready; statistics unavailable", operation.UsageWarning),
                _ when operation.Result is not null => new(NoticeKind.Success, "Transcript ready", "Recognition completed and owned resources were released."),
                _ when operation.SettingsWarning is not null => operation.SettingsWarning,
                _ when operation.Activity is SessionActivity.Connecting or SessionActivity.ModelMaintenance && _engine is null =>
                    new(NoticeKind.Information, "No models installed", "Open Models to download verified weights. Recognition stays disconnected."),
                _ when operation.Activity is SessionActivity.Connecting or SessionActivity.ModelMaintenance =>
                    new(NoticeKind.Success, "Native backend ready", $"{_state.Models.Count} installed models; one resident model at a time."),
                _ => new(NoticeKind.Success, "Operation complete", "Owned resources were released.")
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
        var modelsDirectory = candidate.ModelsDirectory;
        var backendVersion = candidate.Version;
        IRecognitionEngine? previous;
        SessionSnapshot detached;
        lock (_gate)
        {
            EnsureCurrent(operation);
            previous = _engine;
            operation.Previous = previous;
            _engine = null;
            detached = SetState(_state with { Models = [], SelectedIndex = -1, BackendVersion = "" });
        }
        Publish(detached);
        if (previous is not null)
        {
            await previous.DisposeAsync();
            operation.Previous = null;
        }
        operation.Token.ThrowIfCancellationRequested();
        if (models.Count == 0)
        {
            await candidate.DisposeAsync();
            operation.Candidate = null;
        }
        SessionSnapshot state;
        lock (_gate)
        {
            EnsureCurrent(operation);
            _engine = models.Count == 0 ? null : candidate;
            operation.Candidate = null;
            var settings = _state.Settings;
            if (!operation.InitializeUsage)
            {
                settings = settings with { ModelsDirectory = Path.GetFullPath(modelsDirectory) };
                operation.SettingsWarning = SaveSettings(settings);
            }
            var preferredId = settings.ModelId ?? operation.Model?.Id;
            var selectedIndex = models.ToList().FindIndex(model => model.Id == preferredId);
            if (preferredId is not null && selectedIndex < 0)
            {
                operation.SettingsWarning ??= new(NoticeKind.Warning, "Saved model unavailable",
                    models.Count == 0
                        ? $"The preferred model '{preferredId}' is not installed. Install it or choose another model."
                        : $"The preferred model '{preferredId}' is not installed in this folder. " +
                          $"Using '{models[0].DisplayName ?? models[0].Id}' for this session; your saved choice is unchanged.");
            }
            state = SetState(_state with
            {
                Models = Array.AsReadOnly(models.ToArray()),
                SelectedIndex = models.Count == 0 ? -1 : Math.Max(0, selectedIndex),
                ModelsDirectory = modelsDirectory, BackendVersion = backendVersion, Settings = settings
            });
        }
        Publish(state);
    }

    private async Task MaintainModelsCoreAsync(Operation operation)
    {
        SessionSnapshot state;
        lock (_gate)
        {
            EnsureCurrent(operation);
            operation.Previous = _engine;
            _engine = null;
            state = SetState(_state with
            {
                Models = [], SelectedIndex = -1, BackendVersion = "",
                ModelsDirectory = Path.GetFullPath(operation.Path!),
                Notice = new(NoticeKind.Information, "Maintaining models",
                    "Releasing the resident model before changing installed files.")
            });
        }
        Publish(state);
        if (operation.Previous is not null)
        {
            await operation.Previous.DisposeAsync();
            operation.Previous = null;
        }
        operation.Token.ThrowIfCancellationRequested();
        await operation.Maintenance!(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        await ConnectCoreAsync(operation);
    }

    private async Task RecognizeFileAsync(Operation operation)
    {
        var recording = await _input.ReadRecordingAsync(operation.Path!, operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        operation.StartedAt = _time.GetTimestamp();
        operation.Result = await _engine!.TranscribeAsync(
            operation.Model!, recording, operation.Options, Progress(operation), operation.Token);
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
        operation.Inference = _engine!.StreamAsync(operation.Model!, operation.Pipe.Reader, operation.Options,
            Progress(operation),
            operation.Token, () => ready.TrySetResult());
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
        await operation.Producer!;
        operation.RecordingSeconds = operation.Capture?.CapturedSeconds ?? 0;
        UpdateResult(operation);
    }

    private async Task ReleaseOperationAsync(Operation operation, List<Exception> errors)
    {
        Task? stop;
        lock (_gate)
        {
            operation.ControlsSealed = true;
            stop = operation.StopWork;
        }
        operation.Cancellation.Cancel();
        operation.Pipe?.Writer.TryComplete();
        await ObserveAsync(stop, operation.Token, errors);
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
        if (!State.Settings.CollectUsage || State.Usage is null) { return; }
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
        await StopCaptureAsync(operation);
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
        if (operation is not null) { await StopCaptureAsync(operation); }
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
            if (operation is not null)
            {
                _ = StopCaptureAsync(operation);
                await operation.Completion.Task;
            }
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

    private Task StopCaptureAsync(Operation operation)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (!ReferenceEquals(_operation, operation) || operation.Capture is null) { return Task.CompletedTask; }
            if (operation.ControlsSealed) { return operation.Completion.Task; }
            if (operation.StopWork is not null) { return operation.StopWork; }
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.StopWork = completion.Task;
        }
        _ = CompleteStopAsync(operation, completion);
        return completion.Task;
    }

    private static async Task CompleteStopAsync(Operation operation, TaskCompletionSource completion)
    {
        try
        {
            await operation.Capture!.StopAsync();
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            operation.CommandError = error;
            operation.RequestCancellation();
            completion.TrySetException(error);
        }
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
        bool report;
        lock (_gate)
            report = !update.IsFinal && ReferenceEquals(_operation, operation) &&
                !_closing && !operation.Token.IsCancellationRequested;
        if (report) { operation.TranscriptUpdates?.Report(update); }
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
            SessionActivity activity, AudioModel? model, RecognitionOptions options, string? path,
            bool initializeUsage, TimeSpan timeout, TimeProvider time)
        {
            Activity = activity; Model = model; Options = options; Path = path;
            InitializeUsage = initializeUsage;
            Cancellation = new CancellationTokenSource(timeout, time);
        }

        public Guid Id { get; } = Guid.NewGuid();
        public SessionActivity Activity { get; }
        public AudioModel? Model { get; }
        public RecognitionOptions Options { get; }
        public string? Path { get; }
        public bool InitializeUsage { get; }
        public SessionNotice? SettingsWarning { get; set; }
        public Func<CancellationToken, Task>? Maintenance { get; set; }
        public IProgress<TranscriptUpdate>? TranscriptUpdates { get; set; }
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
        public Task? StopWork { get; set; }
        public bool ControlsSealed { get; set; }
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
