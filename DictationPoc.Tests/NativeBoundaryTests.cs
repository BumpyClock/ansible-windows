using DictationPoc.Core;

namespace DictationPoc.Tests;

[Trait("Category", "NativeBoundary")]
public sealed class NativeBoundaryTests
{
    [Fact]
    public void RejectedAbiIsFreedAndCheckedAgainOnRetry()
    {
        var loader = new ModuleLoader { Abi = 0x010000 };
        var module = new NativeModule(loader);
        Assert.Throws<NotSupportedException>(() => module.Initialize("rejected.dll"));
        Assert.Throws<NotSupportedException>(() => module.Initialize("rejected.dll"));
        Assert.Equal(2, loader.AbiReads);
        Assert.Equal(2, loader.Frees);
        Assert.Equal(0, loader.Registrations);

        loader.Abi = 0x0200;
        module.Initialize("accepted.dll");
        module.Initialize("accepted.dll");
        Assert.Equal(3, loader.AbiReads);
        Assert.Equal(1, loader.Registrations);
        Assert.NotEqual(0, loader.Resolve!("audiocpp"));
        Assert.Equal(0, loader.Resolve("other.dll"));
        Assert.Throws<InvalidOperationException>(() => module.Initialize("different.dll"));
    }

    [Fact]
    public void ResolverFailureIsStableAndNeverPublishesTheCandidate()
    {
        var loader = new ModuleLoader { RegistrationError = new InvalidOperationException("resolver already installed") };
        var module = new NativeModule(loader);
        var first = Assert.Throws<InvalidOperationException>(() => module.Initialize("native.dll"));
        var second = Assert.Throws<InvalidOperationException>(() => module.Initialize("native.dll"));
        Assert.Same(first, second);
        Assert.Equal(1, loader.Loads);
        Assert.Equal(1, loader.Frees);
        Assert.Equal(0, loader.Resolve!("audiocpp"));
    }

    [Fact]
    public void MissingAbiExportDoesNotPoisonPublication()
    {
        var loader = new ModuleLoader { AbiError = new EntryPointNotFoundException("audiocpp_abi_version") };
        var module = new NativeModule(loader);
        Assert.Throws<EntryPointNotFoundException>(() => module.Initialize("native.dll"));
        Assert.Equal(1, loader.Frees);
        Assert.Equal(0, loader.Registrations);
        loader.AbiError = null;
        module.Initialize("native.dll");
        Assert.Equal(2, loader.Loads);
        Assert.Equal(1, loader.Registrations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CancellationAfterEachNativePreparationStopsTheNextStep(int cancelAt)
    {
        using var cancellation = new CancellationTokenSource();
        string[] stages = ["load", "inspect options", "create session", "start stream"];
        List<string> calls = [];
        Assert.Throws<OperationCanceledException>(() => NativeOperation.Run(() =>
        {
            for (var index = 0; index < stages.Length; index++)
            {
                NativeOperation.Step(cancellation.Token, () =>
                {
                    calls.Add(stages[index]);
                    if (index == cancelAt)
                        cancellation.Cancel();
                });
            }
            return "unexpected";
        }, success =>
        {
            Assert.False(success);
            calls.Add("invalidate");
        }));
        Assert.Equal(stages.Take(cancelAt + 1).Append("invalidate"), calls);
    }

    [Fact]
    public void CancellationBeforePreparationDoesNotCallNativeCode()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            NativeOperation.Step(cancellation.Token, () => throw new Xunit.Sdk.XunitException("Native code ran.")));
    }

