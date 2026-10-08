using System.Runtime.InteropServices;
using Ansible.Core;

namespace Ansible;

internal sealed record CapturedTextTarget(nint Window, nint FocusWindow, int[]? RuntimeId)
{
    public bool Matches(nint window, nint focusWindow, int[]? runtimeId) =>
        Window == window && FocusWindow == focusWindow &&
        (RuntimeId is null || runtimeId is null || RuntimeId.AsSpan().SequenceEqual(runtimeId));
}

internal static unsafe partial class WindowsTextTarget
{
    private const uint InputKeyboard = 1;
    private const uint KeyUnicode = 4;
    private const uint KeyUp = 2;
    private const int ValuePattern = 10002;
    private const int UiaNotSupported = unchecked((int)0x80040204);
    private const int UiaElementNotAvailable = unchecked((int)0x80040201);
    private const int UiaTimedOut = unchecked((int)0x80131505);
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const uint ClsctxInprocServer = 1;
    // UIAutomationClient.h vtable slots include IUnknown's first three methods.
    private const int FocusedElementSlot = 8;
    private const int RuntimeIdSlot = 4;
    private const int CurrentPatternAsSlot = 14;
    private const int PasswordSlot = 35;
    private const int ValueIsReadOnlySlot = 5;
    private static readonly Guid AutomationClass = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid AutomationInterface = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
    private static readonly Guid ValuePatternInterface = new("a94cd8b1-0844-4cd6-9d2d-640537ab39e9");

    internal static bool TryCapture(nint appWindow, out CapturedTextTarget? target, out string reason)
    {
        target = null;
        if (!TryFocus(appWindow, out var window, out var focusWindow, out reason)) { return false; }
        if (!TryFocusedElement(out var runtimeId, out reason)) { return false; }
        if (!TryFocus(appWindow, out var currentWindow, out var currentFocus, out reason) ||
            currentWindow != window || currentFocus != focusWindow)
        {
            reason = "The focused window changed while selecting the destination.";
            return false;
        }
        target = new CapturedTextTarget(window, focusWindow, runtimeId);
        reason = "";
        return true;
    }

