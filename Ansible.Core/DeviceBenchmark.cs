using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Ansible.Core;

internal sealed record DeviceBenchmarkRequest(string Library, string Catalog, string Models, string Sample,
    string ModelId, NativeBackend Backend, int Device, int Threads, int Headroom, string Mode, int History);

internal sealed record DeviceBenchmarkResult(NativeBackend Backend, double? Milliseconds, string? Error, bool Fatal = false)
{
    public bool Working => Error is null && Milliseconds is > 0 && double.IsFinite(Milliseconds.Value);
}

internal sealed record DeviceBenchmarkSelection(string Key, DeviceBenchmarkResult Cpu, DeviceBenchmarkResult Gpu,
    NativeBackend Backend, DateTimeOffset MeasuredAt)
{
    public string Summary => $"Auto selected {Backend}. CPU: {Describe(Cpu)}; Vulkan: {Describe(Gpu)}. " +
        $"Public sample measured {MeasuredAt:u}; loading and warmup excluded.";
    private static string Describe(DeviceBenchmarkResult result) =>
        result.Working ? $"{result.Milliseconds:F1} ms" : result.Error ?? "invalid measurement";
}

[JsonSerializable(typeof(DeviceBenchmarkRequest))]
[JsonSerializable(typeof(DeviceBenchmarkResult))]
[JsonSerializable(typeof(List<DeviceBenchmarkSelection>))]
internal partial class DeviceBenchmarkJson : JsonSerializerContext;

internal static class DeviceBenchmark
{
    internal const string ResultPrefix = "ANSIBLE_DEVICE_RESULT:";
    internal static readonly TimeSpan DeviceDeadline = TimeSpan.FromMinutes(5);
    private const int OutputLimit = 65536;

    internal static async Task<DeviceBenchmarkSelection> QualifyAsync(string key, string? family, string mode,
        Func<NativeBackend, Task<DeviceBenchmarkResult>> probe, CancellationToken token)
    {
        var cpu = await probe(NativeBackend.Cpu).ConfigureAwait(false);
        if (cpu.Fatal) throw new InvalidDataException($"CPU device qualification failed: {cpu.Error}");
        token.ThrowIfCancellationRequested();
        // An isolated success cannot qualify a combination known to crash in-process.
        var gpu = family == "nemotron_asr" && mode == "streaming"
            ? new DeviceBenchmarkResult(NativeBackend.Vulkan, null, "Nemotron Vulkan streaming is unsafe in-process; qualification skipped.")
            : await probe(NativeBackend.Vulkan).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return Select(key, cpu, gpu);
    }

    internal static DeviceBenchmarkSelection CpuFallback(DeviceBenchmarkSelection prior, NativeExecutionException error)
    {
        if (!prior.Cpu.Working) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return Select(prior.Key, prior.Cpu,
            new(NativeBackend.Vulkan, null, $"real recognition failed after qualification: {error.Message}"));
    }

    internal static string? TryUpdateCache(string? path, string key, DeviceBenchmarkSelection? selection)
    {
        try
        {
            var entries = ReadCache(path);
            entries.RemoveAll(value => value.Key == key);
            if (selection is not null) entries.Add(selection);
            WriteCache(path, entries);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return $"Device cache could not be updated: {error.Message}";
        }
    }

    internal static DeviceBenchmarkSelection Select(string key, DeviceBenchmarkResult cpu, DeviceBenchmarkResult gpu)
    {
        if (cpu.Backend != NativeBackend.Cpu || gpu.Backend != NativeBackend.Vulkan)
            throw new InvalidDataException("Benchmark device identity does not match the requested backend.");
        if (cpu.Fatal || gpu.Fatal)
            throw new InvalidDataException($"Device qualification stopped: CPU: {cpu.Error}; Vulkan: {gpu.Error}");
        if (!cpu.Working && !gpu.Working)
            throw new InvalidOperationException($"Neither device passed qualification. CPU: {cpu.Error}; Vulkan: {gpu.Error}");
        // Differences below five percent are not enough evidence to override the GPU preference.
        var backend = gpu.Working && (!cpu.Working || gpu.Milliseconds <= cpu.Milliseconds * 1.05)
            ? NativeBackend.Vulkan : NativeBackend.Cpu;
        return new(key, cpu, gpu, backend, DateTimeOffset.UtcNow);
    }

