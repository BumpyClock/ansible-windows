using System.Diagnostics;
using System.Runtime.InteropServices;
using Ansible.Core;

namespace Ansible;

internal static unsafe partial class WindowsTextTarget
{
    internal const int VerificationTextBudget = 8192;
    internal readonly record struct FocusState(nint Window, nint FocusWindow);
    internal sealed record FocusedField(int[]? RuntimeId, bool Password, bool ReadOnly, bool Enabled, bool Available = true);
    internal sealed record TextSnapshot(string Document, int SelectionStart, int SelectionEnd,
        bool Complete = true, int SelectionCount = 1);

    internal static string FieldSafetyReason(FocusedField field) =>
        field.Password ? "Automatic insertion is disabled for password fields." :
        field.ReadOnly ? "The selected field is read-only." :
        !field.Enabled ? "The selected field is disabled." : "";

    internal static string RecheckTarget(CapturedTextTarget target,
        Func<FocusState?> focus, Func<FocusedField> field)
    {
        var first = focus();
        if (first is null || first.Value.Window != target.Window || first.Value.FocusWindow != target.FocusWindow)
            return "The original text field no longer has focus.";
        FocusedField fresh;
        try { fresh = field(); }
        catch (COMException error) when (IsAccessibilityUnavailable(error.HResult))
        {
            fresh = new(null, false, false, true, false);
        }
        var reason = FieldSafetyReason(fresh);
        if (reason.Length != 0) { return reason; }
        if (!fresh.Available && target.RuntimeId is not null)
            return "The original field's accessibility provider is no longer available.";
        var last = focus();
        if (first != last || !target.Matches(last!.Value.Window, last.Value.FocusWindow, fresh.RuntimeId))
            return "Focus changed or the original field identity is no longer available.";
        return "";
    }

    private static FocusState? ReadFocus() =>
        TryFocus(0, out var window, out var focus, out _) ? new FocusState(window, focus) : null;

    private static bool TryRecheckTarget(CapturedTextTarget target, out string reason)
    {
        try
        {
            reason = RecheckTarget(target, ReadFocus, () =>
            {
                using var read = new AutomationRead();
                return read.ReadField();
            });
        }
        catch (COMException)
        {
            reason = "Windows could not safely verify the original destination field.";
        }
        return reason.Length == 0;
    }

    internal static bool ExactInsertion(TextSnapshot before, TextSnapshot after, string text)
    {
        static bool Valid(TextSnapshot snapshot) =>
            snapshot.Complete && snapshot.SelectionCount == 1 &&
            snapshot.Document.Length <= VerificationTextBudget &&
            snapshot.SelectionStart >= 0 && snapshot.SelectionEnd >= snapshot.SelectionStart &&
            snapshot.SelectionEnd <= snapshot.Document.Length;
        if (!Valid(before) || !Valid(after) || before.Document == after.Document ||
            after.SelectionStart != after.SelectionEnd ||
            after.SelectionStart != before.SelectionStart + text.Length)
            return false;
        var length = before.Document.Length - (before.SelectionEnd - before.SelectionStart) + text.Length;
        if (length > VerificationTextBudget || after.Document.Length != length) { return false; }
        return after.Document.AsSpan(0, before.SelectionStart).SequenceEqual(before.Document.AsSpan(0, before.SelectionStart)) &&
            after.Document.AsSpan(before.SelectionStart, text.Length).SequenceEqual(text) &&
            after.Document.AsSpan(before.SelectionStart + text.Length).SequenceEqual(before.Document.AsSpan(before.SelectionEnd));
    }

