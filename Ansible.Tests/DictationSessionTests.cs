using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class DictationSessionTests : IDisposable
{
    private readonly string _settingsDirectory = Path.Combine(Path.GetTempPath(), "LocalVoiceSettingsTests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_settingsDirectory, "settings.json");

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public async Task MicrophoneBoostPersistsAndAppliesWhenCaptureStarts(int decibels)
    {
        var initial = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await initial.InitializeAsync();
        Assert.Equal(0, initial.State.Settings.MicrophoneBoostDecibels);
        initial.SetMicrophoneBoost(decibels);
        Assert.Throws<InvalidDataException>(() => initial.SetMicrophoneBoost(-1));
        Assert.Throws<InvalidDataException>(() => initial.SetMicrophoneBoost(25));
        Assert.Equal(decibels, initial.State.Settings.MicrophoneBoostDecibels);
        await initial.CloseAsync();

        var capture = new FakeCaptureFactory();
        var session = Create(new FakeEngine(), capture, settings: new AppSettingsStore(SettingsPath));
        await session.InitializeAsync();
        Assert.Equal(decibels, session.State.Settings.MicrophoneBoostDecibels);
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        Assert.Equal(decibels, capture.LastMicrophoneBoostDecibels);
        Assert.Throws<InvalidOperationException>(() => session.SetMicrophoneBoost(6));
        await session.FinishAsync();
        Assert.Equal(SessionOutcomeKind.Completed, (await recording).Kind);
        await session.CloseAsync();
    }

    [Fact]
    public async Task MicrophoneBoostDoesNotChangeFileOrReplayAudio()
    {
        var received = new List<byte[]>();
        var engine = new FakeEngine
        {
            Streaming = async (_, audio, token, ready) =>
            {
                ready?.Invoke();
                await foreach (var packet in audio.ReadAllAsync(token)) { received.Add(packet); }
                return new RecognitionResult("test", "test");
            }
        };
        var session = Create(engine, input: new FakeInput
        {
            Replay = _ => Task.FromResult<byte[]>([100, 0, 156, 255]),
            Recording = new WaveAudio([0.125f, -0.125f], 16000, 1)
        });
        await session.InitializeAsync();
        session.SetMicrophoneBoost(20);
        Assert.Equal(SessionOutcomeKind.Completed, (await session.TranscribeFileAsync("sample.wav")).Kind);
        Assert.Equal(new float[] { 0.125f, -0.125f }, engine.LastAudio!.Samples);
        Assert.Equal(SessionOutcomeKind.Completed, (await session.ReplayAsync("sample.wav")).Kind);
        Assert.Equal(new byte[] { 100, 0, 156, 255 }, received.SelectMany(packet => packet).ToArray());
        await session.CloseAsync();
    }

    [Fact]
    public async Task BackendPreferencePersistsAndAppliesToFileAndLiveRecognition()
    {
        var engine = new FakeEngine();
        var session = Create(engine, settings: new AppSettingsStore(SettingsPath));
        await session.InitializeAsync();
        Assert.Equal(NativeBackend.Cpu, session.State.Settings.Backend);
        session.SetBackend(NativeBackend.Vulkan);
        Assert.Throws<InvalidDataException>(() => session.SetBackend((NativeBackend)42));
        Assert.Equal(NativeBackend.Vulkan, session.State.Settings.Backend);
        await session.TranscribeFileAsync("sample.wav");
        Assert.Equal(NativeBackend.Vulkan, engine.LastOptions?.Backend);
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        Assert.Equal(NativeBackend.Vulkan, engine.LastOptions?.Backend);
        Assert.Equal(0, engine.LastOptions?.DeviceIndex);
        Assert.Throws<InvalidOperationException>(() => session.SetBackend(NativeBackend.Cpu));
        await session.FinishAsync();
        await recording;
        await session.CloseAsync();

        var restarted = Create(engine = new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await restarted.InitializeAsync();
        Assert.Equal(NativeBackend.Vulkan, restarted.State.Settings.Backend);
        restarted.SetBackend(NativeBackend.Cpu);
        await restarted.TranscribeFileAsync("sample.wav");
        Assert.Equal(NativeBackend.Cpu, engine.LastOptions?.Backend);
        await restarted.CloseAsync();
    }
    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory)) { Directory.Delete(_settingsDirectory, recursive: true); }
    }

    [Fact]
    public async Task CloseJoinsLateConnectionAndNeverPublishesReady()
    {
        var connect = new TaskCompletionSource<IReadOnlyList<AudioModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine { Connecting = _ => connect.Task };
        var session = Create(engine);
        var initialization = session.InitializeAsync();
        await engine.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var close = session.CloseAsync();
        Assert.False(close.IsCompleted);
        Assert.Equal(DictationPhase.Closing, session.State.Phase);
        connect.SetResult([Model]);
        Assert.Equal(SessionOutcomeKind.Cancelled, (await initialization).Kind);
        await close;
        Assert.Equal(DictationPhase.Closed, session.State.Phase);
        Assert.Equal(1, engine.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartDictationAsync());
    }

    [Fact]
    public async Task MicrophoneOpensWhileTheModelLoadsAndKeepsEarlySpeech()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<byte[]>();
        var engine = new FakeEngine
        {
            Streaming = async (_, audio, token, callback) =>
            {
                await ready.Task.WaitAsync(token);
                callback?.Invoke();
                await foreach (var packet in audio.ReadAllAsync(token)) { received.Add(packet); }
                return new RecognitionResult("early words", "early words");
            }
        };
        var source = new FakeCapture();
        var session = Create(engine, new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) });
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        Assert.Contains("model is loading", session.State.Notice.Message);
        Assert.True(source.Packets.Writer.TryWrite([1, 2]));
        // Releasing hold-to-talk before the model is ready finishes the dictation instead of discarding it.
        Assert.True(session.State.CanFinish);
        await session.FinishAsync();
        ready.SetResult();

        var outcome = await recording;

        Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("early words", outcome.Result?.SpeechText);
        Assert.Equal(new byte[] { 1, 2 }, Assert.Single(received));
        await session.CloseAsync();
    }

    [Fact]
    public async Task CancelWhileTheModelLoadsReleasesTheMicrophone()
    {
        var engine = new FakeEngine
        {
            Streaming = async (_, _, token, _) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new RecognitionResult("", "");
            }
        };
        var source = new FakeCapture();
        var session = Create(engine, new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) });
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);

        await session.CancelAsync();

        Assert.Equal(SessionOutcomeKind.Cancelled, (await recording).Kind);
        Assert.True(source.IsReleased);
        Assert.Equal(DictationPhase.Ready, session.State.Phase);
        await session.CloseAsync();
    }

    [Fact]
    public async Task ConnectingPreparesTheSelectedModelAndModelChangesPrepareAgain()
    {
        var engine = new FakeEngine();
        var session = Create(engine);
        await session.InitializeAsync();
        await engine.PrepareCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(Model, engine.PreparedModel);
        Assert.Equal(NativeBackend.Cpu, engine.PreparedOptions?.Backend);

        session.SetBackend(NativeBackend.Vulkan);
        await WaitUntilAsync(() => engine.PreparedOptions?.Backend == NativeBackend.Vulkan);

        await session.CloseAsync();
        Assert.Equal(0, engine.Unloads);
    }

    [Fact]
    public async Task IdleModelUnloadsAfterInactivityAndDictationRestartsTheCountdown()
    {
        var clock = new ManualTime();
        var engine = new FakeEngine();
        var source = new FakeCapture();
        var session = new DictationSession(_ => engine,
            new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) }, new FakeInput(),
            new FakeUsageStore(), "models", new FakeSettingsStore(), clock, idleUnloadAfter: TimeSpan.FromMinutes(10));
        await session.InitializeAsync();
        clock.Advance(TimeSpan.FromMinutes(9));
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        await session.FinishAsync();
        await recording;
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, engine.Unloads);

        clock.Advance(TimeSpan.FromMinutes(2));

        await engine.UnloadCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, engine.Unloads);
        await session.CloseAsync();
    }

    [Fact]
    public async Task CloseCancelsAndJoinsPreparationBeforeReleasingTheEngine()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine
        {
            Preparing = async token =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cancelled.TrySetResult(); }
            }
        };
        var session = Create(engine);
        await session.InitializeAsync();
        await engine.PrepareCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(cancelled.Task.IsCompleted);
        Assert.Equal(1, engine.Disposals);
        Assert.Equal(DictationPhase.Closed, session.State.Phase);
    }

    [Fact]
    public async Task FailedPreparationWarnsWithoutBlockingDictation()
    {
        var engine = new FakeEngine { Preparing = _ => Task.FromException(new InsufficientMemoryException("not enough memory")) };
        var session = Create(engine);
        await session.InitializeAsync();

        await WaitUntilAsync(() => session.State.Notice.Title == "Model not preloaded");

        Assert.Contains("not enough memory", session.State.Notice.Message);
        Assert.True(session.State.CanStart);
        await session.CloseAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) { throw new TimeoutException("The expected session state was not reached."); }
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task CancelAfterReadinessBeforeFactoryReturnReleasesCapture()
    {
        var source = new FakeCapture();
        var opening = new TaskCompletionSource<IAudioCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeCaptureFactory { Starting = _ => opening.Task };
        var engine = new FakeEngine();
        var session = Create(engine, factory);
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.CancelAsync();
        opening.SetResult(source);
        Assert.Equal(SessionOutcomeKind.Cancelled, (await recording).Kind);
        Assert.True(source.IsReleased);
        Assert.Equal(1, source.Disposals);
        Assert.Equal(0, engine.FinishedStreams);
        await session.CloseAsync();
    }

    [Fact]
    public async Task OneOperationOwnsCaptureAndCountsAuthoritativeSpeechOnce()
    {
        var usage = new FakeUsageStore();
        var source = new FakeCapture { Seconds = 10 };
        var factory = new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) };
        var engine = new FakeEngine { Result = new("Speaker 0: hello world", "hello world") };
        var session = Create(engine, factory, usage);
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartDictationAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync("other"));
        await source.Packets.Writer.WriteAsync([0, 0, 0, 0]);
        await session.FinishAsync();
        var outcome = await recording;
        Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("Speaker 0: hello world", session.State.Transcript);
        Assert.Equal(2, Assert.Single(usage.Document.Entries).Words);
        Assert.Equal(10, usage.Document.Entries[0].RecordingSeconds);
        Assert.Equal(1, source.Disposals);
        Assert.Equal(1, factory.Starts);
        await session.CloseAsync();
    }

    [Fact]
    public async Task FailedCaptureReleaseBlocksNewWorkAndCloseRetriesOwnership()
    {
        var source = new FakeCapture { FailDisposal = true };
        var factory = new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) };
        var session = Create(new FakeEngine(), factory);
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        await session.CancelAsync();
        Assert.Equal(SessionOutcomeKind.Failed, (await recording).Kind);
        Assert.Equal(DictationPhase.RecoveryRequired, session.State.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartDictationAsync());
        await Assert.ThrowsAsync<IOException>(() => session.CloseAsync());
        Assert.Equal(DictationPhase.Closing, session.State.Phase);
        source.FailDisposal = false;
        await session.CloseAsync();
        Assert.True(source.IsReleased);
        Assert.Equal(DictationPhase.Closed, session.State.Phase);
    }

    [Fact]
    public async Task StartupFailureRetainsAnUnreleasedCapture()
    {
        var source = new FakeCapture { FailDisposal = true };
        var factory = new FakeCaptureFactory
        {
            Starting = _ => Task.FromException<IAudioCapture>(
                new CaptureOwnershipException("startup cleanup failed", source, new IOException("driver busy")))
        };
        var session = Create(new FakeEngine(), factory);
        await session.InitializeAsync();
        Assert.Equal(SessionOutcomeKind.Failed, (await session.StartDictationAsync()).Kind);
        Assert.Equal(DictationPhase.RecoveryRequired, session.State.Phase);
        source.FailDisposal = false;
        await session.CloseAsync();
        Assert.True(source.IsReleased);
    }

    [Fact]
    public async Task InvalidReplayInputIsRejectedBeforeNativePreparation()
    {
        var engine = new FakeEngine();
        var input = new FakeInput
        {
            Replay = _ => Task.FromException<byte[]>(new InvalidDataException("invalid PCM input"))
        };
        var session = Create(engine, input: input);
        await session.InitializeAsync();
        Assert.Equal(SessionOutcomeKind.Failed, (await session.ReplayAsync("bad.wav")).Kind);
        Assert.Equal(0, engine.StreamStarts);
        await session.CloseAsync();
    }

    [Fact]
    public async Task StoreFailureDoesNotTurnACompletedTranscriptIntoFailure()
    {
        var usage = new FakeUsageStore { FailRecording = true };
        var session = Create(new FakeEngine(), usage: usage);
        await session.InitializeAsync();
        var outcome = await session.TranscribeFileAsync("sample.wav");
        Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("hello world", session.State.Transcript);
        Assert.Equal(NoticeKind.Warning, session.State.Notice.Kind);
        Assert.Empty(usage.Document.Entries);
        await session.CloseAsync();
    }

    [Fact]
    public async Task AllSettingsUseOneStoreAndRestoreAfterRestart()
    {
        var folder = Path.Combine(_settingsDirectory, "chosen-models");
        var models = new[] { Model, Model with { Id = "chosen" } };
        FakeEngine Engine() => new()
        {
            ModelsDirectory = folder,
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>(models)
        };
        var session = Create(Engine(), settings: new AppSettingsStore(SettingsPath));
        await session.InitializeAsync();
        session.SelectModel(1);
        session.SetLanguage("fr");
        session.SetCustomDictionary("Contoso\nWinUI");
        session.SetShortcut(new DictationShortcut(3, 0x44));
        session.SetPushToTalk(false);
        session.SetUsageEnabled(false);
        await session.MaintainModelsAsync(_ => Task.CompletedTask, folder);
        session.SetLanguage("de");
        await session.CloseAsync();

        var usage = new FakeUsageStore();
        var restarted = Create(Engine(), usage: usage, settings: new AppSettingsStore(SettingsPath));
        Assert.Equal(folder, restarted.State.ModelsDirectory);
        await restarted.InitializeAsync();
        Assert.Equal("chosen", restarted.State.SelectedModel?.Id);
        Assert.Equal("de", restarted.State.Language);
        Assert.Equal("Contoso\nWinUI", restarted.State.Settings.CustomDictionary);
        Assert.Equal(new DictationShortcut(3, 0x44), restarted.State.Settings.Shortcut);
        Assert.False(restarted.State.Settings.PushToTalk);
        Assert.False(restarted.State.Settings.CollectUsage);
        Assert.Equal(folder, restarted.State.Settings.ModelsDirectory);
        await restarted.TranscribeFileAsync("sample.wav");
        Assert.Empty(usage.Document.Entries);
        Assert.Equal("settings.json", Path.GetFileName(Assert.Single(Directory.GetFiles(_settingsDirectory))));
        await restarted.CloseAsync();
    }

    [Fact]
    public async Task RepeatedCloseUsesOneLifetimeTask()
    {
        var session = Create(new FakeEngine());
        await session.InitializeAsync();
        var first = session.CloseAsync();
        var second = session.CloseAsync();
        Assert.Same(first, second);
        await first;
    }

    [Fact]
    public async Task DeadlineIsDistinctFromUserCancellation()
    {
        var clock = new ManualTime();
        var engine = new FakeEngine
        {
            Streaming = async (_, _, token, _) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new("", "");
            }
        };
        var session = new DictationSession(_ => engine, new FakeCaptureFactory(), new FakeInput(),
            new FakeUsageStore(), "models", new FakeSettingsStore(), clock, TimeSpan.FromMinutes(1));
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await engine.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(SessionOutcomeKind.TimedOut, (await recording).Kind);
        Assert.Equal(DictationPhase.Ready, session.State.Phase);
        await session.CloseAsync();
    }

    [Fact]
    public async Task LateTranscriptCallbacksCannotChangeACompletedOperation()
    {
        var engine = new FakeEngine();
        var source = new FakeCapture();
        var session = Create(engine, new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) });
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        await session.FinishAsync();
        await recording;
        engine.LastProgress!.Report(new TranscriptUpdate("stale text", true));
        Assert.Equal("hello world", session.State.Transcript);
        await session.CloseAsync();
    }

    [Fact]
    public async Task DisabledCollectionDoesNotEmitCompletedSessionMetadata()
    {
        var usage = new FakeUsageStore();
        var session = Create(new FakeEngine(), usage: usage);
        await session.InitializeAsync();
        session.SetUsageEnabled(false);
        Assert.Equal(SessionOutcomeKind.Completed, (await session.TranscribeFileAsync("sample.wav")).Kind);
        Assert.Empty(usage.Document.Entries);
        await session.CloseAsync();
    }

    [Fact]
    public async Task DelayedFinishFailureCannotBePublishedOrCountedAsSuccess()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeCapture();
        source.Stopping = () =>
        {
            source.Packets.Writer.TryComplete();
            source.ReleaseOwnership();
            return stopped.Task;
        };
        var usage = new FakeUsageStore();
        var engine = new FakeEngine();
        var session = Create(engine,
            new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) }, usage);
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        var finish = session.FinishAsync();
        await engine.StreamCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(recording.IsCompleted);
        Assert.Empty(usage.Document.Entries);
        stopped.SetException(new IOException("original stop attempt failed"));
        await Assert.ThrowsAsync<IOException>(() => finish);
        Assert.Equal(SessionOutcomeKind.Failed, (await recording).Kind);
        Assert.Empty(usage.Document.Entries);
        Assert.Equal(DictationPhase.Ready, session.State.Phase);
        await session.CloseAsync();
    }

    [Fact]
    public async Task CloseStopsCaptureBeforeWaitingForABlockedNativeStep()
    {
        var kernel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeCapture();
        source.Stopping = () =>
        {
            source.Packets.Writer.TryComplete();
            stopped.TrySetResult();
            return Task.CompletedTask;
        };
        var engine = new FakeEngine
        {
            Streaming = async (_, _, token, ready) =>
            {
                ready?.Invoke();
                await kernel.Task;
                token.ThrowIfCancellationRequested();
                return new("ignored", "ignored");
            }
        };
        var session = Create(engine,
            new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) });
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        var close = session.CloseAsync();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(close.IsCompleted);
        kernel.SetResult();
        Assert.Equal(SessionOutcomeKind.Cancelled, (await recording).Kind);
        await close;
        Assert.True(source.IsReleased);
        Assert.Equal(DictationPhase.Closed, session.State.Phase);
    }

    private static readonly AudioModel Model = new() { Id = "test", Family = "test", Mode = "streaming" };

    [Fact]
    public async Task RestartRestoresChosenModelByIdAndLanguageForRecognition()
    {
        var moonshine = Model with { Id = "moonshine-tiny" };
        var chosen = Model with { Id = "whisper-base" };
        var first = Create(new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([moonshine, chosen])
        }, settings: new AppSettingsStore(SettingsPath));
        await first.InitializeAsync();
        first.SelectModel(1);
        first.SetLanguage("  fr  ");
        await first.CloseAsync();

        var engine = new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>(
                [Model with { Id = "new-model" }, moonshine, chosen])
        };
        var restarted = Create(engine, settings: new AppSettingsStore(SettingsPath));
        await restarted.InitializeAsync();
        Assert.Equal("whisper-base", restarted.State.SelectedModel?.Id);
        Assert.Equal(2, restarted.State.SelectedIndex);
        Assert.Equal("fr", restarted.State.Language);
        await restarted.TranscribeFileAsync("sample.wav");
        Assert.Equal("whisper-base", engine.LastModel?.Id);
        Assert.Equal("fr", engine.LastLanguage);
        await restarted.CloseAsync();
    }

    [Fact]
    public async Task DictionaryPersistsAndOnlySupportedModelsReceiveHintsForFilesAndStreams()
    {
        var supported = Model with { Id = "context-model", SupportsCustomDictionary = true };
        var models = new[] { Model, supported };
        FakeEngine Engine() => new() { Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>(models) };
        var engine = Engine();
        var session = Create(engine, settings: new AppSettingsStore(SettingsPath));
        await session.InitializeAsync();
        session.SetCustomDictionary("  Contoso  \r\nWinUI\r\ncontoso\n\nCaf\u0065\u0301\nCAF\u00c9");
        session.SetLanguage("en");
        Assert.Equal("Contoso\nWinUI\nCaf\u00e9", session.State.Settings.CustomDictionary);
        await session.TranscribeFileAsync("sample.wav");
        Assert.Equal("", engine.LastDictionary);
        Assert.Equal("hello world", session.State.Transcript);
        session.SelectModel(1);
        await session.TranscribeFileAsync("sample.wav");
        Assert.Equal("Contoso\nWinUI\nCaf\u00e9", engine.LastDictionary);
        Assert.Equal("en", engine.LastLanguage);
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        Assert.Equal("Contoso\nWinUI\nCaf\u00e9", engine.LastDictionary);
        Assert.Throws<InvalidOperationException>(() => session.SetCustomDictionary("changed during recording"));
        await session.FinishAsync();
        await recording;
        Assert.Equal("hello world", session.State.Result?.SpeechText);
        await session.CloseAsync();

        var restarted = Create(Engine(), settings: new AppSettingsStore(SettingsPath));
        await restarted.InitializeAsync();
        Assert.Equal("context-model", restarted.State.SelectedModel?.Id);
        Assert.Equal("Contoso\nWinUI\nCaf\u00e9", restarted.State.Settings.CustomDictionary);
        restarted.SetCustomDictionary(" \n ");
        await restarted.CloseAsync();
        var cleared = Create(Engine(), settings: new AppSettingsStore(SettingsPath));
        await cleared.InitializeAsync();
        Assert.Equal("", cleared.State.Settings.CustomDictionary);
        Assert.Equal("en", cleared.State.Language);
        await cleared.CloseAsync();
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("entry-length")]
    [InlineData("utf8-size")]
    [InlineData("control-character")]
    public async Task InvalidDictionaryKeepsThePreviousSettings(string failure)
    {
        var text = failure switch
        {
            "entries" => string.Join("\n", Enumerable.Range(1, 101).Select(index => $"word{index}")),
            "entry-length" => new string('a', 101),
            "utf8-size" => string.Join("\n", Enumerable.Range(1, 100).Select(index => new string('\u4e2d', 20) + index)),
            "control-character" => "word\0hidden",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        var session = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await session.InitializeAsync();
        session.SetCustomDictionary("previous word");
        Assert.Throws<InvalidDataException>(() => session.SetCustomDictionary(text));
        Assert.Equal("previous word", session.State.Settings.CustomDictionary);
        await session.CloseAsync();
        var restarted = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await restarted.InitializeAsync();
        Assert.Equal("previous word", restarted.State.Settings.CustomDictionary);
        await restarted.CloseAsync();
    }

    [Fact]
    public async Task DictionaryAcceptsExactlyFourKilobytesButRejectsAnExtraByte()
    {
        var text = string.Join("\n", Enumerable.Range(0, 40).Select(index => $"{index:00}" + new string('a', 98))
            .Append(new string('b', 56)));
        var session = Create(new FakeEngine());
        await session.InitializeAsync();
        session.SetCustomDictionary(text);
        Assert.Equal(4096, System.Text.Encoding.UTF8.GetByteCount(session.State.Settings.CustomDictionary));
        Assert.Throws<InvalidDataException>(() => session.SetCustomDictionary(text + "b"));
        Assert.Equal(text, session.State.Settings.CustomDictionary);
        await session.CloseAsync();
    }

    [Fact]
    public async Task DictionarySupportDoesNotGiveAnOfflineModelMicrophoneSupport()
    {
        var offline = Model with { Id = "offline-context", Mode = "offline", SupportsCustomDictionary = true };
        var engine = new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([offline])
        };
        var session = Create(engine);
        await session.InitializeAsync();
        session.SetCustomDictionary("Contoso");
        await session.TranscribeFileAsync("sample.wav");
        Assert.Equal("Contoso", engine.LastDictionary);
        Assert.False(session.State.CanStart);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartDictationAsync());
        Assert.Equal(0, engine.StreamStarts);
        await session.CloseAsync();
    }

    [Fact]
    public async Task ClearingLanguageAndChangingModelPreservesBothPreferencesOnRestart()
    {
        var models = new[] { Model, Model with { Id = "other" } };
        FakeEngine Engine() => new() { Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>(models) };
        var first = Create(Engine(), settings: new AppSettingsStore(SettingsPath));
        await first.InitializeAsync();
        first.SetLanguage("de");
        first.SelectModel(1);
        first.SetLanguage("");
        await first.CloseAsync();
        var restarted = Create(Engine(), settings: new AppSettingsStore(SettingsPath));
        await restarted.InitializeAsync();
        Assert.Equal("other", restarted.State.SelectedModel?.Id);
        Assert.Equal("", restarted.State.Language);
        await restarted.CloseAsync();
    }

    [Fact]
    public async Task MissingSavedModelWarnsWithoutOverwritingChoiceAndRestoresAfterMaintenance()
    {
        var first = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await first.InitializeAsync();
        first.SelectModel(0);
        await first.CloseAsync();

        var connects = 0;
        var fallback = Model with { Id = "fallback" };
        var restarted = Create(new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>(
                ++connects == 1 ? [fallback] : [fallback, Model])
        }, settings: new AppSettingsStore(SettingsPath));
        await restarted.InitializeAsync();
        Assert.Equal("fallback", restarted.State.SelectedModel?.Id);
        Assert.Equal(NoticeKind.Warning, restarted.State.Notice.Kind);
        Assert.Contains("test", restarted.State.Notice.Message);
        restarted.SetLanguage("en");
        await restarted.MaintainModelsAsync(_ => Task.CompletedTask);
        Assert.Equal("test", restarted.State.SelectedModel?.Id);
        await restarted.CloseAsync();

        var next = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await next.InitializeAsync();
        Assert.Equal("test", next.State.SelectedModel?.Id);
        Assert.Equal("en", next.State.Language);
        await next.CloseAsync();
    }

    [Fact]
    public async Task SavedModelSurvivesStartupWithNoInstalledWeights()
    {
        var settings = new FakeSettingsStore { Settings = new() { ModelId = "test", Language = "en" } };
        var connects = 0;
        var session = Create(new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>(++connects == 1 ? [] : [Model])
        }, settings: settings);
        await session.InitializeAsync();
        Assert.Equal(DictationPhase.Disconnected, session.State.Phase);
        Assert.Null(session.State.SelectedModel);
        Assert.Equal(NoticeKind.Warning, session.State.Notice.Kind);
        Assert.Equal("en", session.State.Language);
        await session.ConnectAsync("models");
        Assert.Equal("test", session.State.SelectedModel?.Id);
        Assert.Equal(1, settings.Saves);
        await session.CloseAsync();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"Language":null}""")]
    [InlineData("""{"Backend":42}""")]
    [InlineData("""{"ModelId":" "}""")]
    [InlineData("""{"ModelsDirectory":"relative"}""")]
    [InlineData("""{"Shortcut":{"Modifiers":0,"Key":65}}""")]
    public async Task InvalidSavedPreferencesWarnButDoNotBlockRecognition(string saved)
    {
        Directory.CreateDirectory(_settingsDirectory);
        File.WriteAllText(SettingsPath, saved);
        var session = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        Assert.Equal(SessionOutcomeKind.Completed, (await session.InitializeAsync()).Kind);
        Assert.Equal("test", session.State.SelectedModel?.Id);
        Assert.Equal("", session.State.Language);
        Assert.Equal(NoticeKind.Warning, session.State.Notice.Kind);
        Assert.Equal("Settings unavailable", session.State.Notice.Title);
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
        Assert.Equal(SessionOutcomeKind.Completed, (await session.TranscribeFileAsync("sample.wav")).Kind);
        Assert.Equal(SessionOutcomeKind.Completed, (await session.MaintainModelsAsync(_ => Task.CompletedTask)).Kind);
        Assert.Equal("Settings not saved", session.State.Notice.Title);
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
        Assert.Equal(SessionOutcomeKind.Completed, (await session.ConnectAsync("models")).Kind);
        Assert.Equal("Settings not saved", session.State.Notice.Title);
        session.SetLanguage("fr");
        Assert.Equal("fr", session.State.Language);
        Assert.Equal("Settings not saved", session.State.Notice.Title);
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
        await session.CloseAsync();
    }

    [Fact]
    public async Task StartupReadFailureCannotOverwritePreferencesAfterTheFileUnlocks()
    {
        var store = new AppSettingsStore(SettingsPath);
        var saved = new AppSettings
        {
            ModelId = "test",
            Language = "de",
            ModelsDirectory = Path.Combine(_settingsDirectory, "preferred-models"),
            Shortcut = new DictationShortcut(2, 0x44),
            PushToTalk = false,
            CollectUsage = false,
            CustomDictionary = "WinUI"
        };
        store.Save(saved);
        var previousDocument = File.ReadAllBytes(SettingsPath);
        DictationSession session;
        using (var lease = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            session = Create(new FakeEngine(), settings: store);
        }

        await session.InitializeAsync();
        Assert.Equal("Settings unavailable", session.State.Notice.Title);
        await session.MaintainModelsAsync(_ => Task.CompletedTask);
        Assert.Equal("Settings not saved", session.State.Notice.Title);
        Assert.Equal(previousDocument, File.ReadAllBytes(SettingsPath));
        await session.ConnectAsync("models");
        session.SetLanguage("fr");
        session.SetCustomDictionary("temporary phrase");
        Assert.Equal("fr", session.State.Language);
        Assert.Equal("temporary phrase", session.State.Settings.CustomDictionary);
        Assert.Equal("Settings not saved", session.State.Notice.Title);
        Assert.Equal(previousDocument, File.ReadAllBytes(SettingsPath));
        await session.CloseAsync();

        var restarted = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await restarted.InitializeAsync();
        Assert.Equal(saved, restarted.State.Settings);
        Assert.Equal("test", restarted.State.SelectedModel?.Id);
        await restarted.CloseAsync();
    }

    [Fact]
    public async Task FailedSaveWarnsAndKeepsTheChangeForThisSessionOnly()
    {
        var settings = new FakeSettingsStore { FailSaving = true };
        var models = new[] { Model, Model with { Id = "other" } };
        FakeEngine Engine() => new() { Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>(models) };
        var session = Create(Engine(), settings: settings);
        await session.InitializeAsync();
        session.SelectModel(1);
        Assert.Equal("other", session.State.SelectedModel?.Id);
        Assert.Equal(NoticeKind.Warning, session.State.Notice.Kind);
        Assert.Contains("unwritable settings", session.State.Notice.Message);
        session.SetLanguage("fr");
        Assert.Equal("fr", session.State.Language);
        Assert.Equal(NoticeKind.Warning, session.State.Notice.Kind);
        await session.CloseAsync();

        var restarted = Create(Engine(), settings: settings);
        await restarted.InitializeAsync();
        Assert.Equal("test", restarted.State.SelectedModel?.Id);
        Assert.Equal("", restarted.State.Language);
        await restarted.CloseAsync();
    }

    [Fact]
    public async Task LockedSettingsFileKeepsThePreviousSettingsAndCanBeSavedAfterRelease()
    {
        var session = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await session.InitializeAsync();
        session.SelectModel(0);
        var previous = File.ReadAllText(SettingsPath);
        using (var lease = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            session.SetLanguage("fr");
            Assert.Equal("fr", session.State.Language);
            Assert.Equal("Settings not saved", session.State.Notice.Title);
            Assert.Equal(NoticeKind.Warning, session.State.Notice.Kind);
            Assert.Equal(previous, File.ReadAllText(SettingsPath));
            Assert.Single(Directory.GetFiles(_settingsDirectory));
        }
        session.SetPushToTalk(false);
        await session.CloseAsync();

        var restarted = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await restarted.InitializeAsync();
        Assert.Equal("fr", restarted.State.Language);
        Assert.Equal("test", restarted.State.SelectedModel?.Id);
        Assert.False(restarted.State.Settings.PushToTalk);
        await restarted.CloseAsync();
    }

    [Fact]
    public async Task InvalidShortcutCannotReplaceTheCurrentOrSavedSettings()
    {
        var session = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await session.InitializeAsync();
        session.SetShortcut(new DictationShortcut(2, 0x44));
        session.SetLanguage("de");
        Assert.Throws<InvalidDataException>(() => session.SetShortcut(new DictationShortcut(0, 0x41)));
        Assert.Equal(new DictationShortcut(2, 0x44), session.State.Settings.Shortcut);
        await session.CloseAsync();
        var restarted = Create(new FakeEngine(), settings: new AppSettingsStore(SettingsPath));
        await restarted.InitializeAsync();
        Assert.Equal(new DictationShortcut(2, 0x44), restarted.State.Settings.Shortcut);
        Assert.Equal("de", restarted.State.Language);
        await restarted.CloseAsync();
    }

    [Fact]
    public async Task ActiveRecognitionRejectsPreferenceChangesBeforeSaving()
    {
        var settings = new FakeSettingsStore();
        var session = Create(new FakeEngine(), settings: settings);
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        Assert.Throws<InvalidOperationException>(() => session.SelectModel(0));
        Assert.Throws<InvalidOperationException>(() => session.SetLanguage("fr"));
        Assert.Throws<InvalidOperationException>(() => session.SetShortcut(new DictationShortcut(0, 0x79)));
        Assert.Throws<InvalidOperationException>(() => session.SetPushToTalk(false));
        Assert.Throws<InvalidOperationException>(() => session.SetUsageEnabled(false));
        Assert.Equal(0, settings.Saves);
        Assert.Equal("", session.State.Language);
        await session.CancelAsync();
        await recording;
        await session.CloseAsync();
    }

    [Fact]
    public async Task EmptyConnectionClearsSelectionAndRemainsDisconnected()
    {
        var connects = 0;
        var engine = new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>(++connects == 1 ? [Model] : [])
        };
        var session = Create(engine);
        await session.InitializeAsync();
        Assert.NotNull(session.State.SelectedModel);
        await session.ConnectAsync("empty");
        Assert.Equal(DictationPhase.Disconnected, session.State.Phase);
        Assert.Empty(session.State.Models);
        Assert.Equal(-1, session.State.SelectedIndex);
        Assert.Null(session.State.SelectedModel);
        Assert.False(session.State.CanStart);
        await session.CloseAsync();
    }

    [Fact]
    public async Task MaintenanceReleasesNativeOwnerBeforeMutationAndReconnects()
    {
        var previous = new FakeEngine();
        var replacement = new FakeEngine();
        var engines = new Queue<IRecognitionEngine>([previous, replacement]);
        var session = new DictationSession(_ => engines.Dequeue(), new FakeCaptureFactory(),
            new FakeInput(), new FakeUsageStore(), "models", new FakeSettingsStore());
        await session.InitializeAsync();
        var outcome = await session.MaintainModelsAsync(_ =>
        {
            Assert.Equal(1, previous.Disposals);
            Assert.Equal(DictationPhase.MaintainingModels, session.State.Phase);
            Assert.Empty(session.State.Models);
            Assert.Null(session.State.SelectedModel);
            return Task.CompletedTask;
        });
        Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(DictationPhase.Ready, session.State.Phase);
        Assert.NotNull(session.State.SelectedModel);
        await session.CloseAsync();
        Assert.Equal(1, replacement.Disposals);
    }

    [Fact]
    public async Task MaintenanceRemovingLastModelDoesNotFabricateReady()
    {
        var previous = new FakeEngine();
        var empty = new FakeEngine { Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([]) };
        var engines = new Queue<IRecognitionEngine>([previous, empty]);
        var session = new DictationSession(_ => engines.Dequeue(), new FakeCaptureFactory(),
            new FakeInput(), new FakeUsageStore(), "models", new FakeSettingsStore());
        await session.InitializeAsync();
        await session.MaintainModelsAsync(_ => Task.CompletedTask);
        Assert.Equal(DictationPhase.Disconnected, session.State.Phase);
        Assert.Null(session.State.SelectedModel);
        Assert.Empty(session.State.Models);
        Assert.Equal(1, previous.Disposals);
        Assert.Equal(1, empty.Disposals);
        await session.CloseAsync();
    }

    [Fact]
    public async Task CaptureAndMaintenanceCannotAcquireTheSameIdleSlot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new FakeCaptureFactory();
        var session = Create(new FakeEngine(), capture);
        await session.InitializeAsync();
        var maintenance = session.MaintainModelsAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartDictationAsync());
        Assert.Equal(0, capture.Starts);
        release.SetResult();
        await maintenance;
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        var invoked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.MaintainModelsAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }));
        Assert.False(invoked);
        await session.CancelAsync();
        await recording;
        await session.CloseAsync();
    }

    [Fact]
    public async Task FailedMaintenanceLeavesNoStaleNativeSelection()
    {
        var engine = new FakeEngine();
        var session = Create(engine);
        await session.InitializeAsync();
        var outcome = await session.MaintainModelsAsync(_ => throw new IOException("owned mutation failed"));
        Assert.Equal(SessionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(DictationPhase.Disconnected, session.State.Phase);
        Assert.Empty(session.State.Models);
        Assert.Null(session.State.SelectedModel);
        Assert.Equal(1, engine.Disposals);
        await session.CloseAsync();
    }

    [Fact]
    public async Task ReconnectionCleanupFailureClearsTheDetachedSelection()
    {
        var previous = new FakeEngine { FailDisposal = true };
        var empty = new FakeEngine { Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([]) };
        var engines = new Queue<IRecognitionEngine>([previous, empty]);
        var session = new DictationSession(_ => engines.Dequeue(), new FakeCaptureFactory(),
            new FakeInput(), new FakeUsageStore(), "models", new FakeSettingsStore());
        await session.InitializeAsync();
        var outcome = await session.ConnectAsync("empty");
        Assert.Equal(SessionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(DictationPhase.RecoveryRequired, session.State.Phase);
        Assert.Empty(session.State.Models);
        Assert.Null(session.State.SelectedModel);
        previous.FailDisposal = false;
        await session.CloseAsync();
    }

    [Fact]
    public async Task CancelledEmptyReconnectCannotRestoreTheDetachedSelection()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = new FakeEngine();
        var empty = new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([]),
            Disposing = async () => { entered.TrySetResult(); await release.Task; }
        };
        var engines = new Queue<IRecognitionEngine>([previous, empty]);
        var session = new DictationSession(_ => engines.Dequeue(), new FakeCaptureFactory(),
            new FakeInput(), new FakeUsageStore(), "models", new FakeSettingsStore());
        await session.InitializeAsync();
        var reconnect = session.ConnectAsync("empty");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.CancelAsync();
        release.SetResult();
        Assert.Equal(SessionOutcomeKind.Cancelled, (await reconnect).Kind);
        Assert.Equal(DictationPhase.Disconnected, session.State.Phase);
        Assert.Empty(session.State.Models);
        Assert.Null(session.State.SelectedModel);
        await session.CloseAsync();
    }

    [Fact]
    public async Task RejectedFolderMaintenanceCannotWritePreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalVoiceFolderTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new AppSettingsStore(Path.Combine(directory, "settings.json"));
            var original = Path.Combine(directory, "original");
            store.Save(new AppSettings { ModelsDirectory = original });
            var session = Create(new FakeEngine { ModelsDirectory = original }, settings: store);
            await session.InitializeAsync();
            var recording = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.MaintainModelsAsync(
                _ =>
                {
                    store.Save(new AppSettings { ModelsDirectory = Path.Combine(directory, "new") });
                    return Task.CompletedTask;
                },
                Path.Combine(directory, "new")));
            Assert.Equal(original, new AppSettingsStore(store.Path).Load().ModelsDirectory);
            Assert.Equal(DictationPhase.Recording, session.State.Phase);
            await session.CancelAsync();
            await recording;
            await session.CloseAsync();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static DictationSession Create(
        FakeEngine engine, FakeCaptureFactory? capture = null,
        FakeUsageStore? usage = null, FakeInput? input = null,
        IAppSettingsStore? settings = null) =>
        new(_ => engine, capture ?? new FakeCaptureFactory(), input ?? new FakeInput(),
            usage ?? new FakeUsageStore(), "models", settingsStore: settings ?? new FakeSettingsStore());

    [Fact]
    public async Task RecognitionUpdatesAreDeliveredBeforeCaptureEndsWithoutWaitingForSilence()
    {
        var source = new FakeCapture();
        var updates = new List<TranscriptUpdate>();
        var engine = new FakeEngine();
        var session = Create(engine, new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) });
        await session.InitializeAsync();
        var recording = session.StartDictationAsync(new TestProgress(updates.Add));
        await WaitForStateAsync(session, DictationPhase.Recording);
        engine.LastProgress!.Report(new("Speaker 0: hello", false, "hello"));
        Assert.False(recording.IsCompleted);
        Assert.Equal("hello", Assert.Single(updates).SpeechText);
        Assert.Equal("Speaker 0: hello", session.State.Transcript);
        engine.LastProgress.Report(new("Speaker 0: hello world", false, "hello world"));
        Assert.Equal(2, updates.Count);
        engine.LastProgress.Report(new("final display", true, "final speech"));
        Assert.Equal(2, updates.Count);
        await session.FinishAsync();
        var outcome = await recording;
        Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("hello world", outcome.Result!.SpeechText);
        Assert.Equal(1, engine.StreamStarts);
        await session.CloseAsync();
    }

    [Fact]
    public async Task LateOrCancelledTranscriptUpdatesCannotReachLiveDelivery()
    {
        var source = new FakeCapture();
        var updates = new List<TranscriptUpdate>();
        var engine = new FakeEngine();
        var session = Create(engine, new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) });
        await session.InitializeAsync();
        var recording = session.StartDictationAsync(new TestProgress(updates.Add));
        await WaitForStateAsync(session, DictationPhase.Recording);
        await session.CancelAsync();
        Assert.Equal(SessionOutcomeKind.Cancelled, (await recording).Kind);
        engine.LastProgress!.Report(new("stale speech", false, "stale speech"));
        Assert.Empty(updates);
        Assert.True(source.IsReleased);
        await session.CloseAsync();
    }

    private sealed class TestProgress(Action<TranscriptUpdate> report) : IProgress<TranscriptUpdate>
    {
        public void Report(TranscriptUpdate value) => report(value);
    }

    private static Task WaitForStateAsync(DictationSession session, DictationPhase phase)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SessionSnapshot state) { if (state.Phase == phase) { completion.TrySetResult(); } }
        session.StateChanged += Changed;
        if (session.State.Phase == phase) { completion.TrySetResult(); }
        return WaitAsync();
        async Task WaitAsync()
        {
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { session.StateChanged -= Changed; }
        }
    }

    private sealed class FakeEngine : IRecognitionEngine
    {
        public string ModelsDirectory { get; init; } = "models";
        public string Version => "test";
        public RecognitionResult Result { get; init; } = new("hello world", "hello world");
        public Func<CancellationToken, Task<IReadOnlyList<AudioModel>>>? Connecting { get; init; }
        public Func<Task>? Disposing { get; init; }
        public Func<AudioModel, ChannelReader<byte[]>, CancellationToken, Action?, Task<RecognitionResult>>? Streaming { get; init; }
        public TaskCompletionSource ConnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StreamEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StreamCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals;
        public bool FailDisposal { get; set; }
        public int StreamStarts;
        public int FinishedStreams;
        public IProgress<TranscriptUpdate>? LastProgress;
        public AudioModel? LastModel;
        public string? LastLanguage;
        public string? LastDictionary;
        public RecognitionOptions? LastOptions;
        public WaveAudio? LastAudio;
        public Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken token)
        {
            ConnectEntered.TrySetResult();
            return Connecting?.Invoke(token) ?? Task.FromResult<IReadOnlyList<AudioModel>>([Model]);
        }
        public Task<RecognitionResult> TranscribeAsync(
            AudioModel model, WaveAudio audio, RecognitionOptions options, IProgress<TranscriptUpdate>? progress, CancellationToken token)
        {
            LastAudio = audio;
            LastModel = model;
            LastLanguage = options.Language;
            LastDictionary = options.CustomDictionary;
            LastOptions = options;
            return Task.FromResult(Result);
        }
        public async Task<RecognitionResult> StreamAsync(
            AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options, IProgress<TranscriptUpdate>? progress,
            CancellationToken token, Action? onReady = null)
        {
            StreamStarts++;
            LastModel = model;
            LastLanguage = options.Language;
            LastDictionary = options.CustomDictionary;
            LastOptions = options;
            LastProgress = progress;
            StreamEntered.TrySetResult();
            if (Streaming is not null) { return await Streaming(model, audio, token, onReady); }
            onReady?.Invoke();
            await foreach (var _ in audio.ReadAllAsync(token)) { }
            token.ThrowIfCancellationRequested();
            FinishedStreams++;
            StreamCompleted.TrySetResult();
            return Result;
        }
        public int Prepares;
        public int Unloads;
        public AudioModel? PreparedModel;
        public RecognitionOptions? PreparedOptions;
        public Func<CancellationToken, Task>? Preparing { get; init; }
        public TaskCompletionSource PrepareCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UnloadCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PrepareAsync(AudioModel model, RecognitionOptions options, CancellationToken token)
        {
            Interlocked.Increment(ref Prepares);
            PreparedModel = model;
            PreparedOptions = options;
            PrepareCalled.TrySetResult();
            if (Preparing is not null) { await Preparing(token); }
        }
        public Task UnloadAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Unloads);
            UnloadCalled.TrySetResult();
            return Task.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            if (FailDisposal) { throw new IOException("native read lease still owned"); }
            if (Disposing is not null) { await Disposing(); }
        }
    }

    private sealed class FakeCaptureFactory : IAudioCaptureFactory
    {
        public int Starts;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task<IAudioCapture>>? Starting { get; init; }
        public int LastMicrophoneBoostDecibels;
        public Task<IAudioCapture> StartAsync(CancellationToken token, int microphoneBoostDecibels = 0)
        {
            token.ThrowIfCancellationRequested();
            LastMicrophoneBoostDecibels = microphoneBoostDecibels;
            Starts++;
            Entered.TrySetResult();
            return Starting?.Invoke(token) ?? Task.FromResult<IAudioCapture>(new FakeCapture());
        }
    }

    private sealed class FakeCapture : IAudioCapture
    {
        public Channel<byte[]> Packets { get; } = Channel.CreateUnbounded<byte[]>();
        public ChannelReader<byte[]> Audio => Packets.Reader;
        public double Seconds { get; init; }
        public double CapturedSeconds => Seconds;
        public bool IsReleased { get; private set; }
        public bool FailDisposal { get; set; }
        public int Disposals;
        public Func<Task>? Stopping { get; set; }
        public event Action<double>? LevelChanged { add { } remove { } }
        public Task StopAsync()
        {
            if (Stopping is not null) { return Stopping(); }
            Packets.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public void ReleaseOwnership() => IsReleased = true;
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            if (IsReleased) { return; }
            await StopAsync();
            if (FailDisposal) { throw new IOException("driver still owns capture"); }
            IsReleased = true;
        }
    }

    private sealed class FakeInput : IAudioInputReader
    {
        public WaveAudio Recording { get; init; } = new([0f], 16000, 1);
        public Func<CancellationToken, Task<byte[]>>? Replay { get; init; }
        public Task<WaveAudio> ReadRecordingAsync(string path, CancellationToken token) =>
            Task.FromResult(Recording);
        public Task<byte[]> ReadReplayAsync(string path, CancellationToken token) =>
            Replay?.Invoke(token) ?? Task.FromResult<byte[]>([0, 0]);
    }

    private sealed class FakeUsageStore : IUsageStore
    {
        public string Path => "usage.json";
        public UsageDocument Document { get; private set; } = new();
        public bool FailRecording { get; init; }
        public Task<UsageDocument> LoadAsync(CancellationToken token = default) => Task.FromResult(Document);
        public Task<UsageDocument> RecordAsync(UsageEntry entry, CancellationToken token = default)
        {
            if (FailRecording) { throw new IOException("unwritable usage"); }
            Document = Document with { Entries = Document.Entries.Append(entry).ToArray() };
            return Task.FromResult(Document);
        }
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public AppSettings Settings { get; set; } = new();
        public bool FailSaving { get; init; }
        public int Saves { get; private set; }
        public AppSettings Load() => Settings;
        public void Save(AppSettings settings)
        {
            Saves++;
            if (FailSaving) { throw new IOException("unwritable settings"); }
            Settings = settings;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            _ticks += amount.Ticks;
            foreach (var timer in _timers.ToArray()) { timer.Fire(_ticks); }
        }
        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            private long _next = due == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + due.Ticks;
            private TimeSpan _period = period;
            private bool _disposed;
            public bool Change(TimeSpan next, TimeSpan repeat)
            {
                if (_disposed) { return false; }
                _next = next == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + next.Ticks;
                _period = repeat;
                return true;
            }
            public void Fire(long now)
            {
                if (_disposed || now < _next) { return; }
                _next = _period == Timeout.InfiniteTimeSpan ? long.MaxValue : now + _period.Ticks;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
