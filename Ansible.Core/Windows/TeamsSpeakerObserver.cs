using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ansible.Core.Windows;

public sealed partial class TeamsSpeakerObserver : IMeetingSpeakerObserver
{
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task _worker = Task.CompletedTask;
    private TeamsSpeakerReadDiagnostics _diagnostics = new(0, 0, false);
    internal TeamsSpeakerReadDiagnostics LastRead => Volatile.Read(ref _diagnostics);
    private bool _disposed;
    private static readonly Guid AutomationClass = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid AutomationId = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");

    public Task StartAsync(int processId, Func<long> samplePosition, Action<MeetingSpeakerObservation> report,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(samplePosition);
        ArgumentNullException.ThrowIfNull(report);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Teams speaker observation requires Windows.");
        using var process = Process.GetProcessById(processId);
        if (!IsTeamsProcess(process.ProcessName)) throw new ArgumentException("Select the Microsoft Teams application.", nameof(processId));
        var created = process.StartTime.ToUniversalTime();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_worker.IsCompleted) throw new InvalidOperationException("Teams observation is already active.");
            _stop?.Dispose();
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cancellation = _stop.Token;
            _worker = Task.Factory.StartNew(() => Run(processId, created, samplePosition, report,
                diagnostics => Volatile.Write(ref _diagnostics, diagnostics), cancellation),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        return Task.CompletedTask;
    }

    internal static bool IsTeamsProcess(string name) => name.Equals("ms-teams", StringComparison.OrdinalIgnoreCase)
        || name.Equals("msteams", StringComparison.OrdinalIgnoreCase) || name.Equals("Teams", StringComparison.OrdinalIgnoreCase);

