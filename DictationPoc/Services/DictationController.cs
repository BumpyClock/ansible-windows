using System.Diagnostics;
using System.Threading.Channels;
using DictationPoc.Core;
using Microsoft.UI.Dispatching;

namespace DictationPoc.Services;

internal enum DictationPhase { Disconnected, Connecting, Ready, Preparing, Recording, Finishing, Transcribing, Cancelling }
internal enum NoticeKind { Information, Success, Warning, Error }
internal sealed record SessionNotice(NoticeKind Kind, string Title, string Message);

internal sealed class DictationController(DispatcherQueue dispatcher)
{
    private readonly UsageStore _usage = new(Path.Combine(AppContext.BaseDirectory, "usage.json"));
    private readonly Stopwatch _clock = new();
    private NativeAudioEngine? _engine;
    private MicrophoneCapture? _capture;
    private CancellationTokenSource? _operation;
    private Task? _activeTask;
    private bool _closing;
    private bool _hasFinal;
    private int _version;
    private int _selected;

    public event Action? Changed;
    public event Action<double>? AudioLevel;
    public event Action? UsageChanged;
    public DictationPhase Phase { get; private set; }
    public SessionNotice Notice { get; private set; } = new(NoticeKind.Information, "On-device", "Speech runs inside this application. No server or cloud connection.");
    public IReadOnlyList<AudioModel> Models { get; private set; } = [];
    public AudioModel? SelectedModel => _selected >= 0 && _selected < Models.Count ? Models[_selected] : null;
    public int SelectedIndex => _selected;
    public string ModelsDirectory { get; private set; } = FindModelsDirectory();
    public string Language { get; set; } = "";
    public string Transcript { get; private set; } = "";
    public string BackendVersion { get; private set; } = "";
    public int Words => AudioMeter.CountWords(Transcript);
    public TimeSpan Elapsed => _clock.Elapsed;
    public bool IsReplay { get; private set; }
    public bool CanStart => Phase == DictationPhase.Ready && SelectedModel?.Mode == "streaming";
    public bool CanFinish => Phase == DictationPhase.Recording;
    public bool CanCancel => Phase is DictationPhase.Preparing or DictationPhase.Recording or DictationPhase.Finishing or DictationPhase.Transcribing;
    public bool IsIdle => Phase is DictationPhase.Ready or DictationPhase.Disconnected;
    public bool IsLiveOperation => Phase is DictationPhase.Preparing or DictationPhase.Recording or DictationPhase.Finishing or DictationPhase.Cancelling;
    public UsageDocument? Usage { get; private set; }
    public string? UsageError { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            Usage = await _usage.LoadAsync();
            UsageChanged?.Invoke();
        }
        catch (Exception error)
        {
            UsageError = $"Local usage statistics could not be read: {error.Message}";
            UsageChanged?.Invoke();
        }
        await ConnectAsync(ModelsDirectory);
    }

    public async Task ConnectAsync(string directory)
    {
        if (!IsIdle)
        {
            Fail(new InvalidOperationException("Finish the active transcription before changing the native backend."));
            return;
        }
        Phase = DictationPhase.Connecting;
        Changed?.Invoke();
        NativeAudioEngine? candidate = null;
        try
        {
            if (_engine is not null) { await _engine.DisposeAsync(); }
            _engine = null;
            Models = [];
            candidate = new NativeAudioEngine(
                Path.Combine(AppContext.BaseDirectory, "audiocpp.dll"),
                Path.Combine(AppContext.BaseDirectory, "audio-models.json"), directory);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Models = await candidate.ConnectAsync(timeout.Token);
            _engine = candidate;
            candidate = null;
            ModelsDirectory = _engine.ModelsDirectory;
            BackendVersion = _engine.Version;
            _selected = 0;
            Phase = DictationPhase.Ready;
            Notify(NoticeKind.Success, "Native backend ready", $"{Models.Count} installed models. One model is loaded at a time.");
        }
        catch (Exception error)
        {
            if (candidate is not null) { await candidate.DisposeAsync(); }
            _engine = null;
            Models = [];
            Phase = DictationPhase.Disconnected;
            Fail(error);
        }
        Changed?.Invoke();
    }

    public void SelectModel(int index)
    {
        if (Phase != DictationPhase.Ready || index < 0 || index >= Models.Count)
        {
            throw new InvalidOperationException("Choose an installed model while the backend is ready.");
        }
        _selected = index;
        Notify(NoticeKind.Information, "Model selected",
            SelectedModel!.Mode == "offline" ? "This model supports WAV verification, not live dictation." :
            SelectedModel.Preview == "final-only" ? "This adapter returns text after Finish. Audio is still processed locally." :
            "The floating panel displays transcript updates when the model produces them.");
    }

    public Task StartDictationAsync()
    {
        if (!CanStart || _engine is null || SelectedModel is null)
        {
            Fail(new InvalidOperationException("Load a streaming ASR model before starting dictation."));
            return Task.CompletedTask;
        }
        return _activeTask = RunLiveAsync(SelectedModel, null);
    }

    public Task ReplayAsync(string path)
    {
        if (!CanStart || _engine is null || SelectedModel is null)
        {
            Fail(new InvalidOperationException("Live verification requires a streaming ASR model."));
            return Task.CompletedTask;
        }
        return _activeTask = RunLiveAsync(SelectedModel, path);
    }

    private async Task RunLiveAsync(AudioModel model, string? replayPath)
    {
        Begin(DictationPhase.Preparing);
        IsReplay = replayPath is not null;
        var token = _operation!.Token;
        var pipe = Channel.CreateBounded<byte[]>(16);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? producer = null;
        Task<string>? inference = null;
        var succeeded = false;
        var captureSeconds = 0.0;
        try
        {
            Notify(NoticeKind.Information, "Preparing model", "Native weights and session state are loaded before microphone capture starts.");
            inference = _engine!.StreamAsync(model, pipe.Reader, Language, Progress(), token, () => prepared.TrySetResult());
            if (await Task.WhenAny(prepared.Task, inference) == inference)
            {
                await inference;
                throw new InvalidOperationException("The native session ended before receiving audio.");
            }
            await prepared.Task.WaitAsync(token);
            if (replayPath is null)
            {
                _capture = await MicrophoneCapture.StartAsync();
                _capture.LevelChanged += PostLevel;
                producer = ForwardMicrophoneAsync(_capture.Audio, pipe.Writer, token);
            }
            else
            {
                var pcm = PcmWave.Read16kMono(replayPath);
                producer = FeedReplayAsync(pcm, pipe.Writer, token);
            }
            _clock.Restart();
            Phase = DictationPhase.Recording;
            Notify(NoticeKind.Information, IsReplay ? "Live verification" : "Listening",
                IsReplay ? "A WAV recording is feeding the native session. This is not microphone capture." :
                "Speak normally. Finish when you are done; preview timing depends on the selected model.");
            var text = await inference;
            await producer;
            if (_capture is not null) { captureSeconds = _capture.CapturedSeconds; }
            SetTranscript(new TranscriptUpdate(text, true));
            succeeded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Notify(NoticeKind.Warning, "Cancelled", "Visible partial text is retained. Cancelled sessions do not count toward Insights.");
        }
        catch (Exception error)
        {
            Fail(error);
        }
        finally
        {
            _operation!.Cancel();
            pipe.Writer.TryComplete();
            if (_capture is not null)
            {
                try { await _capture.DisposeAsync(); }
                catch (Exception error) { Fail(error); succeeded = false; }
                _capture = null;
            }
            if (producer is not null)
            {
                try { await producer; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error) { Fail(error); succeeded = false; }
            }
            if (inference is not null && !inference.IsCompleted)
            {
                try { await inference; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error) { Fail(error); succeeded = false; }
            }
            _clock.Stop();
            if (succeeded)
            {
                await RecordUsageAsync(model.Id, replayPath is null ? UsageSource.Dictation : UsageSource.File, captureSeconds);
            }
            End();
        }
    }

    private static async Task ForwardMicrophoneAsync(ChannelReader<byte[]> input, ChannelWriter<byte[]> output, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            await foreach (var bytes in input.ReadAllAsync(token))
            {
                await output.WriteAsync(bytes, token);
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally { output.TryComplete(failure); }
    }

    private async Task FeedReplayAsync(byte[] pcm, ChannelWriter<byte[]> output, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            for (var offset = 0; offset < pcm.Length; offset += 1280)
            {
                var bytes = pcm.AsSpan(offset, Math.Min(1280, pcm.Length - offset)).ToArray();
                await output.WriteAsync(bytes, token);
                PostLevel(AudioMeter.Pcm16Level(bytes));
                await Task.Delay(40, token);
            }
            dispatcher.TryEnqueue(() =>
            {
                if (Phase == DictationPhase.Recording)
                {
                    Phase = DictationPhase.Finishing;
                    Notify(NoticeKind.Information, "Finishing verification", "Waiting for the native model's final transcript.");
                }
            });
        }
        catch (Exception error) { failure = error; throw; }
        finally { output.TryComplete(failure); }
    }

    public async Task FinishAsync()
    {
        if (!CanFinish || IsReplay)
        {
            return;
        }
        Phase = DictationPhase.Finishing;
        Notify(NoticeKind.Information, "Finishing dictation", "Microphone capture stops while the native session completes.");
        try
        {
            if (_capture is not null) { await _capture.StopAsync(); }
        }
        catch (Exception error) { Fail(error); _operation?.Cancel(); }
    }

    public async Task CancelAsync()
    {
        _operation?.Cancel();
        if (_capture is not null)
        {
            try { await _capture.StopAsync(); }
            catch (Exception error) { Fail(error); }
        }
        if (!IsIdle)
        {
            Phase = DictationPhase.Cancelling;
            Notify(NoticeKind.Warning, "Cancellation requested", "A running native inference step must finish before its state can be released.");
        }
    }

    public Task TranscribeFileAsync(string path)
    {
        if (Phase != DictationPhase.Ready || _engine is null || SelectedModel is null)
        {
            Fail(new InvalidOperationException("Choose an installed ASR model before verifying a recording."));
            return Task.CompletedTask;
        }
        return _activeTask = RunFileAsync(SelectedModel, path);
    }

    private async Task RunFileAsync(AudioModel model, string path)
    {
        Begin(DictationPhase.Transcribing);
        IsReplay = false;
        _clock.Restart();
        try
        {
            Notify(NoticeKind.Information, "Verifying recording", "The selected model runs directly inside the app. Large models can take longer on CPU.");
            var text = await _engine!.TranscribeFileAsync(model, path, Language, Progress(), _operation!.Token);
            SetTranscript(new TranscriptUpdate(text, true));
            _clock.Stop();
            await RecordUsageAsync(model.Id, UsageSource.File, 0);
        }
        catch (OperationCanceledException) when (_operation?.IsCancellationRequested == true)
        {
            Notify(NoticeKind.Warning, "Cancelled", "Cancelled sessions do not count toward Insights.");
        }
        catch (Exception error) { Fail(error); }
        finally { End(); }
    }

    private async Task RecordUsageAsync(string model, UsageSource source, double seconds)
    {
        Notify(NoticeKind.Success, "Transcript ready",
            string.IsNullOrWhiteSpace(Transcript) ? "No speech was recognized." : "The completed transcript is ready to copy.");
        if (Usage?.Enabled != true)
        {
            return;
        }
        try
        {
            Usage = await _usage.RecordAsync(new UsageEntry
            {
                Id = Guid.NewGuid(), CompletedAt = DateTimeOffset.UtcNow, ModelId = model,
                Source = source, Words = Words, RecordingSeconds = seconds, ElapsedSeconds = _clock.Elapsed.TotalSeconds
            });
            UsageError = null;
            UsageChanged?.Invoke();
        }
        catch (Exception error)
        {
            UsageError = $"The transcript succeeded, but local usage statistics could not be saved: {error.Message}";
            Notify(NoticeKind.Warning, "Transcript ready; statistics unavailable", UsageError);
            UsageChanged?.Invoke();
        }
    }

    public async Task SetUsageEnabledAsync(bool enabled)
    {
        try
        {
            Usage = await _usage.SetEnabledAsync(enabled);
            UsageError = null;
            UsageChanged?.Invoke();
        }
        catch (Exception error) { UsageError = error.Message; Fail(error); }
    }

    private void Begin(DictationPhase phase)
    {
        _operation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _version++;
        _hasFinal = false;
        Transcript = "";
        Phase = phase;
        Changed?.Invoke();
    }

    private void End()
    {
        _clock.Stop();
        _operation?.Dispose();
        _operation = null;
        Phase = _engine is null ? DictationPhase.Disconnected : DictationPhase.Ready;
        AudioLevel?.Invoke(0);
        Changed?.Invoke();
    }

    private IProgress<TranscriptUpdate> Progress()
    {
        var version = _version;
        return new Progress<TranscriptUpdate>(update =>
        {
            if (version == _version) { SetTranscript(update); }
        });
    }

    private void SetTranscript(TranscriptUpdate update)
    {
        if (_closing || _hasFinal && !update.IsFinal) { return; }
        _hasFinal |= update.IsFinal;
        Transcript = update.Text;
        Changed?.Invoke();
    }

    private void PostLevel(double value) => dispatcher.TryEnqueue(() =>
    {
        if (!_closing && Phase == DictationPhase.Recording) { AudioLevel?.Invoke(value); }
    });

    public void Fail(Exception error) => Notify(NoticeKind.Error, "Operation failed", error.Message);
    public void Notify(NoticeKind kind, string title, string message)
    {
        Notice = new SessionNotice(kind, title, message);
        if (!_closing) { Changed?.Invoke(); }
        else { Debug.WriteLine($"Local Dictation: {title}: {message}"); }
    }

    public async Task ShutdownAsync()
    {
        _closing = true;
        await CancelAsync();
        if (_activeTask is not null) { await _activeTask; }
        if (_engine is not null) { await _engine.DisposeAsync(); }
    }

    private static string FindModelsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, ".runtime", "models");
            if (Directory.Exists(path)) { return path; }
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "models");
    }
}
