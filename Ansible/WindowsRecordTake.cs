using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ansible.Core;

namespace Ansible;

/// <summary>Snapshots the pointer monitor; the admitted operation owns all running native work.</summary>
internal sealed partial class WindowsRecordTakeFactory(CaptureStore store, LocalDiagnosticLog? diagnostics) : IRecordTakeFactory
{
    internal Action? BeforeCapture { get; set; }
    internal RecordCaptureDiagnostic? LastDiagnostics { get; private set; }
    internal long? DiagnosticGeneration => diagnostics?.CurrentGeneration;

    internal void ReportDiagnostics(RecordCaptureApi.Status status, long? generation)
    {
        LastDiagnostics = new(status.MouseEvents, status.MouseMoves, status.MaxHookDelayMs,
            status.HookDelaysOver100Ms, status.MaxPresentUs, status.MaxLoopUs,
            status.MaxFenceWaitMs, status.HooksReleasedBeforeJoin != 0);
        if (generation is { } consent)
            diagnostics?.Write(new ApplicationDiagnostic(DateTimeOffset.UtcNow, ApplicationDiagnosticStage.RecordCapture,
                status.Discarded != 0 ? DiagnosticOutcome.Cancelled :
                status.CaptureError < 0 || status.InkError < 0 || status.VideoError < 0 || status.FrameError < 0
                    ? DiagnosticOutcome.Failed : DiagnosticOutcome.Completed)
                { RecordCapture = LastDiagnostics }, consent);
    }
    private CaptureLease? _currentResult;
    private long _currentResultOrigin;
    internal long TakeOriginTimestamp { get; private set; }
    private static readonly SemaphoreSlim ClipboardGate = new(1, 1);
    private static CaptureLease? s_clipboardLease;
    private static uint s_clipboardSequence;
    private static CaptureStore? s_store;

    public RecordTakeOptions Snapshot(bool includeVideoLink)
    {
        var origin = Stopwatch.GetTimestamp();
        TakeOriginTimestamp = origin;
        var monitor = GetCursorPos(out var point) ? MonitorFromPoint(point, 2) : 0;
        try { BeforeCapture?.Invoke(); }
        catch { monitor = 0; } // Media preparation failure must still allow authoritative speech.
        return new(monitor, origin, includeVideoLink);
    }

    public async Task<IRecordTakeCapture> StartAsync(RecordTakeOptions options, Action<RecordInkBurst> onBurst,
        Action onDiscard, CancellationToken cancellationToken)
    {
        if (options.Monitor == 0)
            throw new IOException("The screen monitor or capture privacy could not be prepared. This take contains text only.");
        s_store = store;
        await RefreshClipboardProtectionAsync().ConfigureAwait(false);
        if (_currentResult is { } old)
        {
            _currentResult = null;
            await old.DisposeAsync().ConfigureAwait(false);
        }
        var lease = await store.CreateTakeAsync(cancellationToken).ConfigureAwait(false);
        try { return new WindowsRecordTake(this, lease, options, onBurst, onDiscard, cancellationToken); }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task RetainResultAsync(CaptureLease lease, long origin)
    {
        if (_currentResult is { } old) await old.DisposeAsync().ConfigureAwait(false);
        _currentResult = lease;
        _currentResultOrigin = origin;
    }

    internal async Task DiscardResultAsync(long origin)
    {
        if (_currentResult is not { } lease || _currentResultOrigin != origin) return;
        _currentResult = null;
        await lease.DisposeAsync().ConfigureAwait(false);
        await store.DeleteTakeAsync(lease.Directory).ConfigureAwait(false);
    }

    internal Task<bool> DeleteDiscardedAsync(string directory) => store.DeleteTakeAsync(directory);

    internal Task<CaptureCleanupResult> CleanupAsync() => store.CleanupAsync();

    // This observes clipboard replacement only for file lifetime, never as evidence of insertion.
    internal static async Task RefreshClipboardProtectionAsync()
    {
        await ClipboardGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (s_clipboardLease is not null && GetClipboardSequenceNumber() != s_clipboardSequence)
            {
                await s_clipboardLease.DisposeAsync().ConfigureAwait(false);
                s_clipboardLease = null;
            }
        }
        finally { ClipboardGate.Release(); }
    }

    internal static async Task<CaptureLease?> ProtectPictureFilesAsync(IReadOnlyList<RecordFrame> frames)
    {
        await RefreshClipboardProtectionAsync().ConfigureAwait(false);
        return s_store is { } storage && frames.Count > 0
            ? await storage.ProtectAsync(Path.GetDirectoryName(frames[0].Path)!).ConfigureAwait(false) : null;
    }

