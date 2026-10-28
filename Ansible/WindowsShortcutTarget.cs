using Ansible.Core;

namespace Ansible;

/// <summary>Adapts <see cref="WindowsTextTarget"/> to the Core shortcut dictation destination contract.</summary>
internal sealed class WindowsShortcutTargetInspector(nint appWindow) : IShortcutTargetInspector
{
    public Task<IShortcutTextTarget> InspectAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var locateCaret = settings.OverlayFollowsCaret;
        var method = settings.InsertionMethod;
        var typingGap = settings.TypingGapMilliseconds;
        var pasteShortcut = settings.PasteShortcut;
        // Inspection always runs to completion. Shutdown joins it instead of cancelling it.
        return Task.Run<IShortcutTextTarget>(() =>
        {
            if (!WindowsTextTarget.TryCapture(appWindow, locateCaret, out var target, out var reason))
                throw new InvalidOperationException(reason);
            return new WindowsShortcutTarget(target!, method, typingGap, pasteShortcut);
        });
    }
}

internal sealed class WindowsShortcutTarget(
    CapturedTextTarget target, TextInsertionMethod method, int typingGapMilliseconds, PasteShortcut pasteShortcut)
    : IShortcutTextTarget
{
    public CaretBounds? Caret => target.Caret;

    public Task<TextDeliveryOutcome> InsertAsync(string text, Func<bool> isCurrent, CancellationToken cancellationToken) =>
        Task.Run(() => WindowsTextTarget.Insert(target, text, method, typingGapMilliseconds, pasteShortcut,
            cancellationToken, isCurrent), cancellationToken);
}