    internal static TextDeliveryOutcome Insert(
        CapturedTextTarget target, string text, CancellationToken cancellationToken, Func<bool> stillCurrent)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TextDeliveryOutcome.Rejected("The recognition result contains no speech text. Use Copy transcript if needed.");
        // A held modifier delays typing rather than disabling it: settle briefly, then defer (do not fail) so
        // the caller retries once the keys are released. Only focus/target loss or a partial send is a rejection.
        for (var attempt = 0; attempt < 8 && !ShortcutModifiersReleased(); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stillCurrent())
                return TextDeliveryOutcome.Rejected("Another operation started before the text could be sent. Use Copy transcript instead.");
            Thread.Sleep(25);
        }
        if (!ShortcutModifiersReleased())
            return TextDeliveryOutcome.Deferred("The shortcut keys are still held; typing resumes when they are released.");
        if (!TryFocus(0, out var window, out var focusWindow, out var reason) ||
            window != target.Window || focusWindow != target.FocusWindow)
            return TextDeliveryOutcome.Rejected("The original text field no longer has focus. Use Copy transcript instead.");
        if (!TryFocusedElement(out var runtimeId, out reason))
            return TextDeliveryOutcome.Rejected($"{reason} Use Copy transcript instead.");
        if (!target.Matches(window, focusWindow, runtimeId))
            return TextDeliveryOutcome.Rejected("The original text field no longer has focus. Use Copy transcript instead.");

        var input = new KeyboardEvent[checked(text.Length * 2)];
        for (var index = 0; index < text.Length; index++)
        {
            input[2 * index] = KeyboardEvent.Unicode(text[index], KeyUnicode);
            input[2 * index + 1] = KeyboardEvent.Unicode(text[index], KeyUnicode | KeyUp);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!stillCurrent())
            return TextDeliveryOutcome.Rejected("Another operation started before the text could be sent. Use Copy transcript instead.");
        // Re-validate the target as a rejection, but a modifier re-pressed at the last moment only defers.
        if (!TryFocus(0, out window, out focusWindow, out reason) || !target.Matches(window, focusWindow, runtimeId))
            return TextDeliveryOutcome.Rejected("Focus changed before insertion. Use Copy transcript instead.");
        if (!ShortcutModifiersReleased())
            return TextDeliveryOutcome.Deferred("The shortcut keys are still held; typing resumes when they are released.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!stillCurrent())
            return TextDeliveryOutcome.Rejected("Another operation started before the text could be sent. Use Copy transcript instead.");
        var sent = SendInput((uint)input.Length, input, Marshal.SizeOf<KeyboardEvent>());
        if (sent != input.Length)
        {
            var error = Marshal.GetLastPInvokeError();
            return TextDeliveryOutcome.Rejected($"Windows sent {sent} of {input.Length} keyboard events (error {error}). Some text may have been inserted. Check the field before using Copy transcript.");
        }
        return TextDeliveryOutcome.Sent(target.RuntimeId is null || runtimeId is null
            ? "Text was sent to the original window. Windows could not verify the individual field; check where the text appeared."
            : "Text was sent to the original field. Check the field if the target app does not accept simulated typing.");
    }

    private static bool ShortcutModifiersReleased()
    {
        return (GetAsyncKeyState(0x10) & 0x8000) == 0 &&
            (GetAsyncKeyState(0x11) & 0x8000) == 0 &&
            (GetAsyncKeyState(0x12) & 0x8000) == 0 &&
            (GetAsyncKeyState(0x5B) & 0x8000) == 0 &&
            (GetAsyncKeyState(0x5C) & 0x8000) == 0;
    }

    private static bool TryFocus(nint appWindow, out nint window, out nint focusWindow, out string reason)
    {
        window = GetForegroundWindow();
        focusWindow = 0;
        reason = "Select an editable field in another application before starting dictation.";
        if (window == 0 || window == appWindow) { return false; }
        if (GetWindowThreadProcessId(window, out var processId) == 0)
        {
            reason = "Windows could not identify the destination process.";
            return false;
        }
        if (processId == Environment.ProcessId) { return false; }
        if (!IsRegularProcess(processId, out reason)) { return false; }
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info) || info.Active != window || info.Focus == 0)
        {
            reason = "Windows could not identify the focused field.";
            return false;
        }
        focusWindow = info.Focus;
        return true;
    }

    private static bool TryFocusedElement(out int[]? id, out string reason)
    {
        id = null;
        reason = "The focused field cannot be verified for safe insertion.";
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
        try
        {
            try { return ReadFocusedElement(out id, out reason); }
            catch (COMException error) when (IsAccessibilityUnavailable(error.HResult))
            {
                id = null;
                reason = "The target's accessibility provider could not identify an individual field.";
                return true;
            }
        }
        finally { CoUninitialize(); }
    }

    private static bool ReadFocusedElement(out int[]? id, out string reason)
    {
        id = null;
        reason = "The focused field cannot be verified for safe insertion.";
        var clsid = AutomationClass;
        var iid = AutomationInterface;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, ClsctxInprocServer, ref iid, out var automation));
        try
        {
            var getFocused = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(automation, FocusedElementSlot);
            nint element = 0;
            Marshal.ThrowExceptionForHR(getFocused(automation, &element));
            if (element == 0) { return true; }
            try
            {
                int password = 0;
                var getPassword = (delegate* unmanaged[Stdcall]<nint, int*, int>)Method(element, PasswordSlot);
                Marshal.ThrowExceptionForHR(getPassword(element, &password));
                if (password != 0)
                {
                    reason = "Automatic insertion is disabled for password fields.";
                    return false;
                }
                nint valuePattern = 0;
                var patternId = ValuePatternInterface;
                var getPattern = (delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Method(element, CurrentPatternAsSlot);
                var patternStatus = getPattern(element, ValuePattern, &patternId, &valuePattern);
                if (patternStatus != UiaNotSupported) { Marshal.ThrowExceptionForHR(patternStatus); }
                if (valuePattern != 0)
                {
                    try
                    {
                        int readOnly = 0;
                        var getReadOnly = (delegate* unmanaged[Stdcall]<nint, int*, int>)Method(valuePattern, ValueIsReadOnlySlot);
                        Marshal.ThrowExceptionForHR(getReadOnly(valuePattern, &readOnly));
                        if (readOnly != 0)
                        {
                            reason = "The selected field is read-only.";
                            return false;
                        }
                    }
                    finally { Release(valuePattern); }
                }
                nint array = 0;
                var getId = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(element, RuntimeIdSlot);
                var idStatus = getId(element, &array);
                if (idStatus < 0)
                {
                    if (array != 0) { Marshal.ThrowExceptionForHR(SafeArrayDestroy(array)); }
                    if (idStatus != UiaNotSupported) { Marshal.ThrowExceptionForHR(idStatus); }
                    return true;
                }
                if (array == 0) { return true; }
                try
                {
                    if (SafeArrayGetDim(array) != 1) { return true; }
                    Marshal.ThrowExceptionForHR(SafeArrayGetLBound(array, 1, out var lower));
                    Marshal.ThrowExceptionForHR(SafeArrayGetUBound(array, 1, out var upper));
                    if (upper < lower || upper - lower >= 128) { return true; }
                    Marshal.ThrowExceptionForHR(SafeArrayAccessData(array, out var data));
                    try
                    {
                        id = new int[upper - lower + 1];
                        Marshal.Copy(data, id, 0, id.Length);
                    }
                    finally { Marshal.ThrowExceptionForHR(SafeArrayUnaccessData(array)); }
                    return true;
                }
                finally { Marshal.ThrowExceptionForHR(SafeArrayDestroy(array)); }
            }
            finally { Release(element); }
        }
        finally { Release(automation); }
    }

    internal static bool IsAccessibilityUnavailable(int hresult) =>
        hresult is UiaNotSupported or UiaElementNotAvailable or UiaTimedOut or RpcDisconnected;

    private static nint Method(nint instance, int index) => ((nint*)(*(nint*)instance))[index];

    private static void Release(nint instance)
    {
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)Method(instance, 2);
        release(instance);
    }

    private static bool IsRegularProcess(uint processId, out string reason)
    {
        reason = "Windows could not verify the destination's access level.";
        var process = OpenProcess(0x1000, false, processId);
        if (process == 0) { return false; }
        try
        {
            if (!OpenProcessToken(process, 0x0008, out var token)) { return false; }
            try
            {
                if (!GetTokenInformation(token, 20, out var elevation, sizeof(int), out var length) ||
                    length != sizeof(int)) { return false; }
                if (elevation != 0)
                {
                    reason = "Automatic insertion into elevated applications is unavailable.";
                    return false;
                }
                reason = "";
                return true;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardEvent
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeybdInput Key;

        public static KeyboardEvent Unicode(char character, uint flags) =>
            new() { Type = InputKeyboard, Key = new KeybdInput { ScanCode = character, Flags = flags } };
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] KeyboardEvent[] inputs, int size);
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint threadingModel);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
    [LibraryImport("oleaut32.dll")]
    private static partial uint SafeArrayGetDim(nint array);
    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayGetLBound(nint array, uint dimension, out int bound);
    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayGetUBound(nint array, uint dimension, out int bound);
    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayAccessData(nint array, out nint data);
    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayUnaccessData(nint array);
    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayDestroy(nint array);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int informationClass, out int information, int length, out int returnedLength);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