    internal static async Task RememberPictureClipboardAsync(CaptureLease? lease, uint sequence)
    {
        if (lease is null) return;
        await ClipboardGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (s_clipboardLease is { } old) await old.DisposeAsync().ConfigureAwait(false);
            s_clipboardLease = lease;
            s_clipboardSequence = sequence;
        }
        finally { ClipboardGate.Release(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);
    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();
}

internal sealed class WindowsRecordTake : IRecordTakeCapture
{
    private readonly object _gate = new();
    private readonly WindowsRecordTakeFactory _owner;
    private readonly CaptureLease _lease;
    private readonly RecordTakeOptions _options;
    private readonly Task<int> _running;
    private readonly Task _observing;
    private readonly CancellationTokenRegistration _cancellation;
    private readonly long? _diagnosticGeneration;
    private nint _handle;
    private Task<RecordMediaResult>? _stopping;
    private bool _discard;
    private bool _leaseTransferred;
    public bool IsReleased { get { lock (_gate) return _handle == 0; } }

    internal WindowsRecordTake(WindowsRecordTakeFactory owner, CaptureLease lease, RecordTakeOptions options,
        Action<RecordInkBurst> onBurst, Action onDiscard, CancellationToken cancellationToken)
    {
        _owner = owner;
        _diagnosticGeneration = owner.DiagnosticGeneration;
        _lease = lease;
        _options = options;
        Marshal.ThrowExceptionForHR(RecordCaptureApi.Create(options.Monitor, lease.Directory, options.OriginTimestamp, out _handle));
        var nativeHandle = _handle;
        _running = Task.Run(() => RecordCaptureApi.Run(nativeHandle));
        _cancellation = cancellationToken.UnsafeRegister(_ => RecordCaptureApi.Stop(nativeHandle, 1), null);
        _observing = Task.Run(async () =>
        {
            uint seen = 0;
            var discarded = false;
            try
            {
                while (true)
                {
                    var finished = _running.IsCompleted;
                    var status = ReadStatus();
                    while (seen < status.BurstCount)
                    {
                        var burst = ReadBurst(seen++);
                        onBurst(new((int)burst.Number, burst.Timestamp));
                    }
                    if (status.Discarded != 0 && !discarded)
                    {
                        discarded = true;
                        onDiscard();
                    }
                    if (finished) break;
                    await Task.Delay(40).ConfigureAwait(false);
                }
            }
            catch
            {
                RecordCaptureApi.Stop(nativeHandle, 0);
                throw;
            }
        });
    }

    public Task<RecordMediaResult> StopAsync(bool discard)
    {
        lock (_gate)
        {
            _discard |= discard;
            if (_handle != 0) Marshal.ThrowExceptionForHR(RecordCaptureApi.Stop(_handle, _discard ? 1 : 0));
            return _stopping ??= StopCoreAsync();
        }
    }

