using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class AutoDeviceQualifierTests
{
    [Fact]
    public void MissPreparesOnceThenProbesCpuAndGpuBeforePublishingAndPersisting()
    {
        using var fixture = new Fixture();
        var qualifier = fixture.Create();

        var ticket = fixture.Qualify(qualifier);

        Assert.Equal(["identity", "prepare", "Cpu", "Vulkan"], fixture.Events);
        Assert.Equal(NativeBackend.Vulkan, ticket.Backend);
        Assert.Equal(NativeDiagnosticSource.Qualification, qualifier.Current.Source);
        Assert.Equal(ticket.Selection, Assert.Single(DeviceBenchmark.ReadCache(fixture.Cache)));
        Assert.Equal(ticket.Selection.MeasuredAt, qualifier.Current.Comparison!.MeasuredAt);
        Assert.Contains("median of three", qualifier.Current.Summary);
    }

    [Fact]
    public void MemoryCachePrecedesDiskAndDiskHitNeverPreparesOrProbes()
    {
        using var fixture = new Fixture();
        var qualifier = fixture.Create();
        var ticket = fixture.Qualify(qualifier);
        File.WriteAllText(fixture.Cache, "corrupt");
        fixture.Events.Clear();

        Assert.Same(ticket, fixture.Qualify(qualifier));
        Assert.Equal(["identity"], fixture.Events);
        Assert.Equal(NativeDiagnosticSource.MemoryCache, qualifier.Current.Source);

        DeviceBenchmark.WriteCache(fixture.Cache, [ticket.Selection]);
        var fresh = fixture.Create();
        fixture.Events.Clear();
        Assert.Equal(ticket, fixture.Qualify(fresh));
        Assert.Equal(["identity"], fixture.Events);
        Assert.Equal(NativeDiagnosticSource.DiskCache, fresh.Current.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeIdentityIsRevalidatedBeforeEitherCacheTier(bool freshQualifier)
    {
        using var fixture = new Fixture();
        var qualifier = fixture.Create();
        var first = fixture.Qualify(qualifier);
        if (freshQualifier) qualifier = fixture.Create();
        fixture.Hardware = "changed driver";
        fixture.Events.Clear();

        var next = fixture.Qualify(qualifier);

        Assert.NotEqual(first.Selection.Key, next.Selection.Key);
        Assert.Equal(["identity", "prepare", "Cpu", "Vulkan"], fixture.Events);
        Assert.Equal(NativeDiagnosticSource.Qualification, qualifier.Current.Source);
    }

    [Fact]
    public void RuntimeFileReplacementInvalidatesWarmSelection()
    {
        using var fixture = new Fixture();
        var qualifier = fixture.Create();
        var first = fixture.Qualify(qualifier);
        File.WriteAllText(fixture.Input.Request.Library, "changed native content");
        fixture.Events.Clear();

        var next = fixture.Qualify(qualifier);

        Assert.NotEqual(first.Selection.Key, next.Selection.Key);
        Assert.Equal(["identity", "prepare", "Cpu", "Vulkan"], fixture.Events);
    }

    [Fact]
    public void AdmissionFailurePreventsProbesAndCachePublication()
    {
        using var fixture = new Fixture();
        var qualifier = fixture.Create();
        var failure = new NativeMemoryAdmissionException("budget rejected", 100, 1, 1);
        var actual = Assert.Throws<NativeMemoryAdmissionException>(() =>
            qualifier.Qualify(fixture.Input, () => throw failure, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Equal(["identity"], fixture.Events);
        Assert.False(File.Exists(fixture.Cache));
        Assert.Null(qualifier.Current.Comparison);
        Assert.Contains(failure.Message, qualifier.Current.Summary);
        fixture.Events.Clear();
        fixture.Qualify(qualifier);
        Assert.Equal(["identity", "prepare", "Cpu", "Vulkan"], fixture.Events);
    }

    [Theory]
    [InlineData(NativeBackend.Cpu)]
    [InlineData(NativeBackend.Vulkan)]
    public void FatalCalibrationNeverWritesASelection(NativeBackend failing)
    {
        using var fixture = new Fixture();
        fixture.Result = backend => backend == failing
            ? new(backend, null, "invalid calibration", Fatal: true)
            : new(backend, 100, null);
        var qualifier = fixture.Create();

        Assert.Throws<InvalidDataException>(() => fixture.Qualify(qualifier));

        Assert.Equal(failing == NativeBackend.Cpu ? new[] { "identity", "prepare", "Cpu" } :
            ["identity", "prepare", "Cpu", "Vulkan"], fixture.Events);
        Assert.False(File.Exists(fixture.Cache));
        Assert.Null(qualifier.Current.Comparison);
    }

    [Fact]
    public void CancellationAfterProbeDoesNotPublishOrCacheItsResult()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Result = backend =>
        {
            if (backend == NativeBackend.Vulkan) cancellation.Cancel();
            return new(backend, 100, null);
        };
        var qualifier = fixture.Create();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            qualifier.Qualify(fixture.Input, fixture.Prepare, cancellation.Token));

        Assert.False(File.Exists(fixture.Cache));
        Assert.Null(qualifier.Current.Comparison);
        fixture.Result = backend => new(backend, 100, null);
        fixture.Events.Clear();
        fixture.Qualify(qualifier);
        Assert.Equal(["identity", "prepare", "Cpu", "Vulkan"], fixture.Events);
    }

    [Fact]
    public void MissingIdentityClearsPriorPresentationWithoutProbing()
    {
        using var fixture = new Fixture();
        var qualifier = fixture.Create();
        fixture.Qualify(qualifier);
        File.Delete(fixture.Input.Request.Library);
        fixture.Events.Clear();

        Assert.Throws<FileNotFoundException>(() => fixture.Qualify(qualifier));

        Assert.Equal(["identity"], fixture.Events);
        Assert.Null(qualifier.Current.Comparison);
        Assert.Null(qualifier.Current.Source);
        Assert.StartsWith("Auto qualification did not select a device:", qualifier.Current.Summary);
    }

    [Fact]
    public void InvalidCacheIsAMissButUnreadableCacheFailsWithoutProbing()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Cache, "{broken");
        var ticket = fixture.Qualify(fixture.Create());
        Assert.Equal(ticket.Selection, Assert.Single(DeviceBenchmark.ReadCache(fixture.Cache)));
        fixture.Events.Clear();
        using var locked = new FileStream(fixture.Cache, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var qualifier = fixture.Create();

        Assert.Throws<IOException>(() => fixture.Qualify(qualifier));

        Assert.Equal(["identity"], fixture.Events);
        Assert.Null(qualifier.Current.Comparison);
    }

    [Fact]
    public void RejectionUsesTheOriginalTicketNotTheMostRecentDeviceOrDecoderMode()
    {
        using var fixture = new Fixture();
        var qualifier = fixture.Create();
        var original = fixture.Qualify(qualifier);
        var otherInput = fixture.Input with { Request = fixture.Input.Request with { Device = 1, UseDictionary = true } };
        var other = fixture.Qualify(qualifier, otherInput);

        var fallback = qualifier.RejectGpu(original, ExecutionFailure());

        Assert.Equal(NativeBackend.Cpu, fallback.Backend);
        Assert.Equal(original.Selection.MeasuredAt, fallback.Selection.MeasuredAt);
        Assert.Equal(NativeBackend.Cpu, fixture.Qualify(qualifier).Backend);
        Assert.Equal(other, fixture.Qualify(qualifier, otherInput));
        var disk = fixture.Create();
        fixture.Events.Clear();
        Assert.Equal(NativeBackend.Cpu, fixture.Qualify(disk).Backend);
        Assert.Equal(NativeBackend.Vulkan, fixture.Qualify(disk, otherInput).Backend);
        Assert.Equal(["identity", "identity"], fixture.Events);
    }

    [Fact]
    public void GpuFailureWithoutCpuRemovesOnlyThatTicketAndRethrowsTheOriginalFailure()
    {
        using var fixture = new Fixture();
        fixture.Result = backend => backend == NativeBackend.Cpu
            ? new(backend, null, "CPU unavailable") : new(backend, 90, null);
        var qualifier = fixture.Create();
        var rejected = fixture.Qualify(qualifier);
        var otherInput = fixture.Input with { ModelFingerprint = "another model fingerprint" };
        var other = fixture.Qualify(qualifier, otherInput);
        var failure = ExecutionFailure();

        Assert.Same(failure, Assert.Throws<NativeExecutionException>(() => qualifier.RejectGpu(rejected, failure)));

        Assert.Equal(other.Selection, Assert.Single(DeviceBenchmark.ReadCache(fixture.Cache)));
        Assert.Null(qualifier.Current.Comparison);
        Assert.Contains("no qualified CPU fallback", qualifier.Current.Summary);
        fixture.Events.Clear();
        fixture.Qualify(qualifier);
        Assert.Equal(["identity", "prepare", "Cpu", "Vulkan"], fixture.Events);
    }

    [Fact]
    public void DisabledPersistenceStillCachesWithinTheQualifier()
    {
        using var fixture = new Fixture();
        var qualifier = fixture.Create(persist: false);
        fixture.Qualify(qualifier);
        Assert.Contains("Persistence disabled; this session only.", qualifier.Current.Summary);
        fixture.Events.Clear();

        fixture.Qualify(qualifier);

        Assert.Equal(["identity"], fixture.Events);
        Assert.False(File.Exists(fixture.Cache));
    }

    [Fact]
    public void CacheWriteFailureDoesNotPreventQualifiedCpuRecovery()
    {
        using var fixture = new Fixture();
        var blockedPath = Path.Combine(fixture.Directory, "directory-not-file");
        System.IO.Directory.CreateDirectory(blockedPath);
        var qualifier = fixture.Create(cachePath: blockedPath);
        var ticket = fixture.Qualify(qualifier);
        Assert.Contains("Device cache could not be updated:", qualifier.Current.Summary);

        var fallback = qualifier.RejectGpu(ticket, ExecutionFailure());

        Assert.Equal(NativeBackend.Cpu, fallback.Backend);
        Assert.Contains("Device cache could not be updated:", qualifier.Current.Summary);
        fixture.Events.Clear();
        Assert.Equal(fallback, fixture.Qualify(qualifier));
        Assert.Equal(["identity"], fixture.Events);
    }

    [Fact]
    public void MeetingPaceIsCheckedForQualificationMemoryAndDiskSelections()
    {
        using var fixture = new Fixture();
        fixture.Result = backend => new(backend, backend == NativeBackend.Cpu ? 600 : 550, null);
        var input = fixture.MeetingInput;
        var qualifier = fixture.Create();

        foreach (var source in new[] { NativeDiagnosticSource.Qualification, NativeDiagnosticSource.MemoryCache, NativeDiagnosticSource.DiskCache })
        {
            if (source == NativeDiagnosticSource.DiskCache) qualifier = fixture.Create();
            var error = Assert.Throws<MeetingRealTimeException>(() => fixture.Qualify(qualifier, input));
            Assert.Equal(0.55, error.RealTimeFactor, 2);
            Assert.Equal(source, qualifier.Current.Source);
            Assert.Null(qualifier.Current.Comparison);
        }
    }

    [Fact]
    public void MeetingPaceIsRecheckedBeforeRetryingCpu()
    {
        using var fixture = new Fixture();
        fixture.Result = backend => new(backend, backend == NativeBackend.Cpu ? 600 : 100, null);
        var qualifier = fixture.Create();
        var ticket = fixture.Qualify(qualifier, fixture.MeetingInput);
        var attempts = 0;

        var error = Assert.Throws<MeetingRealTimeException>(() =>
            NativeAutoRecovery.Run<int>(true, new(Backend: ticket.Backend), CancellationToken.None,
                _ => { attempts++; throw ExecutionFailure(); }, failure => qualifier.RejectGpu(ticket, failure)));

        Assert.Equal(1, attempts);
        Assert.Equal(0.6, error.RealTimeFactor, 2);
        Assert.Equal(NativeBackend.Cpu, qualifier.Current.Comparison!.RecommendedBackend);
        Assert.Equal(NativeBackend.Cpu, Assert.Single(DeviceBenchmark.ReadCache(fixture.Cache)).Backend);
    }

    [Fact]
    public void MeetingSampleDurationBelongsToTheQualifiedIdentity()
    {
        using var fixture = new Fixture();
        fixture.Result = backend => new(backend, backend == NativeBackend.Cpu ? 600 : 500, null);
        var qualifier = fixture.Create();
        var first = fixture.Qualify(qualifier, fixture.MeetingInput);
        Assert.Equal(1000, first.SampleMilliseconds);
        Assert.Contains("0.50x", qualifier.Current.Summary);
        fixture.WriteSample(seconds: 2);

        var next = fixture.Qualify(qualifier, fixture.MeetingInput);

        Assert.NotEqual(first.Selection.Key, next.Selection.Key);
        Assert.Equal(2000, next.SampleMilliseconds);
        Assert.Contains("0.25x", qualifier.Current.Summary);
    }

    [Fact]
    public void NemotronStreamingNeverProbesVulkan()
    {
        using var fixture = new Fixture();
        var qualifier = fixture.Create();
        var input = fixture.MeetingInput with { Family = "nemotron_asr" };

        var ticket = fixture.Qualify(qualifier, input);

        Assert.Equal(["identity", "prepare", "Cpu"], fixture.Events);
        Assert.Equal(NativeBackend.Cpu, ticket.Backend);
        Assert.Contains("qualification skipped", qualifier.Current.Comparison!.GpuError);
    }

    [Theory]
    [InlineData("create the native recognition session on vulkan device 0", false)]
    [InlineData("transcribe the recording", false)]
    [InlineData("start native dictation", false)]
    [InlineData("process a dictation audio chunk", true)]
    public void RecoveryWaitsForCleanupAndNeverReplaysConsumedAudio(string operation, bool consumed)
    {
        using var fixture = new Fixture();
        var qualifier = fixture.Create();
        var ticket = fixture.Qualify(qualifier);
        var events = new List<string>();
        int Run() => NativeAutoRecovery.Run(true, new(Backend: ticket.Backend), CancellationToken.None,
            options => NativeOperation.Run(() =>
            {
                events.Add($"execute {options.Backend}");
                if (options.Backend == NativeBackend.Vulkan) throw new NativeExecutionException("device lost", operation);
                return 42;
            }, _ => events.Add($"cleanup {options.Backend}")),
            failure =>
            {
                events.Add("reject");
                qualifier.RejectGpu(ticket, failure);
            }, () => consumed);

        if (consumed)
        {
            Assert.Contains("Audio was not replayed", Assert.Throws<InvalidOperationException>(() => Run()).Message);
            Assert.Equal(["execute Vulkan", "cleanup Vulkan", "reject"], events);
        }
        else
        {
            Assert.Equal(42, Run());
            Assert.Equal(["execute Vulkan", "cleanup Vulkan", "reject", "execute Cpu", "cleanup Cpu"], events);
        }
        Assert.Equal(NativeBackend.Cpu, fixture.Qualify(qualifier).Backend);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("calibration")]
    [InlineData("cleanup")]
    [InlineData("cancelled")]
    public void IneligibleFailuresDoNotRejectTheQualifiedGpu(string kind)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var qualifier = fixture.Create();
        var ticket = fixture.Qualify(qualifier);
        Exception failure = kind switch
        {
            "memory" => new NativeExecutionException("out of memory", "start native dictation"),
            "calibration" => new InvalidDataException("invalid calibration"),
            "cleanup" => new AggregateException(ExecutionFailure()),
            _ => ExecutionFailure()
        };
        if (kind == "cancelled") cancellation.Cancel();
        var attempts = 0;

        var actual = Assert.ThrowsAny<Exception>(() =>
            NativeAutoRecovery.Run<int>(true, new(Backend: ticket.Backend), cancellation.Token,
                _ => { attempts++; throw failure; }, error => qualifier.RejectGpu(ticket, error)));

        Assert.Same(failure, actual);
        Assert.Equal(1, attempts);
        Assert.Equal(ticket, fixture.Qualify(qualifier));
        Assert.Equal(ticket.Selection, Assert.Single(DeviceBenchmark.ReadCache(fixture.Cache)));
    }

    [Theory]
    [InlineData(NativeBackend.Cpu)]
    [InlineData(NativeBackend.Vulkan)]
    public async Task ManualEnginePathsSkipQualificationWithoutNativeResources(NativeBackend backend)
    {
        var model = new AudioModel { Id = "manual", Mode = "streaming" };
        await using var engine = new NativeAudioEngine("missing.dll", "missing.json", "models");
        var options = new RecognitionOptions(Backend: backend);
        var channel = Channel.CreateBounded<byte[]>(1);
        Func<Task>[] paths =
        [
            () => engine.PrepareAsync(model, options, CancellationToken.None),
            () => engine.TranscribeAsync(model, new([0.1f], 16000, 1), options, null, CancellationToken.None),
            () => engine.StreamAsync(model, channel.Reader, options, null, CancellationToken.None)
        ];
        foreach (var path in paths)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(path);
            Assert.Contains("Connect the native backend", error.Message);
            Assert.Contains($"Manual {backend}", engine.DeviceSelectionSummary);
            Assert.Null(engine.DeviceComparison);
        }
    }

    private static NativeExecutionException ExecutionFailure() => new("device lost", "transcribe the recording");

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "Ansible-qualification-" + Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Directory, "cache.json");
        public List<string> Events { get; } = [];
        public string Hardware { get; set; } = "test hardware";
        public Func<NativeBackend, DeviceBenchmarkResult> Result { get; set; } =
            backend => new(backend, backend == NativeBackend.Cpu ? 100 : 90, null);
        public QualificationInput Input { get; }
        public QualificationInput MeetingInput => Input with { Request = Input.Request with { Mode = "streaming", History = 4096 } };

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var library = Path.Combine(Directory, "native.dll");
            File.WriteAllText(library, "native");
            Input = new(new(library, "catalog", "models", Path.Combine(Directory, "sample.wav"),
                "model", NativeBackend.Cpu, 0, 4, 512, "offline", 0), "model fingerprint", "kroko_asr");
            WriteSample(1);
        }

        public void WriteSample(int seconds) =>
            File.WriteAllBytes(Input.Request.Sample, WaveFixture.Create(1, 1, 16000, 16, new byte[32000 * seconds]));

        public AutoDeviceQualifier Create(bool persist = true, string? cachePath = null) =>
            new(persist ? cachePath ?? Cache : null, (request, _) =>
            {
                Events.Add(request.Backend.ToString());
                return Task.FromResult(Result(request.Backend));
            }, new DeviceBenchmarkIdentity(() => { Events.Add("identity"); return Hardware; }));

        public void Prepare() => Events.Add("prepare");
        public QualifiedDevice Qualify(AutoDeviceQualifier qualifier, QualificationInput? input = null) =>
            qualifier.Qualify(input ?? Input, Prepare, CancellationToken.None);

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
