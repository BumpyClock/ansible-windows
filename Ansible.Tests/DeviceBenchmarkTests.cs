using System.Diagnostics;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class DeviceBenchmarkTests
{
    [Fact]
    public void AutoIsDefaultAndExistingEnumValuesRemainStable()
    {
        Assert.Equal(NativeBackend.Auto, new RecognitionOptions().Backend);
        Assert.Equal(0, (int)NativeBackend.Cpu);
        Assert.Equal(1, (int)NativeBackend.Vulkan);
        new RecognitionOptions().ValidateBackend();
    }

    [Theory]
    [InlineData(100, 100, NativeBackend.Vulkan)]
    [InlineData(100, 105, NativeBackend.Vulkan)]
    [InlineData(100, 106, NativeBackend.Cpu)]
    [InlineData(120, 90, NativeBackend.Vulkan)]
    public void SelectsFasterWorkingDeviceWithGpuNearTiePreference(double cpu, double gpu, NativeBackend expected)
    {
        var result = DeviceBenchmark.Select("key", new(NativeBackend.Cpu, cpu, null), new(NativeBackend.Vulkan, gpu, null));
        Assert.Equal(expected, result.Backend);
        Assert.Contains($"{cpu:F1} ms", result.Summary);
        Assert.Contains($"{gpu:F1} ms", result.Summary);
    }

    [Theory]
    [InlineData(true, NativeBackend.Vulkan)]
    [InlineData(false, NativeBackend.Cpu)]
    public void SelectsOnlyWorkingDeviceAndReportsOtherFailure(bool cpuFailed, NativeBackend expected)
    {
        var result = DeviceBenchmark.Select("key", new(NativeBackend.Cpu, cpuFailed ? null : 100, cpuFailed ? "CPU failed" : null),
            new(NativeBackend.Vulkan, cpuFailed ? 100 : null, cpuFailed ? null : "GPU crashed"));
        Assert.Equal(expected, result.Backend);
        Assert.Contains(cpuFailed ? "CPU failed" : "GPU crashed", result.Summary);
    }

    [Fact]
    public void BothFailedAndIntegrityFailureAreNotSuccessShapedDefaults()
    {
        Assert.Throws<InvalidOperationException>(() => DeviceBenchmark.Select("key",
            new(NativeBackend.Cpu, null, "failed"), new(NativeBackend.Vulkan, null, "crashed")));
        Assert.Throws<InvalidDataException>(() => DeviceBenchmark.Select("key",
            new(NativeBackend.Cpu, 1, null), new(NativeBackend.Vulkan, null, "integrity failed", true)));
    }

    [Fact]
    public void NativeStatusClassesArePreservedAndControlQualificationAndRetry()
    {
        foreach (var status in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 99 })
        {
            var failure = new NativeExecutionException("opaque native failure", "transcribe the recording", status);
            var fatal = status is not 4 and not 7;
            Assert.Equal(status, failure.StatusCode);
            Assert.Equal(status == 5, failure.IsMemoryFailure);
            Assert.Equal(fatal, failure.IsQualificationFatal);
            Assert.Equal(!fatal, failure.CanRetry);
            var workerResult = DeviceBenchmarkWorker.ClassifyFailure(NativeBackend.Vulkan, failure);
            Assert.Equal(fatal, workerResult.Fatal);
            if (fatal)
            {
                Assert.Throws<InvalidDataException>(() => DeviceBenchmark.Select("status-class",
                    new(NativeBackend.Cpu, 100, null), workerResult));
                var attempts = 0;
                Assert.Same(failure, Assert.Throws<NativeExecutionException>(() =>
                    NativeAutoRecovery.Run<int>(true, new(Backend: NativeBackend.Vulkan), CancellationToken.None,
                        _ => { attempts++; throw failure; }, _ => Assert.Fail("fatal ABI status must not blacklist GPU"))));
                Assert.Equal(1, attempts);
            }
            else
            {
                Assert.Equal(NativeBackend.Cpu, DeviceBenchmark.Select("status-class",
                    new(NativeBackend.Cpu, 100, null), workerResult).Backend);
            }
        }
    }

    [Theory]
    [InlineData("Nemotron ASR decoder graph allocation failed")]
    [InlineData("failed to allocate Nemotron ASR graph")]
    [InlineData("backend buffer allocation failed")]
    [InlineData("vocos CUDA graph alloc failed")]
    [InlineData("Nemotron ASR decoder failed to create graph allocator")]
    [InlineData("Nemotron ASR decoder failed to create joint graph allocator")]
    [InlineData("Qwen3 ASR prefill graph does not fit in device memory at this size")]
    public void GenericRuntimeAllocationFailureAbortsQualificationAndCannotBlacklistGpu(string message)
    {
        var failure = new NativeExecutionException(message, "process a dictation audio chunk", (int)NativeStatus.Runtime);
        Assert.True(failure.IsMemoryFailure);
        Assert.False(failure.CanRetry);
        var workerResult = DeviceBenchmarkWorker.ClassifyFailure(NativeBackend.Vulkan, failure);
        Assert.True(workerResult.Fatal);
        Assert.Throws<InvalidDataException>(() => DeviceBenchmark.Select("allocation",
            new(NativeBackend.Cpu, 100, null), workerResult));
        var attempts = 0;
        Assert.Same(failure, Assert.Throws<NativeExecutionException>(() =>
            NativeAutoRecovery.Run<int>(true, new(Backend: NativeBackend.Vulkan), CancellationToken.None,
                _ => { attempts++; throw failure; }, _ => Assert.Fail("allocation failure must not poison device selection"))));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void ConsumedStreamingFailureDoesNotReplayAndUpdatesNextDevice()
    {
        var attempts = new List<NativeBackend>();
        var rejected = false;
        var error = Assert.Throws<InvalidOperationException>(() => NativeAutoRecovery.Run<int>(true,
            new(Backend: NativeBackend.Vulkan), CancellationToken.None, backend =>
            {
                attempts.Add(backend.Backend);
                throw new NativeExecutionException("device lost", "process a dictation audio chunk");
            }, _ => rejected = true, () => true));
        Assert.Equal([NativeBackend.Vulkan], attempts);
        Assert.True(rejected);
        Assert.Contains("Audio was not replayed", error.Message);
    }

    [Fact]
    public void PreInputFailureCanRetryCpuOnce()
    {
        var attempts = new List<NativeBackend>();
        var result = NativeAutoRecovery.Run(true, new(Backend: NativeBackend.Vulkan), CancellationToken.None, backend =>
        {
            attempts.Add(backend.Backend);
            if (backend.Backend == NativeBackend.Vulkan)
                throw new NativeExecutionException("device lost", "start native dictation");
            return "CPU speech";
        }, _ => { }, () => false);
        Assert.Equal("CPU speech", result);
        Assert.Equal([NativeBackend.Vulkan, NativeBackend.Cpu], attempts);
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("teardown")]
    [InlineData("request")]
    [InlineData("cancel")]
    [InlineData("memory")]
    public void NonExecutionOrOwnershipFailureNeverRetries(string kind)
    {
        var count = 0;
        Exception failure = kind switch
        {
            "validation" => new InvalidDataException("invalid audio"),
            "teardown" => new AggregateException(new NativeExecutionException("reset failed")),
            "request" => new NativeExecutionException("invalid request", "set recognition context"),
            "memory" => new NativeExecutionException("VK_ERROR_OUT_OF_DEVICE_MEMORY", "create the native recognition session"),
            _ => new OperationCanceledException()
        };
        var actual = Assert.ThrowsAny<Exception>(() => NativeAutoRecovery.Run<int>(true, new(Backend: NativeBackend.Vulkan),
            CancellationToken.None, _ => { count++; throw failure; }, _ => Assert.Fail("must not blacklist GPU")));
        Assert.Same(failure, actual);
        Assert.Equal(1, count);
    }

    [Fact]
    public void CancellationAndManualBackendNeverFallback()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        foreach (var automatic in new[] { false, true })
        {
            var count = 0;
            Assert.Throws<NativeExecutionException>(() => NativeAutoRecovery.Run<int>(automatic, new(Backend: NativeBackend.Vulkan),
                canceled.Token, _ => { count++; throw new NativeExecutionException("lost", "transcribe the recording"); },
                _ => Assert.Fail("must not blacklist GPU")));
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public void CacheRoundTripKeepsModeKeysSeparateAndDoesNotReturnStaleKey()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".benchmark-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "cache.json");
            var offline = DeviceBenchmark.Select("offline-hash", new(NativeBackend.Cpu, 10, null), new(NativeBackend.Vulkan, 9, null));
            var streaming = DeviceBenchmark.Select("streaming-hash", new(NativeBackend.Cpu, 10, null), new(NativeBackend.Vulkan, 20, null));
            DeviceBenchmark.WriteCache(path, [offline, streaming]);
            var entries = DeviceBenchmark.ReadCache(path);
            Assert.Equal(NativeBackend.Vulkan, entries.Single(value => value.Key == "offline-hash").Backend);
            Assert.Equal(NativeBackend.Cpu, entries.Single(value => value.Key == "streaming-hash").Backend);
            Assert.DoesNotContain(entries, value => value.Key == "changed-native-hash");
            foreach (var corrupt in new[] { "{broken", "null", "[null]", "[{}]", new string('x', 1024 * 1024 + 1) })
            {
                File.WriteAllText(path, corrupt);
                Assert.Empty(DeviceBenchmark.ReadCache(path));
                DeviceBenchmark.WriteCache(path, [offline]);
                Assert.Equal(offline, Assert.Single(DeviceBenchmark.ReadCache(path)));
            }
            DeviceBenchmark.WriteCache(path, [offline with { Cpu = new(NativeBackend.Cpu, null, "failed"),
                Gpu = new(NativeBackend.Vulkan, null, "failed") }]);
            Assert.Empty(DeviceBenchmark.ReadCache(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CacheIdentityChangesWithModeHistoryModelNativeSampleAndDeviceSettings()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".benchmark-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var dll = Path.Combine(directory, "native.dll");
            var sample = Path.Combine(directory, "sample.wav");
            File.WriteAllText(dll, "native one");
            File.WriteAllText(sample, "sample one");
            var request = new DeviceBenchmarkRequest(dll, "catalog", "models", sample, "moonshine", NativeBackend.Cpu, 0, 4, 512, "offline", 0);
            string Key(DeviceBenchmarkRequest input, string hash = "model-sha") => DeviceBenchmark.Key(input, hash, CancellationToken.None);
            var original = Key(request);
            Assert.Equal(original, Key(request with
            {
                Library = Path.GetRelativePath(Directory.GetCurrentDirectory(), dll),
                Sample = Path.GetRelativePath(Directory.GetCurrentDirectory(), sample)
            }));
            Assert.NotEqual(original, Key(request with { Mode = "streaming" }));
            Assert.NotEqual(original, Key(request with { History = 4096 }));
            Assert.NotEqual(original, Key(request with { UseDictionary = true }));
            Assert.NotEqual(original, Key(request with { Device = 1 }));
            Assert.NotEqual(original, Key(request with { Threads = 2 }));
            Assert.NotEqual(original, Key(request, "changed-model-sha"));
            Assert.NotEqual(Key(request, "asr-sha|diar-one|low"), Key(request, "asr-sha|diar-two|low"));
            Assert.NotEqual(Key(request, "asr-sha|diar-one|low"), Key(request, "asr-sha|diar-one|very_low"));
            Assert.Equal(original, Key(request with { Backend = NativeBackend.Vulkan }));
            File.WriteAllText(dll, "native two");
            Assert.NotEqual(original, Key(request));
            File.WriteAllText(dll, "native one");
            File.WriteAllText(sample, "sample two");
            Assert.NotEqual(original, Key(request));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MissingAutoResourcesFailExplicitly()
    {
        await using var engine = new NativeAudioEngine("missing.dll", "missing.json", "models");
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.TranscribeAsync(new() { Id = "test" },
            new([0.1f], 16000, 1), new(), null, CancellationToken.None));
    }

    [Fact]
    public async Task OwnedProbeTimeoutTerminatesAndJoinsProcess()
    {
        var start = PowerShell("Start-Sleep -Seconds 60");
        var result = await DeviceBenchmark.RunProcessAsync(start, NativeBackend.Vulkan, CancellationToken.None, TimeSpan.FromMilliseconds(300));
        Assert.False(result.Working);
        Assert.False(result.Fatal);
        Assert.Contains("terminated", result.Error);
        Assert.Equal(NativeBackend.Cpu, DeviceBenchmark.Select("timed-out-gpu",
            new(NativeBackend.Cpu, 100, null), result).Backend);
    }

    [Fact]
    public async Task ProbeCrashAndMalformedSuccessAreRejected()
    {
        var crash = await DeviceBenchmark.RunProcessAsync(PowerShell("exit 77"), NativeBackend.Vulkan, CancellationToken.None);
        Assert.False(crash.Working);
        Assert.False(crash.Fatal);
        Assert.Contains("77", crash.Error);
        Assert.Equal(NativeBackend.Cpu, DeviceBenchmark.Select("crashed-gpu",
            new(NativeBackend.Cpu, 100, null), crash).Backend);
        await Assert.ThrowsAsync<InvalidDataException>(() => DeviceBenchmark.RunProcessAsync(
            PowerShell("exit 0"), NativeBackend.Vulkan, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => DeviceBenchmark.RunProcessAsync(
            PowerShell("Write-Output 'ANSIBLE_DEVICE_RESULT:{\"Backend\":1,\"Milliseconds\":0,\"Error\":null,\"Fatal\":false}'"),
            NativeBackend.Vulkan, CancellationToken.None));
    }

    [Theory]
    [InlineData("nemotron_asr", "streaming", false)]
    [InlineData("nemotron_asr", "offline", true)]
    [InlineData("moonshine_asr", "streaming", true)]
    public async Task QualificationDoesNotLaunchIneligibleGpu(string family, string mode, bool gpuEligible)
    {
        var attempts = new List<NativeBackend>();
        var selection = await DeviceBenchmark.QualifyAsync("key", family, mode, backend =>
        {
            attempts.Add(backend);
            return Task.FromResult(new DeviceBenchmarkResult(backend, 100, null));
        }, CancellationToken.None);
        Assert.Equal(gpuEligible ? [NativeBackend.Cpu, NativeBackend.Vulkan] : new[] { NativeBackend.Cpu }, attempts);
        Assert.Equal(gpuEligible ? NativeBackend.Vulkan : NativeBackend.Cpu, selection.Backend);
        if (!gpuEligible) Assert.Contains("qualification skipped", selection.Gpu.Error);
    }

    [Fact]
    public void GpuFailureWithoutQualifiedCpuPreservesOriginalErrorAndDoesNotRetry()
    {
        var prior = DeviceBenchmark.Select("key", new(NativeBackend.Cpu, null, "CPU failed"), new(NativeBackend.Vulkan, 100, null));
        var failure = new NativeExecutionException("GPU failed", "transcribe the recording");
        var attempts = 0;
        var actual = Assert.Throws<NativeExecutionException>(() => NativeAutoRecovery.Run<int>(true,
            new(Backend: NativeBackend.Vulkan), CancellationToken.None,
            _ => { attempts++; throw failure; }, error => DeviceBenchmark.CpuFallback(prior, error)));
        Assert.Same(failure, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void CpuFallbackPreservesTheOriginalBenchmarkTimestamp()
    {
        var measuredAt = new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero);
        var prior = DeviceBenchmark.Select("key", new(NativeBackend.Cpu, 100, null), new(NativeBackend.Vulkan, 90, null))
            with { MeasuredAt = measuredAt };

        var fallback = DeviceBenchmark.CpuFallback(prior, new NativeExecutionException("GPU failed", "transcribe the recording"));

        Assert.Equal(NativeBackend.Cpu, fallback.Backend);
        Assert.Equal(measuredAt, fallback.MeasuredAt);
        Assert.Equal(100, fallback.Cpu.Milliseconds);
    }

    [Fact]
    public void UnwritableCacheDoesNotPreventQualifiedCpuRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Ansible-device-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var prior = DeviceBenchmark.Select("key", new(NativeBackend.Cpu, 100, null), new(NativeBackend.Vulkan, 90, null));
            string? warning = null;
            var attempts = new List<NativeBackend>();
            var result = NativeAutoRecovery.Run(true, new(Backend: NativeBackend.Vulkan), CancellationToken.None, options =>
            {
                attempts.Add(options.Backend);
                if (options.Backend == NativeBackend.Vulkan) throw new NativeExecutionException("GPU failed", "transcribe the recording");
                return "recognized speech";
            }, error => warning = DeviceBenchmark.TryUpdateCache(directory, prior.Key, DeviceBenchmark.CpuFallback(prior, error)));
            Assert.Equal("recognized speech", result);
            Assert.Equal([NativeBackend.Vulkan, NativeBackend.Cpu], attempts);
            Assert.Contains("could not be updated", warning);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ProbeDrainsBothStreamsAndReadsOnlyTheBoundedResultProtocol()
    {
        var result = await DeviceBenchmark.RunProcessAsync(PowerShell(
            "Write-Output ('x' * 100000); [Console]::Error.Write(('e' * 100000)); " +
            "Write-Output 'ANSIBLE_DEVICE_RESULT:{\"Backend\":1,\"Milliseconds\":123.5,\"Error\":null,\"Fatal\":false}'"),
            NativeBackend.Vulkan, CancellationToken.None);
        Assert.True(result.Working);
        Assert.Equal(123.5, result.Milliseconds);
        Assert.Equal(NativeBackend.Vulkan, result.Backend);
    }

    [Fact]
    public async Task OwnedProbeCancellationIsNotADeviceFailure()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeviceBenchmark.RunProcessAsync(
            PowerShell("Start-Sleep -Seconds 60"), NativeBackend.Vulkan, cancellation.Token));
    }

    private static ProcessStartInfo PowerShell(string script)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(value);
        return start;
    }
}