    internal static string Key(DeviceBenchmarkRequest request, string modelHash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var identity = new StringBuilder($"benchmark-v3|{modelHash}|{request.ModelId}|{request.Mode}|{request.History}|" +
            $"{request.Device}|{request.Threads}|{request.Headroom}|{Environment.OSVersion}|{Environment.ProcessorCount}|");
        identity.Append(HashFile(request.Library, token)).Append('|').Append(HashFile(request.Sample, token));
        // Native dependencies and their contents are part of the compiled backend identity.
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(request.Library)!, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
            identity.Append('|').Append(Path.GetFileName(path)).Append(':').Append(HashFile(path, token));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Device qualification requires Windows hardware identity.");
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")
            ?? throw new IOException("Cannot read CPU hardware identity.");
        identity.Append('|').Append(cpu.GetValue("ProcessorNameString")).Append('|').Append(cpu.GetValue("Identifier"));
        using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}")
            ?? throw new IOException("Cannot read GPU driver identity.");
        foreach (var name in display.GetSubKeyNames().Order(StringComparer.Ordinal))
        {
            if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;
            using var adapter = display.OpenSubKey(name);
            if (adapter?.GetValue("DriverDesc") is not string description) continue;
            var driverVersion = adapter.GetValue("DriverVersion") as string;
            if (string.IsNullOrWhiteSpace(driverVersion))
                throw new IOException($"Cannot read GPU driver version for {description}.");
            identity.Append('|').Append(name).Append(':').Append(description).Append(':')
                .Append(driverVersion).Append(':').Append(adapter.GetValue("MatchingDeviceId"));
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }

    private static string HashFile(string path, CancellationToken token)
    {
        NativeModelIntegrity.RejectLinks(path);
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static List<DeviceBenchmarkSelection> ReadCache(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        NativeModelIntegrity.RejectLinks(path);
        using var file = File.OpenRead(path);
        if (file.Length > 1024 * 1024) return [];
        try
        {
            var entries = JsonSerializer.Deserialize(file, DeviceBenchmarkJson.Default.ListDeviceBenchmarkSelection);
            if (entries is null || entries.Count > 128 || entries.Any(entry => entry is null ||
                string.IsNullOrWhiteSpace(entry.Key) || entry.Cpu is null || entry.Gpu is null ||
                Select(entry.Key, entry.Cpu, entry.Gpu).Backend != entry.Backend)) return [];
            return entries;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException)
        {
            return [];
        }
    }

    internal static void WriteCache(string? path, List<DeviceBenchmarkSelection> entries)
    {
        if (path is null) return;
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        NativeModelIntegrity.RejectLinks(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) NativeModelIntegrity.RejectLinks(path);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, entries.TakeLast(128).ToList(), DeviceBenchmarkJson.Default.ListDeviceBenchmarkSelection);
                file.Flush(true);
            }
            File.Move(staging, path, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    internal static async Task<DeviceBenchmarkResult> RunAsync(string executable, DeviceBenchmarkRequest request,
        CancellationToken token, TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        start.ArgumentList.Add("--device-benchmark");
        start.ArgumentList.Add(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request, DeviceBenchmarkJson.Default.DeviceBenchmarkRequest)));
        return await RunProcessAsync(start, request.Backend, token, timeout).ConfigureAwait(false);
    }

    internal static async Task<DeviceBenchmarkResult> RunProcessAsync(ProcessStartInfo start, NativeBackend backend,
        CancellationToken token, TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Could not start the isolated device benchmark.");
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? DeviceDeadline);
        var timedOut = false;
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            timedOut = !token.IsCancellationRequested;
            // Do not relinquish ownership before both the process and redirected streams have exited.
            Exception? terminationFailure = null;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) { terminationFailure = error; }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (terminationFailure is not null)
                throw new IOException("The device probe could not be terminated; ownership was retained until it exited.", terminationFailure);
        }
        finally
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        if (timedOut) return new(backend, null, "isolated probe timed out and was terminated");
        var output = await stdout;
        var line = output.Split('\n').LastOrDefault(value => value.StartsWith(ResultPrefix, StringComparison.Ordinal));
        if (line is null)
        {
            if (process.ExitCode == 0) throw new InvalidDataException("Device probe exited successfully without a result.");
            return new(backend, null, $"isolated probe exited {process.ExitCode} without a result (crash)");
        }
        DeviceBenchmarkResult result;
        try
        {
            result = JsonSerializer.Deserialize(line[ResultPrefix.Length..], DeviceBenchmarkJson.Default.DeviceBenchmarkResult)
                ?? throw new InvalidDataException("Device probe returned a null result.");
        }
        catch (JsonException error) { throw new InvalidDataException("Device probe returned invalid JSON.", error); }
        if (result.Backend != backend || result.Working != (process.ExitCode == 0) ||
            result.Error is null && !result.Working || result.Error?.Length > 4096)
            throw new InvalidDataException("Device probe returned an invalid measurement.");
        return result;
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > OutputLimit) tail.Remove(0, tail.Length - OutputLimit);
        }
        return tail.ToString();
    }
}