    private async Task<RecordMediaResult> StopCoreAsync()
    {
        var warnings = new List<string>();
        // Join both operations even if one faults. The native handle stays owned until DisposeAsync.
        try { await Task.WhenAll(_running, _observing).ConfigureAwait(false); }
        catch (Exception error) { warnings.Add($"Screen capture stopped with an error. {error.Message}"); }
        if (_running.IsCompletedSuccessfully && _running.Result < 0)
            warnings.Add($"Screen capture failed (0x{_running.Result:X8}).");
        var status = ReadStatus();
        _owner.ReportDiagnostics(status, _diagnosticGeneration);
        _discard |= status.Discarded != 0;
        var discarded = _discard || status.Discarded != 0;
        foreach (var (error, part) in new[] { (status.CaptureError, "Screen capture"), (status.InkError, "Ink"),
                     (status.VideoError, "Video encoding"), (status.FrameError, "Capture pictures") })
            if (error < 0) warnings.Add($"{part} failed (0x{error:X8}).");
        var frames = new List<RecordFrame>();
        if (!discarded)
        {
            for (uint index = 0; index < status.BurstCount; index++)
            {
                var burst = ReadBurst(index);
                var path = Path.Combine(_lease.Directory, $"burst-{burst.Number}.png");
                if (burst.FrameSaved != 0 && File.Exists(path))
                    frames.Add(new(path, (int)burst.Number, Stopwatch.GetElapsedTime(_options.OriginTimestamp, burst.Timestamp)));
            }
            var summary = Path.Combine(_lease.Directory, "summary.png");
            if (status.SummarySaved != 0 && File.Exists(summary))
                frames.Add(new(summary, null, Stopwatch.GetElapsedTime(_options.OriginTimestamp, status.StopQpc), status.BurstCount > 0));
        }
        var video = Path.Combine(_lease.Directory, "take.mp4");
        string? videoPath = !discarded && status.VideoError >= 0 && status.CaptureError >= 0 &&
            status.VideoFinalized != 0 && status.EncodedSamples > 0 && File.Exists(video) ? video : null;
        if (!discarded && (frames.Count > 0 || videoPath is not null))
        {
            await _owner.RetainResultAsync(_lease, _options.OriginTimestamp).ConfigureAwait(false);
            _leaseTransferred = true;
        }
        else await _lease.DisposeAsync().ConfigureAwait(false);
        try { await WindowsRecordTakeFactory.RefreshClipboardProtectionAsync().ConfigureAwait(false); await _owner.CleanupAsync().ConfigureAwait(false); }
        catch (Exception error) { warnings.Add($"Capture cleanup failed. {error.Message}"); }
        return new(frames, videoPath, warnings, discarded,
            status.FirstVideoQpc > 0 ? Stopwatch.GetElapsedTime(_options.OriginTimestamp, status.FirstVideoQpc) : null);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(_stopping is null).ConfigureAwait(false); }
        finally
        {
            // Result inspection can fail after workers exit. It must not strand a native handle.
            await Task.WhenAll(_running.ContinueWith(_ => { }, TaskScheduler.Default),
                _observing.ContinueWith(_ => { }, TaskScheduler.Default)).ConfigureAwait(false);
            _cancellation.Dispose();
            lock (_gate)
            {
                if (_handle != 0)
                {
                    Marshal.ThrowExceptionForHR(RecordCaptureApi.Destroy(_handle));
                    _handle = 0;
                }
            }
            if (!_leaseTransferred) await _lease.DisposeAsync().ConfigureAwait(false);
            if (_discard)
            {
                if (_leaseTransferred) await _owner.DiscardResultAsync(_options.OriginTimestamp).ConfigureAwait(false);
                else await _owner.DeleteDiscardedAsync(_lease.Directory).ConfigureAwait(false);
            }
        }
    }

    private RecordCaptureApi.Status ReadStatus()
    {
        var status = new RecordCaptureApi.Status { Size = 112 };
        Marshal.ThrowExceptionForHR(RecordCaptureApi.GetStatus(_handle, ref status));
        if (status.BurstCount > 256) throw new InvalidDataException("Capture returned too many ink bursts.");
        return status;
    }

    private RecordCaptureApi.Burst ReadBurst(uint index)
    {
        var burst = new RecordCaptureApi.Burst { Size = 24 };
        Marshal.ThrowExceptionForHR(RecordCaptureApi.GetBurst(_handle, index, ref burst));
        return burst;
    }
}

internal static partial class RecordCaptureApi
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status
    {
        public uint Size, Running, Discarded, BurstCount, PictureCount, Width, Height, SummarySaved;
        public int CaptureError, InkError, VideoError, FrameError;
        public long FirstVideoQpc, StopQpc;
        public uint SourceFrames, EncodedSamples, HeldSamples, VideoFinalized;
        public uint MouseEvents, MouseMoves, MaxHookDelayMs, HookDelaysOver100Ms;
        public uint MaxPresentUs, MaxLoopUs, MaxFenceWaitMs, HooksReleasedBeforeJoin;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Burst
    {
        public uint Size, Number, FrameSaved, Reserved;
        public long Timestamp;
    }
    [LibraryImport("record_capture.dll", EntryPoint = "record_take_create", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Create(nint monitor, string directory, long origin, out nint take);
    [LibraryImport("record_capture.dll", EntryPoint = "record_take_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Run(nint take);
    [LibraryImport("record_capture.dll", EntryPoint = "record_take_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Stop(nint take, int discard);
    [LibraryImport("record_capture.dll", EntryPoint = "record_take_get_status")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetStatus(nint take, ref Status status);
    [LibraryImport("record_capture.dll", EntryPoint = "record_take_get_burst")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetBurst(nint take, uint index, ref Burst burst);
    [LibraryImport("record_capture.dll", EntryPoint = "record_take_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Destroy(nint take);
}
