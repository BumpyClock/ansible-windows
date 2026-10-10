using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ansible.Core.Windows;

public sealed partial class TeamsMeetingDetector : IMeetingDetector
{
    internal const int MaximumEndpoints = 32;
    internal const int MaximumSessions = 256;
    internal const int MaximumProcesses = 8192;
    private readonly SemaphoreSlim _reading = new(1, 1);
    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EnumeratorId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid ManagerId = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid SessionId = new("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d");

    public async Task<MeetingDetectionCandidate?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Meeting detection requires Windows.");
        await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Factory.StartNew(() => Read(cancellationToken), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        finally { _reading.Release(); }
    }

    private static unsafe MeetingDetectionCandidate? Read(CancellationToken token)
    {
        nint enumerator = 0, devices = 0;
        bool com = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var observedUtc = DateTime.UtcNow;
            Check(CoInitializeEx(0, 0)); com = true;
            Check(CoCreateInstance(in EnumeratorClass, 0, 1, in EnumeratorId, out enumerator));
            Check(((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)Slot(enumerator, 3))(
                enumerator, 1, 1, &devices));
            uint count = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(devices, 3))(devices, &count));
            if (count > MaximumEndpoints) throw new MeetingDetectionUnavailableException("Meeting detection exceeded its capture-device enumeration limit.");
            var started = Stopwatch.StartNew();
            var processIds = new HashSet<int>();
            var sessionCount = 0;
            for (uint index = 0; index < count; index++)
            {
                CheckBudget(started, token);
                nint device = 0, manager = 0, sessions = 0;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(devices, 4))(devices, index, &device));
                    var iid = ManagerId;
                    Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(device, 3))(
                        device, &iid, 23, 0, &manager));
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(manager, 5))(manager, &sessions));
                    int size = 0;
                    Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(sessions, 3))(sessions, &size));
                    if (size < 0 || size > MaximumSessions - sessionCount)
                        throw new MeetingDetectionUnavailableException("Meeting detection exceeded its audio-session enumeration limit.");
                    sessionCount += size;
                    for (int sessionIndex = 0; sessionIndex < size; sessionIndex++)
                    {
                        CheckBudget(started, token);
                        nint control = 0, extended = 0;
                        try
                        {
                            Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(sessions, 4))(
                                sessions, sessionIndex, &control));
                            int state = 0;
                            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(control, 3))(control, &state));
                            if (state is < 0 or > 2) throw new MeetingDetectionUnavailableException("Windows returned an invalid audio-session state.");
                            if (state != 1) continue;
                            iid = SessionId;
                            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(control, 0))(
                                control, &iid, &extended));
                            uint processId = 0;
                            var status = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(extended, 14))(
                                extended, &processId);
                            Check(status);
                            // AUDCLNT_S_NO_SINGLE_PROCESS cannot establish one process as the microphone owner.
                            if (status != 0 || processId is 0 or > int.MaxValue) continue;
                            processIds.Add((int)processId);
                        }
                        finally { Release(extended); Release(control); }
                    }
                }
                finally { Release(sessions); Release(manager); Release(device); }
            }
            var candidates = new List<MeetingDetectionCandidate>();
            var parents = processIds.Count == 0 ? new Dictionary<int, ProcessSnapshotNode>() : ReadProcessSnapshot(started, token);
            var identities = new Dictionary<int, TeamsProcessNode?>();
            TeamsProcessNode? Identity(int processId)
            {
                if (identities.TryGetValue(processId, out var cached)) return cached;
                if (!parents.TryGetValue(processId, out var snapshot)) return null;
                CheckBudget(started, token);
                try
                {
                    using var process = Process.GetProcessById(processId);
                    var name = process.ProcessName;
                    if (!name.Equals(Path.GetFileNameWithoutExtension(snapshot.Executable), StringComparison.OrdinalIgnoreCase))
                        return identities[processId] = null;
                    return identities[processId] = new(processId, snapshot.ParentProcessId, name, process.StartTime.ToUniversalTime());
                }
                catch (ArgumentException) { return identities[processId] = null; }
                catch (InvalidOperationException) { return identities[processId] = null; }
            }
            foreach (var processId in processIds)
            {
                CheckBudget(started, token);
                if (ResolveCaptureOwner(processId, Identity, observedUtc) is { } candidate) candidates.Add(candidate);
            }
            return SelectCandidate(candidates);
        }
        catch (Exception error) when (error is COMException or Win32Exception)
        {
            throw new MeetingDetectionUnavailableException("Windows microphone-session detection is unavailable.", error);
        }
        finally
        {
            Release(devices); Release(enumerator);
            if (com) CoUninitialize();
        }
    }

    internal static MeetingDetectionCandidate? SelectCandidate(IEnumerable<MeetingDetectionCandidate> candidates)
        => candidates.OrderBy(candidate => candidate.ProcessStartedUtc).ThenBy(candidate => candidate.ProcessId).FirstOrDefault();
    internal static MeetingDetectionCandidate? ResolveCaptureOwner(int processId, Func<int, TeamsProcessNode?> lookup,
        DateTime observedUtc)
    {
        var visited = new HashSet<int>();
        var childStarted = observedUtc;
        for (var depth = 0; processId > 0 && depth < 16; depth++)
        {
            if (!visited.Add(processId) || lookup(processId) is not { } node) return null;
            if (node.ProcessId != processId || node.StartedUtc.Kind != DateTimeKind.Utc || node.StartedUtc > childStarted)
                return null;
            if (TeamsSpeakerObserver.IsTeamsProcess(node.ProcessName)) return new(node.ProcessId, node.StartedUtc);
            childStarted = node.StartedUtc;
            processId = node.ParentProcessId;
        }
        return null;
    }
    private static unsafe Dictionary<int, ProcessSnapshotNode> ReadProcessSnapshot(Stopwatch timer, CancellationToken token)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var processes = new Dictionary<int, ProcessSnapshotNode>();
            var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
            if (Process32FirstW(snapshot, ref entry) == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 18) return processes;
                throw new Win32Exception(error);
            }
            do
            {
                CheckBudget(timer, token);
                if (processes.Count >= MaximumProcesses)
                    throw new MeetingDetectionUnavailableException("Meeting detection exceeded its process-identity enumeration limit.");
                if (entry.ProcessId is > 0 and <= int.MaxValue && entry.ParentProcessId <= int.MaxValue)
                    processes[(int)entry.ProcessId] = new((int)entry.ParentProcessId, new string(entry.Executable));
                entry.Size = (uint)sizeof(ProcessEntry);
            } while (Process32NextW(snapshot, ref entry) != 0);
            var lastError = Marshal.GetLastPInvokeError();
            if (lastError != 18) throw new Win32Exception(lastError);
            return processes;
        }
        finally { CloseHandle(snapshot); }
    }
    private sealed record ProcessSnapshotNode(int ParentProcessId, string Executable);
    [StructLayout(LayoutKind.Sequential)] private unsafe struct ProcessEntry
    {
        internal uint Size, Usage, ProcessId;
        internal nuint DefaultHeapId;
        internal uint ModuleId, Threads, ParentProcessId;
        internal int PriorityClass;
        internal uint Flags;
        internal fixed char Executable[260];
    }
    private static void CheckBudget(Stopwatch timer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (timer.Elapsed > TimeSpan.FromSeconds(2))
            throw new MeetingDetectionUnavailableException("Windows microphone-session detection exceeded its enumeration time budget.");
    }
    private static unsafe nint Slot(nint instance, int index) => ((nint*)(*(nint*)instance))[index];
    private static unsafe void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }
    private static void Check(int status) => Marshal.ThrowExceptionForHR(status);
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int Process32NextW(nint snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll")] private static partial int CloseHandle(nint handle);
}

internal sealed record TeamsProcessNode(int ProcessId, int ParentProcessId, string ProcessName, DateTime StartedUtc);
