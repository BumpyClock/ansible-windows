using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ansible.Core.Windows;

/// <summary>Reads the selected Teams call window without relying on a process title alone.</summary>
public sealed partial class TeamsMeetingContextReader : IMeetingContextReader
{
    private const int ButtonControlType = 50000;
    private const int NameProperty = 30005;
    private const int AutomationIdProperty = 30011;
    private const int ControlTypeProperty = 30003;
    private const int MaximumWindows = 32;
    private const int MaximumButtons = 256;
    private readonly SemaphoreSlim _reading = new(1, 1);
    private int _knownProcessId;
    private DateTime _knownProcessStartedUtc;
    private nint _knownCallWindow;
    private static readonly Guid AutomationClass = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid AutomationId = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");

    public async Task<MeetingContextObservation> ReadAsync(MeetingDetectionCandidate identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Teams context requires Windows.");
        await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Factory.StartNew(() => Read(identity, cancellationToken), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        finally { _reading.Release(); }
    }

    internal static string? CleanTitle(string title)
    {
        var value = title.Trim();
        foreach (var suffix in new[] { " | Microsoft Teams", " - Microsoft Teams", " | Teams", " - Teams" })
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                value = value[..^suffix.Length].Trim();
        if (value.Length == 0 || value.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Teams", StringComparison.OrdinalIgnoreCase) || value.Equals("Chat", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("New meeting", StringComparison.OrdinalIgnoreCase) || value.Equals("Meeting", StringComparison.OrdinalIgnoreCase))
            return null;
        return value.Length <= 500 ? value : value[..500].TrimEnd();
    }

    internal static bool IsInCallToolbarButton(string name, string automationId)
    {
        var id = automationId.Trim();
        if (new[] { "mute-button", "microphone-button", "toggle-mute", "camera-button", "toggle-camera", "video-button" }
            .Contains(id, StringComparer.OrdinalIgnoreCase)) return true;
        var label = name.Trim();
        return label.Equals("Mute mic", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Unmute mic", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Mute microphone", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Unmute microphone", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Turn camera on", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Turn camera off", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownHangupId(string id) => new[]
        { "hangup-button", "call-hangup", "leave-call", "end-call", "hang-up-button" }
        .Contains(id, StringComparer.OrdinalIgnoreCase);

    private static bool IsLeaveLabel(string label) =>
        label.Equals("Leave", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Leave meeting", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Hang up", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("End call", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCallWindow(IReadOnlyList<(string Name, string AutomationId)> buttons)
        => buttons.Any(button => IsKnownHangupId(button.AutomationId.Trim())) ||
            buttons.Any(button => IsLeaveLabel(button.Name.Trim())) &&
            buttons.Any(button => IsInCallToolbarButton(button.Name, button.AutomationId));

    private unsafe MeetingContextObservation Read(MeetingDetectionCandidate identity, CancellationToken token)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!TeamsSpeakerObserver.IsTeamsProcess(process.ProcessName) ||
                process.StartTime.ToUniversalTime() != identity.ProcessStartedUtc)
                return new(MeetingCallPresence.Absent);
        }
        catch (ArgumentException) { return new(MeetingCallPresence.Absent); }
        catch (InvalidOperationException) { return new(MeetingCallPresence.Unknown); }
        catch (Win32Exception) { return new(MeetingCallPresence.Unknown); }

        if (_knownProcessId != identity.ProcessId || _knownProcessStartedUtc != identity.ProcessStartedUtc)
        {
            _knownProcessId = identity.ProcessId;
            _knownProcessStartedUtc = identity.ProcessStartedUtc;
            _knownCallWindow = 0;
        }

        var timer = Stopwatch.StartNew();
        var windows = EnumerateWindows(identity.ProcessId);
        if (windows.Failed || windows.Truncated || timer.Elapsed > TimeSpan.FromSeconds(2)) return new(MeetingCallPresence.Unknown);
        if (windows.Handles.Count == 0)
        {
            if (_knownCallWindow != 0 && IsWindow(_knownCallWindow) == 0)
            {
                _knownCallWindow = 0;
                return new(MeetingCallPresence.Absent);
            }
            return _knownCallWindow == 0 ? new(MeetingCallPresence.Absent) : new(MeetingCallPresence.Unknown);
        }

        nint automation = 0, cache = 0, condition = 0;
        bool com = false;
        try
        {
            Check(CoInitializeEx(0, 0));
            com = true;
            Check(CoCreateInstance(in AutomationClass, 0, 1, in AutomationId, out automation));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(automation, 61))(automation, 200));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(automation, 63))(automation, 200));
            nint request = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, 20))(automation, &request));
            cache = request;
            foreach (var property in new[] { NameProperty, AutomationIdProperty, ControlTypeProperty })
                Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(cache, 3))(cache, property));
            // FindAllBuildCache returns each matching button. Cache only the returned element itself.
            Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(cache, 7))(cache, 1));
            nint raw = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, 17))(automation, &raw));
            try { Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(cache, 9))(cache, raw)); }
            finally { Release(raw); }

            var variant = new AutomationVariant { Type = 3, Integer = ButtonControlType };
            Check(((delegate* unmanaged[Stdcall]<nint, int, AutomationVariant, nint*, int>)Slot(automation, 23))(
                automation, ControlTypeProperty, variant, &condition));
            var confirmed = new List<(nint Window, string? Title)>();
            foreach (var window in windows.Handles)
            {
                token.ThrowIfCancellationRequested();
                if (timer.Elapsed > TimeSpan.FromSeconds(2)) return new(MeetingCallPresence.Unknown);
                nint root = 0, buttons = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(automation, 10))(
                    automation, window, cache, &root));
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint, nint*, int>)Slot(root, 8))(
                        root, 4, condition, cache, &buttons));
                    int count = 0;
                    Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(buttons, 3))(buttons, &count));
                    if (count > MaximumButtons) return new(MeetingCallPresence.Unknown);
                    var controls = new List<(string Name, string AutomationId)>();
                    for (var index = 0; index < count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (timer.Elapsed > TimeSpan.FromSeconds(2)) return new(MeetingCallPresence.Unknown);
                        nint button = 0;
                        Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(buttons, 4))(buttons, index, &button));
                        try
                        {
                            var name = ReadString(button, 55);
                            var id = ReadString(button, 61);
                            controls.Add((name, id));
                        }
                        finally { Release(button); }
                    }
                    if (IsCallWindow(controls))
                    {
                        var title = GetWindowTitle(window);
                        confirmed.Add((window, CleanTitle(title)));
                    }
                }
                finally { Release(buttons); Release(root); }
            }
            if (timer.Elapsed > TimeSpan.FromSeconds(2)) return new(MeetingCallPresence.Unknown);
            if (confirmed.Count == 0)
            {
                if (_knownCallWindow != 0 && IsWindow(_knownCallWindow) == 0)
                {
                    _knownCallWindow = 0;
                    return new(MeetingCallPresence.Absent);
                }
                return _knownCallWindow == 0 ? new(MeetingCallPresence.Absent) : new(MeetingCallPresence.Unknown);
            }
            _knownCallWindow = confirmed[0].Window;
            var distinct = confirmed.Select(item => item.Title).Where(title => title is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return distinct.Length == 1 ? new(MeetingCallPresence.Active, distinct[0]) : new(MeetingCallPresence.Active);
        }
        catch (OperationCanceledException) { throw; }
        catch (COMException) { return new(MeetingCallPresence.Unknown); }
        catch (Win32Exception) { return new(MeetingCallPresence.Unknown); }
        finally
        {
            Release(condition); Release(cache); Release(automation);
            if (com) CoUninitialize();
        }
    }

    private static unsafe WindowSearch EnumerateWindows(int processId)
    {
        var state = new WindowSearch((uint)processId);
        var handle = GCHandle.Alloc(state);
        try
        {
            var result = EnumWindows((nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&CollectWindow, GCHandle.ToIntPtr(handle));
            if (result == 0 && !state.Truncated) state.Failed = true;
        }
        finally { handle.Free(); }
        return state;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CollectWindow(nint window, nint context)
    {
        var state = (WindowSearch)GCHandle.FromIntPtr(context).Target!;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == state.ProcessId)
        {
            if (state.Handles.Count >= MaximumWindows) { state.Truncated = true; return 0; }
            state.Handles.Add(window);
        }
        return 1;
    }

    private sealed class WindowSearch(uint processId)
    {
        internal uint ProcessId = processId;
        internal List<nint> Handles { get; } = [];
        internal bool Truncated { get; set; }
        internal bool Failed { get; set; }
    }

    private static string GetWindowTitle(nint window)
    {
        var length = GetWindowTextLengthW(window);
        if (length <= 0 || length > 2048) return "";
        Span<char> buffer = stackalloc char[length + 1];
        var written = GetWindowTextW(window, buffer, buffer.Length);
        return written <= 0 ? "" : new string(buffer[..written]);
    }

    private static unsafe string ReadString(nint element, int slot)
    {
        nint text = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(element, slot))(element, &text));
        try
        {
            if (text == 0) return "";
            var length = SysStringLen(text);
            return length > 4096 ? "" : Marshal.PtrToStringUni(text, (int)length) ?? "";
        }
        finally { SysFreeString(text); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct AutomationVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal int Integer;
    }

    private static unsafe nint Slot(nint instance, int index) => ((nint*)(*(nint*)instance))[index];
    private static unsafe void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [LibraryImport("oleaut32.dll")] private static partial uint SysStringLen(nint text);
    [LibraryImport("oleaut32.dll")] private static partial void SysFreeString(nint value);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int EnumWindows(nint callback, nint context);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    [LibraryImport("user32.dll")] private static partial int IsWindow(nint window);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static partial int GetWindowTextLengthW(nint window);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(nint window, Span<char> text, int maxCount);
}