    private static unsafe void Run(int processId, DateTime created, Func<long> samplePosition,
        Action<MeetingSpeakerObservation> report, Action<TeamsSpeakerReadDiagnostics> diagnostics, CancellationToken token)
    {
        nint automation = 0, walker = 0, tileCondition = 0, cache = 0;
        bool com = false;
        bool failureReported = false;
        var lastRead = new TeamsSpeakerReadDiagnostics(0, 0, false);
        void RecordDiagnostics(TeamsSpeakerReadDiagnostics value)
        {
            if (!failureReported && (value.BudgetExceeded || value.ErrorHResult is not null))
            {
                failureReported = true;
                Trace.TraceWarning("Ansible: Teams speaker observation unavailable. Windows: {0}; nodes: {1}; budget exceeded: {2}; HRESULT: {3:X8}.",
                    value.WindowCount, value.NodeCount, value.BudgetExceeded, value.ErrorHResult);
            }
            lastRead = value;
            diagnostics(value);
        }
        try
        {
            Check(CoInitializeEx(0, 0));
            com = true;
            Check(CoCreateInstance(in AutomationClass, 0, 1, in AutomationId, out automation));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(automation, 61))(automation, 200));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(automation, 63))(automation, 200));
            nint result = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, 16))(automation, &result));
            walker = result;
            tileCondition = CreateTileCondition(automation);
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, 20))(automation, &result));
            cache = result;
            foreach (var property in new[] { 30003, 30005, 30011, 30013, 30022, 30026 })
                Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(cache, 3))(cache, property));
            Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(cache, 7))(cache, 1));
            while (!token.IsCancellationRequested)
            {
                var position = Math.Max(0, samplePosition());
                MeetingSpeakerObservation observation;
                try
                {
                    using var process = Process.GetProcessById(processId);
                    if (process.StartTime.ToUniversalTime() != created || !IsTeamsProcess(process.ProcessName)) break;
                    observation = Read(automation, walker, tileCondition, cache, processId, position, RecordDiagnostics, token);
                }
                catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
                {
                    RecordDiagnostics(lastRead with { ErrorHResult = error.HResult });
                    observation = new(position, [], null, MeetingSpeakerReadStatus.Unavailable);
                }
                report(observation);
                token.WaitHandle.WaitOne(500);
            }
        }
        catch (COMException error)
        {
            RecordDiagnostics(lastRead with { ErrorHResult = error.HResult });
            if (!token.IsCancellationRequested) report(new(Math.Max(0, samplePosition()), [], null, MeetingSpeakerReadStatus.Unavailable));
        }
        finally
        {
            Release(cache); Release(tileCondition); Release(walker); Release(automation);
            if (com) CoUninitialize();
        }
    }

    private static unsafe MeetingSpeakerObservation Read(nint automation, nint walker, nint tileCondition, nint cache, int processId,
        long position, Action<TeamsSpeakerReadDiagnostics> diagnostics, CancellationToken token)
    {
        var windows = EnumerateWindows(processId);
        var nodes = new List<TeamsSpeakerNode>();
        var timer = Stopwatch.StartNew();
        diagnostics(new(windows.Count, 0, false));
        foreach (var window in windows)
        {
            if (timer.ElapsedMilliseconds > 750 || token.IsCancellationRequested)
            {
                diagnostics(new(windows.Count, nodes.Count, true));
                return new(position, [], null, MeetingSpeakerReadStatus.Unavailable);
            }
            nint root = 0, tiles = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(automation, 10))(automation, window, cache, &root));
            try
            {
                // Keep chat history outside the participant traversal budget and stay inside this window.
                Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint, nint*, int>)Slot(root, 8))(
                    root, 4, tileCondition, cache, &tiles));
                int count = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(tiles, 3))(tiles, &count));
                if (count > 256)
                {
                    diagnostics(new(windows.Count, nodes.Count, true));
                    return new(position, [], null, MeetingSpeakerReadStatus.Unavailable);
                }
                for (var candidate = 0; candidate < count; candidate++)
                {
                    nint tile = 0;
                    Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(tiles, 4))(tiles, candidate, &tile));
                    try
                    {
                        if (timer.ElapsedMilliseconds > 750 || token.IsCancellationRequested ||
                            TeamsSpeakerClassifier.IsTile(ReadNode(tile, -1)) &&
                            !Walk(walker, cache, tile, -1, 0, nodes, timer, token))
                        {
                            diagnostics(new(windows.Count, nodes.Count, true));
                            return new(position, [], null, MeetingSpeakerReadStatus.Unavailable);
                        }
                    }
                    finally { Release(tile); }
                }
            }
            finally { Release(tiles); Release(root); }
        }
        if (timer.ElapsedMilliseconds > 750 || token.IsCancellationRequested)
        {
            diagnostics(new(windows.Count, nodes.Count, true));
            return new(position, [], null, MeetingSpeakerReadStatus.Unavailable);
        }
        diagnostics(new(windows.Count, nodes.Count, false));
        return TeamsSpeakerClassifier.Classify(nodes, position);
    }

    private static unsafe bool Walk(nint walker, nint cache, nint element, int parent, int depth, List<TeamsSpeakerNode> nodes,
        Stopwatch timer, CancellationToken token)
    {
        if (depth > 32 || nodes.Count >= 256 || timer.ElapsedMilliseconds > 750 || token.IsCancellationRequested) return false;
        int offscreen = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 70))(element, &offscreen));
        if (offscreen != 0) return true;
        var index = nodes.Count;
        nodes.Add(ReadNode(element, parent));
        nint child = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(walker, 10))(walker, element, cache, &child));
        while (child != 0)
        {
            nint next = 0;
            try
            {
                if (!Walk(walker, cache, child, index, depth + 1, nodes, timer, token)) return false;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(walker, 12))(walker, child, cache, &next));
            }
            finally { Release(child); }
            child = next;
        }
        return true;
    }

    private static unsafe nint CreateTileCondition(nint automation)
    {
        nint ids = 0, menuItem = 0, tiles = 0, visible = 0, visibleTiles = 0;
        try
        {
            foreach (var marker in TeamsSpeakerClassifier.TileAutomationIds)
                AddStringCondition(ref ids, 30011, marker);
            var value = new AutomationVariant { Type = 3, Integer = 50011 };
            Check(((delegate* unmanaged[Stdcall]<nint, int, AutomationVariant, nint*, int>)Slot(automation, 23))(
                automation, 30003, value, &menuItem));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(automation, 28))(
                automation, menuItem, ids, &tiles));
            value = new AutomationVariant { Type = 11, Boolean = 0 };
            Check(((delegate* unmanaged[Stdcall]<nint, int, AutomationVariant, nint*, int>)Slot(automation, 23))(
                automation, 30022, value, &visible));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(automation, 25))(
                automation, tiles, visible, &visibleTiles));
            var result = visibleTiles;
            visibleTiles = 0;
            return result;
        }
        finally { Release(visibleTiles); Release(visible); Release(tiles); Release(menuItem); Release(ids); }

        void AddStringCondition(ref nint combined, int property, string text)
        {
            var value = new AutomationVariant { Type = 8, Text = Marshal.StringToBSTR(text) };
            nint condition = 0, next = 0;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, int, AutomationVariant, int, nint*, int>)Slot(automation, 24))(
                    automation, property, value, 3, &condition));
                if (combined == 0) { combined = condition; condition = 0; }
                else
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(automation, 28))(
                        automation, combined, condition, &next));
                    Release(combined);
                    combined = next;
                }
            }
            finally { Release(condition); Marshal.FreeBSTR(value.Text); }
        }
    }

    private static unsafe TeamsSpeakerNode ReadNode(nint element, int parent)
    {
        int controlType = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 53))(element, &controlType));
        return new(ReadString(element, 55), ReadString(element, 61), ReadString(element, 63),
            ReadString(element, 74), parent, controlType);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct AutomationVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal short Boolean;
        [FieldOffset(8)] internal int Integer;
        [FieldOffset(8)] internal nint Text;
    }

    private static unsafe string ReadString(nint element, int slot)
    {
        nint text = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(element, slot))(element, &text));
        try
        {
            if (text == 0) return "";
            var length = SysStringLen(text);
            if (length > 4096) return "";
            return Marshal.PtrToStringUni(text, (int)length) ?? "";
        }
        finally { SysFreeString(text); }
    }

    private static unsafe List<nint> EnumerateWindows(int processId)
    {
        var state = new WindowSearch((uint)processId);
        var handle = GCHandle.Alloc(state);
        try { EnumWindows((nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&CollectWindow, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return state.Windows;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int CollectWindow(nint window, nint context)
    {
        var state = (WindowSearch)GCHandle.FromIntPtr(context).Target!;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == state.ProcessId && IsWindowVisible(window) != 0 && IsIconic(window) == 0)
        {
            if (state.Windows.Count == 4) { state.Windows.Clear(); return 0; }
            state.Windows.Add(window);
        }
        return 1;
    }
    private sealed class WindowSearch(uint processId) { internal uint ProcessId = processId; internal List<nint> Windows = []; }
    private static unsafe nint Slot(nint instance, int index) => ((nint*)(*(nint*)instance))[index];
    private static unsafe void Release(nint instance) { if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance); }
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    public async Task StopAsync()
    {
        Task worker;
        lock (_gate) { _stop?.Cancel(); worker = _worker; }
        await worker.ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await StopAsync().ConfigureAwait(false);
        lock (_gate) { _stop?.Dispose(); _stop = null; }
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [LibraryImport("oleaut32.dll")] private static partial uint SysStringLen(nint text);
    [LibraryImport("oleaut32.dll")] private static partial void SysFreeString(nint text);
    [LibraryImport("user32.dll")] private static partial int EnumWindows(nint callback, nint context);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    [LibraryImport("user32.dll")] private static partial int IsWindowVisible(nint window);
    [LibraryImport("user32.dll")] private static partial int IsIconic(nint window);
}

internal sealed record TeamsSpeakerNode(string Name, string AutomationId = "", string HelpText = "",
    string ItemStatus = "", int Parent = -1, int ControlType = 50011);

internal sealed record TeamsSpeakerReadDiagnostics(int WindowCount, int NodeCount, bool BudgetExceeded, int? ErrorHResult = null);

internal static class TeamsSpeakerClassifier
{
    private const string SelfTilePrefix = "Myself video,";
    private static readonly string[] TileDescriptions = [
        "Video on", "Video off", "video is on", "video is off",
        "Has control of production tools", "muted", "Unmuted"];
    internal static readonly string[] TileAutomationIds = ["video-tile", "participant-tile"];

    internal static MeetingSpeakerObservation Classify(IReadOnlyList<TeamsSpeakerNode> nodes, long position)
    {
        var speaking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var self = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool readable = false;
        var silent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var tile = IsTile(node) ? node : FindTile(nodes, node.Parent);
            if (tile is null) continue;
            var name = ParseName(tile.Name);
            if (name is null) continue;
            if (tile.Name.StartsWith(SelfTilePrefix, StringComparison.OrdinalIgnoreCase) ||
                tile.Name.Contains("(You)", StringComparison.OrdinalIgnoreCase)) self.Add(name);
            if (ExplicitlySpeaking(node)) { speaking.Add(name); readable = true; }
            if (ExplicitlyNotSpeaking(node)) { readable = true; silent.Add(name); }
        }
        var status = !readable ? MeetingSpeakerReadStatus.Unavailable
            : speaking.Count > 1 || self.Count > 1 || speaking.Overlaps(silent)
                ? MeetingSpeakerReadStatus.Ambiguous : MeetingSpeakerReadStatus.Readable;
        return new(Math.Max(0, position), speaking.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            self.Count == 1 ? self.Single() : null, status);
    }
    private static TeamsSpeakerNode? FindTile(IReadOnlyList<TeamsSpeakerNode> nodes, int parent)
    {
        for (var depth = 0; parent >= 0 && parent < nodes.Count && depth < 8; depth++)
        {
            if (IsTile(nodes[parent])) return nodes[parent];
            parent = nodes[parent].Parent;
        }
        return null;
    }
    internal static bool IsTile(TeamsSpeakerNode node) =>
        TileAutomationIds.Any(marker => node.AutomationId.Contains(marker, StringComparison.OrdinalIgnoreCase))
        || node.ControlType == 50011 && (TileDescriptionStart(node.Name) >= 0
            || node.Name.StartsWith(SelfTilePrefix, StringComparison.OrdinalIgnoreCase));
    private static bool ExplicitlySpeaking(TeamsSpeakerNode node) => HasMarker(node, "Active speaker")
        || HasMarker(node, "Speaking") || HasMarker(node, "You are speaking");
    private static bool ExplicitlyNotSpeaking(TeamsSpeakerNode node) => HasMarker(node, "Not speaking");
    private static bool HasMarker(TeamsSpeakerNode node, string marker)
    {
        var name = node.Name;
        if (IsTile(node))
        {
            var description = TileDescriptionStart(name);
            var firstComma = name.IndexOf(',');
            name = description >= 0 ? name[(description + 1)..]
                : firstComma >= 0 ? name[(firstComma + 1)..] : "";
        }
        return new[] { name, node.HelpText, node.ItemStatus }
            .Any(text => text.Split(',', StringSplitOptions.TrimEntries)
                .Any(part => part.Equals(marker, StringComparison.OrdinalIgnoreCase)));
    }
    private static int TileDescriptionStart(string text)
    {
        var start = text.StartsWith(SelfTilePrefix, StringComparison.OrdinalIgnoreCase) ? SelfTilePrefix.Length : 0;
        for (var comma = text.IndexOf(',', start); comma >= 0; comma = text.IndexOf(',', comma + 1))
        {
            var end = text.IndexOf(',', comma + 1);
            var part = text.AsSpan(comma + 1, (end < 0 ? text.Length : end) - comma - 1).Trim();
            foreach (var marker in TileDescriptions)
                if (part.Equals(marker, StringComparison.OrdinalIgnoreCase)) return comma;
        }
        return -1;
    }
    private static string? ParseName(string text)
    {
        var descriptionStart = TileDescriptionStart(text);
        var start = text.StartsWith(SelfTilePrefix, StringComparison.OrdinalIgnoreCase) ? SelfTilePrefix.Length : 0;
        var name = (descriptionStart >= 0 ? text[start..descriptionStart] : text[start..].Split(',', 2)[0]).Trim();
        while (true)
        {
            var badge = new[] { "(You)", "(Guest)", "(External)" }
                .FirstOrDefault(value => name.EndsWith(value, StringComparison.OrdinalIgnoreCase));
            if (badge is null) break;
            name = name[..^badge.Length].TrimEnd();
        }
        return name.Length is > 0 and <= 200 && !name.Equals("You", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("Active speaker", StringComparison.OrdinalIgnoreCase) ? name : null;
    }
}