    [Fact]
    public async Task InFlightSynchronousCallCompletesBeforeCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var cleaned = 0;
        var operation = Task.Run(() => NativeOperation.Run(() =>
        {
            NativeOperation.Step(cancellation.Token, () =>
            {
                entered.Set();
                Assert.True(finish.Wait(TimeSpan.FromSeconds(10)));
            });
            return true;
        }, _ => Interlocked.Exchange(ref cleaned, 1)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            Assert.Equal(0, Volatile.Read(ref cleaned));
        }
        finally
        {
            finish.Set();
        }
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await operation);
        Assert.Equal(1, Volatile.Read(ref cleaned));
    }

    [Fact]
    public void CleanSuccessStillResetsAndDisposesTheSession()
    {
        List<string> calls = [];
        var result = NativeOperation.Run(() => "speech", success =>
            NativeOperation.Cleanup(success, () => calls.Add("reset"),
                () => calls.Add("dispose"), () => calls.Add("invalidate model")));
        Assert.Equal("speech", result);
        Assert.Equal(["reset", "dispose"], calls);
    }

    [Fact]
    public void PartialStartAndResetFailuresPreserveBothErrorsAndInvalidateState()
    {
        var primary = new InvalidOperationException("partial stream start");
        var cleanup = new IOException("reset failed");
        List<string> calls = [];
        var error = Assert.Throws<AggregateException>(() => NativeOperation.Run<string>(() => throw primary, success =>
            NativeOperation.Cleanup(success, () => throw cleanup,
                () => calls.Add("dispose"), () => calls.Add("invalidate model"))));
        Assert.Same(primary, error.InnerExceptions[0]);
        Assert.Same(cleanup, error.InnerExceptions[1]);
        Assert.Equal(["dispose", "invalidate model"], calls);
    }

    [Fact]
    public void ResetFailureAfterRecognitionDoesNotReturnSuccessfulSpeech()
    {
        List<string> calls = [];
        var error = Assert.Throws<IOException>(() => NativeOperation.Run(() => "speech", success =>
            NativeOperation.Cleanup(success, () => throw new IOException("reset failed"),
                () => calls.Add("dispose"), () => calls.Add("invalidate model"))));
        Assert.Equal("reset failed", error.Message);
        Assert.Equal(["dispose", "invalidate model"], calls);
    }

    [Fact]
    public void DisposalFailureAlsoInvalidatesModelState()
    {
        var invalidated = false;
        var error = Assert.Throws<IOException>(() => NativeOperation.Cleanup(true, null,
            () => throw new IOException("dispose failed"), () => invalidated = true));
        Assert.Equal("dispose failed", error.Message);
        Assert.True(invalidated);
    }

    [Fact]
    public void AuthoritativeSegmentsExcludeSpeakerLabelsButPreserveSpokenSpeakerPhrases()
    {
        var result = NativeTranscript.Normalize("vibevoice_asr", "Speaker 0: I said Speaker 0.",
            ["I said Speaker 0."], ["I said Speaker 0."]);
        Assert.Equal("Speaker 0: I said Speaker 0.", result.DisplayText);
        Assert.Equal("I said Speaker 0.", result.SpeechText);
        Assert.Equal(4, result.SpokenWords);
    }

    [Fact]
    public void SpeakerTurnsAreUsedWithoutDuplicatingSegmentText()
    {
        var result = NativeTranscript.Normalize("vibevoice_asr_streaming", "Speaker 0: hello. Speaker 1: world.",
            [], ["hello.", "world."]);
        Assert.Equal("hello. world.", result.SpeechText);
        Assert.Equal(2, result.SpokenWords);
    }

    [Fact]
    public void MissingOrIncompleteAnnotatedSpeechIsUnknownNotZero()
    {
        var absent = NativeTranscript.Normalize("vibevoice_asr", "Speaker 0:", [], []);
        var incomplete = NativeTranscript.Normalize("vibevoice_asr", "Speaker 0: hello", ["hello", ""], []);
        Assert.Null(absent.SpeechText);
        Assert.Null(absent.SpokenWords);
        Assert.Null(incomplete.SpeechText);
        Assert.Null(incomplete.SpokenWords);
    }

    [Fact]
    public void PlainSpeechIsNotRegexStripped()
    {
        var result = NativeTranscript.Normalize("moonshine_asr", "Speaker 0 is what I said.", [], []);
        Assert.Equal("Speaker 0 is what I said.", result.SpeechText);
        Assert.Equal(6, result.SpokenWords);
    }

    [Fact]
    public void MemoryAdmissionAlwaysIncludesWeightsAlongsideInputCopies()
    {
        var small = NativeMemory.EstimateRequired(60_000_000, 1024, 512);
        var large = NativeMemory.EstimateRequired(60_000_000, NativeMemory.MaximumDecodedBytes, 512);
        Assert.Equal(1_171_544_896, small);
        Assert.Equal(3 * (NativeMemory.MaximumDecodedBytes - 1024), large - small);
    }

    [Fact]
    public void SuccessfulCleanupCannotExemptTheSameModelsNextSessionFromWeightAdmission()
    {
        const ulong fullBudget = 1_171_544_896;
        const ulong weightFreeBudget = 1_102_544_896;
        var available = fullBudget;
        var cachedModel = new object();
        var selectedModel = cachedModel;
        var created = 0;
        var active = false;
        var resets = 0;
        var invalidations = 0;

        bool CreateSession()
        {
            Assert.Same(cachedModel, selectedModel);
            NativeMemory.CheckBudget(60_000_000, 1024, 512, available, available);
            created++;
            active = true;
            return true;
        }

        void Cleanup(bool success) => NativeOperation.Cleanup(success,
            active ? () => resets++ : null, () => active = false, () => invalidations++);

        Assert.True(NativeOperation.Run(CreateSession, Cleanup));
        Assert.False(active);
        Assert.Equal(1, resets);
        Assert.Equal(0, invalidations);
        available = weightFreeBudget;

        var error = Assert.Throws<InvalidOperationException>(() => NativeOperation.Run(CreateSession, Cleanup));
        Assert.Contains("available memory", error.Message);
        Assert.Equal(1, created);
        Assert.False(active);
        Assert.Equal(1, resets);
        Assert.Equal(1, invalidations);
    }

    [Theory]
    [InlineData(1_171_544_895UL, 1_171_544_896UL)]
    [InlineData(1_171_544_896UL, 1_171_544_895UL)]
    public void SessionAdmissionRequiresBothPhysicalMemoryAndCommitCapacity(ulong physical, ulong commit) =>
        Assert.Throws<InvalidOperationException>(() => NativeMemory.CheckBudget(60_000_000, 1024, 512, physical, commit));

    private sealed class ModuleLoader : INativeModuleLoader
    {
        public uint Abi { get; set; } = 0x0200;
        public Exception? AbiError { get; set; }
        public Exception? RegistrationError { get; set; }
        public int Loads { get; private set; }
        public int AbiReads { get; private set; }
        public int Registrations { get; private set; }
        public int Frees { get; private set; }
        public Func<string, nint>? Resolve { get; private set; }
        public nint Load(string path) => ++Loads;
        public uint ReadAbi(nint handle)
        {
            AbiReads++;
            if (AbiError is not null) throw AbiError;
            return Abi;
        }
        public void RegisterResolver(Func<string, nint> resolve)
        {
            Resolve = resolve;
            Registrations++;
            Assert.Equal(0, resolve("audiocpp"));
            if (RegistrationError is not null) throw RegistrationError;
        }
        public void Free(nint handle) => Frees++;
    }
}