    private static TextSnapshot? TryReadTextSnapshot(CapturedTextTarget target,
        CancellationToken cancellationToken, Func<bool> stillCurrent, Stopwatch? deadline = null)
    {
        if (target.RuntimeId is null || cancellationToken.IsCancellationRequested || !stillCurrent()) { return null; }
        try
        {
            var first = ReadFocus();
            if (first is null || first.Value.Window != target.Window || first.Value.FocusWindow != target.FocusWindow) { return null; }
            using var read = new AutomationRead(cancellationToken, stillCurrent, deadline);
            var field = read.ReadField();
            if (FieldSafetyReason(field).Length != 0 || field.RuntimeId is null ||
                !target.Matches(first.Value.Window, first.Value.FocusWindow, field.RuntimeId)) { return null; }
            var snapshot = read.ReadText();
            // Re-read the focused element, not a cached element's identity, after fetching document content.
            var fresh = read.ReadField();
            var last = ReadFocus();
            return first == last && FieldSafetyReason(fresh).Length == 0 &&
                target.Matches(last!.Value.Window, last.Value.FocusWindow, fresh.RuntimeId) &&
                !cancellationToken.IsCancellationRequested && stillCurrent() ? snapshot : null;
        }
        catch (COMException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    private static TextDeliveryOutcome VerifyDelivery(CapturedTextTarget target, string text,
        TextSnapshot? before, CancellationToken cancellationToken, Func<bool> stillCurrent)
    {
        var clock = Stopwatch.StartNew();
        var outcome = ObserveDelivery(before, text,
            () => TryReadTextSnapshot(target, cancellationToken, stillCurrent, clock),
            () => !cancellationToken.IsCancellationRequested && stillCurrent(),
            Thread.Sleep, () => clock.ElapsedMilliseconds);
        return outcome with { VerificationMilliseconds = clock.Elapsed.TotalMilliseconds };
    }

    internal static TextDeliveryOutcome ObserveDelivery(TextSnapshot? before, string text,
        Func<TextSnapshot?> observe, Func<bool> active, Action<int> wait, Func<long> elapsedMilliseconds)
    {
        if (before is not null)
        {
            while (elapsedMilliseconds() < 600 && active())
            {
                var after = observe();
                if (after is null) { break; }
                if (ExactInsertion(before, after, text) && elapsedMilliseconds() < 600 && active())
                    return TextDeliveryOutcome.Verified("Exact text insertion was observed in the original field.", TextDeliveryReason.Verified);
                wait(25);
            }
        }
        return TextDeliveryOutcome.Sent("Input was queued, but insertion could not be verified within the bounded accessibility observation. Check the field; no text will be replayed.", TextDeliveryReason.SentUnverified);
    }

    private sealed class AutomationRead : IDisposable
    {
        private readonly Stopwatch _clock;
        private readonly int _budget;
        private readonly CancellationToken _cancellationToken;
        private readonly Func<bool> _stillCurrent;
        private nint _automation, _element;
        private bool _initialized;
        private static readonly Guid TextPatternInterface = new("32eba289-3583-42c9-9c59-3b6d9a1e9b6a");
        private static readonly Guid TextPattern2Interface = new("506a921a-fcc9-409f-b23b-37eb74106872");
        private const int TextPattern2 = 10024;
        // IUIAutomationTextRange slots from UIAutomationClient.h: Clone 3, ExpandToEnclosingUnit 6,
        // GetBoundingRectangles 10, MoveEndpointByUnit 14. TextUnit_Character is 0.
        private const int ExpandToEnclosingUnitSlot = 6, BoundingRectanglesSlot = 10, MoveEndpointByUnitSlot = 14;
        private const int CaretRangeSlot = 10;
        // The caret is a placement hint. It gets at most this share of the shared clock so a slow
        // provider cannot delay opening the microphone by the full field-verification budget.
        private const int CaretBudget = 250;
        private int? _caretDeadline;

        public AutomationRead(CancellationToken cancellationToken = default,
            Func<bool>? stillCurrent = null, Stopwatch? deadline = null)
        {
            _clock = deadline ?? Stopwatch.StartNew();
            _budget = deadline is null ? 750 : 600;
            _cancellationToken = cancellationToken;
            _stillCurrent = stillCurrent ?? (() => true);
            try
            {
                BeforeCall();
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
                _initialized = true;
                var clsid = AutomationClass;
                var iid = AutomationInterface;
                BeforeCall();
                Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, ClsctxInprocServer, ref iid, out _automation));
                // IUIAutomation2, SDK slots 61/63: provider connection and transaction waits in milliseconds.
                SetTimeout(61, 150);
                SetTimeout(63, 150);
            }
            catch { Dispose(); throw; }
        }

        private void BeforeCall()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_stillCurrent()) { throw new OperationCanceledException("The delivery operation was superseded."); }
            if (_clock.ElapsedMilliseconds >= (_caretDeadline ?? _budget)) { throw new COMException("Accessibility observation deadline elapsed.", UiaTimedOut); }
        }

        private void SetTimeout(int slot, uint milliseconds)
        {
            BeforeCall();
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, int>)Method(_automation, slot))(_automation, milliseconds));
        }

        private int Integer(nint instance, int slot)
        {
            BeforeCall();
            int result = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, int>)Method(instance, slot))(instance, &result));
            return result;
        }

        private nint Pointer(nint instance, int slot)
        {
            BeforeCall();
            nint result = 0;
            var status = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(instance, slot))(instance, &result);
            if (status < 0)
            {
                if (result != 0) { Release(result); }
                Marshal.ThrowExceptionForHR(status);
            }
            return result;
        }

        private nint Pattern(int id, Guid iid)
        {
            BeforeCall();
            nint result = 0;
            var status = ((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Method(_element, CurrentPatternAsSlot))(_element, id, &iid, &result);
            if (status < 0)
            {
                if (result != 0) { Release(result); }
                if (status == UiaNotSupported) { return 0; }
                Marshal.ThrowExceptionForHR(status);
            }
            return result;
        }

        public FocusedField ReadField()
        {
            if (_element != 0) { Release(_element); _element = 0; }
            _element = Pointer(_automation, FocusedElementSlot);
            if (_element == 0) { throw new COMException("No focused automation element.", UiaElementNotAvailable); }
            var password = Integer(_element, PasswordSlot) != 0;
            if (password) { return new(null, true, false, true); }
            var enabled = Integer(_element, 28) != 0;
            if (!enabled) { return new(null, false, false, false); }
            if (ReadOnly()) { return new(null, false, true, true); }
            BeforeCall();
            nint array = 0;
            var status = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(_element, RuntimeIdSlot))(_element, &array);
            try
            {
                if (status == UiaNotSupported) { return StableEditableField(null); }
                Marshal.ThrowExceptionForHR(status);
                if (array == 0 || SafeArrayGetDim(array) != 1) { return StableEditableField(null); }
                Marshal.ThrowExceptionForHR(SafeArrayGetLBound(array, 1, out var lower));
                Marshal.ThrowExceptionForHR(SafeArrayGetUBound(array, 1, out var upper));
                var length = (long)upper - lower + 1;
                if (length <= 0 || length > 128) { return StableEditableField(null); }
                Marshal.ThrowExceptionForHR(SafeArrayAccessData(array, out var data));
                int[] id;
                try
                {
                    id = new int[(int)length];
                    Marshal.Copy(data, id, 0, id.Length);
                }
                finally { Marshal.ThrowExceptionForHR(SafeArrayUnaccessData(array)); }
                return StableEditableField(id);
            }
            finally { if (array != 0) { Marshal.ThrowExceptionForHR(SafeArrayDestroy(array)); } }
        }

        private FocusedField StableEditableField(int[]? id)
        {
            if (Integer(_element, PasswordSlot) != 0) { return new(null, true, false, true); }
            if (Integer(_element, 28) == 0) { return new(null, false, false, false); }
            if (ReadOnly()) { return new(null, false, true, true); }
            var focused = Pointer(_automation, FocusedElementSlot);
            if (focused == 0) { throw new COMException("No focused automation element.", UiaElementNotAvailable); }
            try
            {
                BeforeCall();
                int same = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, nint, int*, int>)Method(_automation, 3))(
                    _automation, _element, focused, &same));
                if (same == 0)
                    throw new COMException("The focused field changed during accessibility validation.", unchecked((int)0x80004005));
                BeforeCall();
                return new(id, false, false, true);
            }
            finally { Release(focused); }
        }

        private bool ReadOnly()
        {
            var value = Pattern(ValuePattern, ValuePatternInterface);
            if (value != 0)
            {
                try { if (Integer(value, ValueIsReadOnlySlot) != 0) { return true; } }
                finally { Release(value); }
            }
            // Text-only editors can expose read-only state without ValuePattern.
            var text = Pattern(10014, TextPatternInterface);
            if (text != 0)
            {
                try
                {
                    var range = Pointer(text, 7);
                    if (range != 0)
                    {
                        try { if (TextReadOnly(range)) { return true; } }
                        finally { Release(range); }
                    }
                }
                finally { Release(text); }
            }
            return false;
        }

        private bool TextReadOnly(nint range)
        {
            BeforeCall();
            Variant value = default;
            try
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, Variant*, int>)Method(range, 9))(range, 40015, &value));
                return value.Type == 11 && value.Boolean != 0;
            }
            finally { Marshal.ThrowExceptionForHR(VariantClear(ref value)); }
        }

        public TextSnapshot? ReadText()
        {
            var pattern = Pattern(10014, TextPatternInterface);
            if (pattern == 0) { return null; }
            nint document = 0, selections = 0, selection = 0, prefix = 0, freshSelections = 0, freshSelection = 0;
            try
            {
                document = Pointer(pattern, 7);
                selections = Pointer(pattern, 5);
                if (document == 0 || selections == 0 || Integer(selections, 3) != 1) { return null; }
                selection = Selection(selections);
                if (selection == 0) { return null; }
                if (CompareEndpoint(selection, 0, document, 0) < 0 ||
                    CompareEndpoint(selection, 1, document, 1) > 0 ||
                    CompareEndpoint(selection, 0, selection, 1) > 0) { return null; }
                var content = Text(document);
                if (content is null) { return null; }
                prefix = Pointer(document, 3);
                if (prefix == 0) { return null; }
                SetEnd(prefix, selection, 0);
                var start = Text(prefix);
                SetEnd(prefix, selection, 1);
                var end = Text(prefix);
                if (start is null || end is null || !content.StartsWith(start, StringComparison.Ordinal) ||
                    !content.StartsWith(end, StringComparison.Ordinal) || start.Length > end.Length) { return null; }
                // Reject document or selection mutation instead of accepting a stitched snapshot.
                freshSelections = Pointer(pattern, 5);
                if (freshSelections == 0 || Integer(freshSelections, 3) != 1) { return null; }
                freshSelection = Selection(freshSelections);
                if (freshSelection == 0 ||
                    CompareEndpoint(selection, 0, freshSelection, 0) != 0 ||
                    CompareEndpoint(selection, 1, freshSelection, 1) != 0) { return null; }
                SetEnd(prefix, freshSelection, 0);
                if (Text(prefix) != start) { return null; }
                SetEnd(prefix, freshSelection, 1);
                if (Text(prefix) != end) { return null; }
                if (Text(document) != content) { return null; }
                return new(content, start.Length, end.Length);
            }
            finally
            {
                if (freshSelection != 0) { Release(freshSelection); }
                if (freshSelections != 0) { Release(freshSelections); }
                if (prefix != 0) { Release(prefix); }
                if (selection != 0) { Release(selection); }
                if (selections != 0) { Release(selections); }
                if (document != 0) { Release(document); }
                Release(pattern);
            }
        }

        /// <summary>
        /// Reads the caret rectangle in physical screen pixels from the focused element's text provider.
        /// Prefers the provider's caret range, then the first selection range. Collapsed ranges often
        /// report no rectangle, so the range is widened to the adjacent character on this private copy;
        /// the destination's own selection is never changed.
        /// </summary>
        public CaretBounds? ReadCaret()
        {
            var elapsed = _clock.ElapsedMilliseconds;
            if (elapsed >= CaretBudget) { return null; }
            _caretDeadline = (int)Math.Min(_budget, elapsed + CaretBudget);
            nint range = 0;
            try
            {
                var caretPattern = Pattern(TextPattern2, TextPattern2Interface);
                if (caretPattern != 0)
                {
                    try
                    {
                        BeforeCall();
                        var active = 0;
                        var status = ((delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)Method(caretPattern, CaretRangeSlot))(
                            caretPattern, &active, &range);
                        if (status < 0 || active == 0)
                        {
                            if (range != 0) { Release(range); range = 0; }
                        }
                    }
                    finally { Release(caretPattern); }
                }
                if (range == 0)
                {
                    var pattern = Pattern(10014, TextPatternInterface);
                    if (pattern == 0) { return null; }
                    try
                    {
                        var selections = Pointer(pattern, 5);
                        if (selections == 0) { return null; }
                        try
                        {
                            if (Integer(selections, 3) < 1) { return null; }
                            range = Selection(selections);
                        }
                        finally { Release(selections); }
                    }
                    finally { Release(pattern); }
                }
                if (range == 0) { return null; }
                if (BoundingRectangle(range) is { } exact) { return exact; }
                // Widen forward over the next character and anchor on its left edge.
                BeforeCall();
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int>)Method(range, ExpandToEnclosingUnitSlot))(range, 0));
                if (BoundingRectangle(range) is { } next) { return next with { Width = 0 }; }
                // At the end of the document, widen backward over the previous character and anchor on its right edge.
                BeforeCall();
                var moved = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int, int, int*, int>)Method(range, MoveEndpointByUnitSlot))(
                    range, 0, 0, -1, &moved));
                if (moved == 0) { return null; }
                return BoundingRectangle(range) is { } previous
                    ? previous with { Left = previous.Left + previous.Width, Width = 0 } : null;
            }
            finally
            {
                _caretDeadline = null;
                if (range != 0) { Release(range); }
            }
        }

        private CaretBounds? BoundingRectangle(nint range)
        {
            BeforeCall();
            nint array = 0;
            var status = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(range, BoundingRectanglesSlot))(range, &array);
            try
            {
                Marshal.ThrowExceptionForHR(status);
                if (array == 0 || SafeArrayGetDim(array) != 1) { return null; }
                Marshal.ThrowExceptionForHR(SafeArrayGetLBound(array, 1, out var lower));
                Marshal.ThrowExceptionForHR(SafeArrayGetUBound(array, 1, out var upper));
                var length = (long)upper - lower + 1;
                if (length < 4 || length > 4096) { return null; }
                Marshal.ThrowExceptionForHR(SafeArrayAccessData(array, out var data));
                var values = new double[4];
                try { Marshal.Copy(data, values, 0, values.Length); }
                finally { Marshal.ThrowExceptionForHR(SafeArrayUnaccessData(array)); }
                foreach (var value in values)
                {
                    if (!double.IsFinite(value) || Math.Abs(value) > 1 << 24) { return null; }
                }
                var bounds = new CaretBounds((int)Math.Round(values[0]), (int)Math.Round(values[1]),
                    (int)Math.Round(values[2]), (int)Math.Round(values[3]));
                return bounds.IsUsable ? bounds : null;
            }
            finally { if (array != 0) { Marshal.ThrowExceptionForHR(SafeArrayDestroy(array)); } }
        }

        private nint Selection(nint selections)
        {
            BeforeCall();
            nint result = 0;
            var status = ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Method(selections, 4))(selections, 0, &result);
            if (status < 0)
            {
                if (result != 0) { Release(result); }
                Marshal.ThrowExceptionForHR(status);
            }
            return result;
        }

        private void SetEnd(nint range, nint selection, int endpoint)
        {
            BeforeCall();
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, nint, int, int>)Method(range, 15))(range, 1, selection, endpoint));
        }

        private int CompareEndpoint(nint range, int endpoint, nint other, int otherEndpoint)
        {
            BeforeCall();
            int result = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, nint, int, int*, int>)Method(range, 5))(
                range, endpoint, other, otherEndpoint, &result));
            return result;
        }

        private string? Text(nint range)
        {
            if (Integer(_element, PasswordSlot) != 0) { return null; }
            BeforeCall();
            nint text = 0;
            var status = ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Method(range, 12))(range, VerificationTextBudget + 1, &text);
            try
            {
                Marshal.ThrowExceptionForHR(status);
                if (text == 0) { return ""; }
                var length = SysStringLen(text);
                return length > VerificationTextBudget ? null : new string((char*)text, 0, (int)length);
            }
            finally { if (text != 0) { SysFreeString(text); } }
        }

        public void Dispose()
        {
            // Timeouts limit UIA provider requests, not arbitrary in-process COM code or teardown.
            // Keep all calls joined on this worker; never abandon a running call or its COM ownership.
            if (_element != 0) { Release(_element); _element = 0; }
            if (_automation != 0) { Release(_automation); _automation = 0; }
            if (_initialized) { CoUninitialize(); _initialized = false; }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public short Boolean;
    }

    [LibraryImport("oleaut32.dll")]
    private static partial int VariantClear(ref Variant value);
    [LibraryImport("oleaut32.dll")]
    private static partial uint SysStringLen(nint value);
    [LibraryImport("oleaut32.dll")]
    private static partial void SysFreeString(nint value);
}
