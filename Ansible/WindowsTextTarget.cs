using System.Runtime.InteropServices;
using Ansible.Core;

namespace Ansible;

internal sealed record CapturedTextTarget(nint Window, nint FocusWindow, int[]? RuntimeId)
{
    public bool Matches(nint window, nint focusWindow, int[]? runtimeId) =>
        Window == window && FocusWindow == focusWindow &&
        (RuntimeId is null || (runtimeId is not null && RuntimeId.AsSpan().SequenceEqual(runtimeId)));
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
    private static readonly Guid AutomationClass = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid AutomationInterface = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");
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
        CapturedTextTarget target, string text, TextInsertionMethod method, int typingGapMilliseconds,
        PasteShortcut pasteShortcut, CancellationToken cancellationToken, Func<bool> stillCurrent)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TextDeliveryOutcome.Rejected("The recognition result contains no speech text. Use Copy transcript if needed.");
        // Never mask physical modifiers with synthetic releases. Deferral is safe only before delivery.
        for (var attempt = 0; attempt < 8 && !ShortcutModifiersReleased(); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stillCurrent())
                return TextDeliveryOutcome.Rejected("Another operation started before the text could be sent. Use Copy transcript instead.");
            Thread.Sleep(25);
        }
        if (!InsertionKeysReleased(method, ShortcutModifiersReleased(), PasteTriggerReleased(pasteShortcut)))
            return TextDeliveryOutcome.Deferred("The shortcut keys are still held; typing resumes when they are released.");
        TextSnapshot? before = null;
        TextDeliveryOutcome? Recheck()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stillCurrent())
                return TextDeliveryOutcome.Rejected("Another operation started before the text could be sent. Use Copy transcript instead.");
            if (!TryRecheckTarget(target, out var reason))
                return TextDeliveryOutcome.Rejected($"{reason} Use Copy transcript instead.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!stillCurrent())
                return TextDeliveryOutcome.Rejected("Another operation started before the text could be sent. Use Copy transcript instead.");
            if (!InsertionKeysReleased(method, ShortcutModifiersReleased(), PasteTriggerReleased(pasteShortcut)))
                return TextDeliveryOutcome.Deferred("The shortcut keys are still held; typing resumes when they are released.");
            return null;
        }

        TextDeliveryOutcome? PrepareSend()
        {
            before = TryReadTextSnapshot(target, cancellationToken, stillCurrent);
            return Recheck();
        }

        if (Recheck() is { } early) { return early; }
        var failure = method == TextInsertionMethod.Paste
            ? Paste(text, pasteShortcut, cancellationToken, stillCurrent, Recheck, PrepareSend)
            : Type(text, typingGapMilliseconds, Recheck, PrepareSend);
        if (failure is { } outcome) { return outcome; }
        return VerifyDelivery(target, text, before, cancellationToken, stillCurrent);
    }

    // A gap of zero sends the whole text in one SendInput call. Slow targets such as Windows 11 Notepad resolve
    // each injected Unicode keystroke when they process it, so a burst can repeat the latest character instead.
    // A positive gap sends one character (or surrogate pair) at a time and re-validates the target before each.
    private static TextDeliveryOutcome? Type(string text, int gapMilliseconds,
        Func<TextDeliveryOutcome?> recheck, Func<TextDeliveryOutcome?> prepareSend)
    {
        if (gapMilliseconds <= 0)
        {
            if (prepareSend() is { } veto) { return veto; }
            return SendUnicode(text, 0, text.Length);
        }
        var index = 0;
        while (index < text.Length)
        {
            var count = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
            TextDeliveryOutcome? veto;
            try { veto = index == 0 ? prepareSend() : recheck(); }
            catch (OperationCanceledException) when (index > 0)
            {
                return TextDeliveryOutcome.Partial("Typing was cancelled after some text was sent. Check the field; no text will be replayed.");
            }
            catch (COMException) when (index > 0)
            {
                return TextDeliveryOutcome.Partial("Typing stopped because the original field could no longer be checked. Some text was sent; check the field. No text will be replayed.");
            }
            if (veto is { } outcome)
            {
                return index == 0 ? outcome : TextDeliveryOutcome.Partial(
                    $"Typing stopped after {index} of {text.Length} characters. {outcome.Message}");
            }
            if (SendUnicode(text, index, count) is { } failure)
                return index > 0 && failure.Status == TextDeliveryStatus.Rejected
                    ? TextDeliveryOutcome.Partial($"Typing stopped after {index} characters. {failure.Message}") : failure;
            index += count;
            if (index < text.Length) { Thread.Sleep(gapMilliseconds); }
        }
        return null;
    }

    private static TextDeliveryOutcome? SendUnicode(string text, int start, int count)
    {
        var input = new KeyboardEvent[checked(count * 2)];
        for (var index = 0; index < count; index++)
        {
            input[2 * index] = KeyboardEvent.Unicode(text[start + index], KeyUnicode);
            input[2 * index + 1] = KeyboardEvent.Unicode(text[start + index], KeyUnicode | KeyUp);
        }
        return SendUnicodeEvents(input, NativeSend);
    }

    internal static TextDeliveryOutcome? SendUnicodeEvents(KeyboardEvent[] input, Func<KeyboardEvent[], uint> send)
    {
        var sent = Math.Min(send(input), (uint)input.Length);
        if (sent == input.Length) { return null; }
        if (sent == 0)
            return TextDeliveryOutcome.Rejected("Windows did not queue any typing events. Use Copy transcript instead.");
        if ((sent & 1) != 0 && !ReleaseOwnedKeys([input[sent]], send))
            return TextDeliveryOutcome.CleanupFailed("Typing stopped with an unreleased injected Unicode key. Some text may have been inserted; check the field. No text will be replayed.");
        return TextDeliveryOutcome.Partial($"Windows queued {sent} of {input.Length} typing events. Some text may have been inserted; check the field. No text will be replayed.");
    }

    private static bool ShortcutModifiersReleased()
    {
        return (GetAsyncKeyState(0x10) & 0x8000) == 0 &&
            (GetAsyncKeyState(0x11) & 0x8000) == 0 &&
            (GetAsyncKeyState(0x12) & 0x8000) == 0 &&
            (GetAsyncKeyState(0x5B) & 0x8000) == 0 &&
            (GetAsyncKeyState(0x5C) & 0x8000) == 0;
    }

    private static bool PasteTriggerReleased(PasteShortcut shortcut) =>
        (GetAsyncKeyState(shortcut == PasteShortcut.ShiftInsert ? 0x2D : VkV) & 0x8000) == 0;

    internal static bool InsertionKeysReleased(TextInsertionMethod method, bool modifiersReleased, bool triggerReleased) =>
        modifiersReleased && (method != TextInsertionMethod.Paste || triggerReleased);

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
        try
        {
            using var read = new AutomationRead();
            var field = read.ReadField();
            id = field.RuntimeId;
            reason = FieldSafetyReason(field);
            return reason.Length == 0;
        }
        catch (COMException error) when (IsAccessibilityUnavailable(error.HResult))
        {
            reason = "The target's accessibility provider could not identify an individual field.";
            return true;
        }
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
    internal struct KeybdInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct KeyboardEvent
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeybdInput Key;

        public static KeyboardEvent Unicode(char character, uint flags) =>
            new() { Type = InputKeyboard, Key = new KeybdInput { ScanCode = character, Flags = flags } };

        public static KeyboardEvent Virtual(ushort key, uint flags) =>
            new() { Type = InputKeyboard, Key = new KeybdInput { VirtualKey = key, Flags = flags } };
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
