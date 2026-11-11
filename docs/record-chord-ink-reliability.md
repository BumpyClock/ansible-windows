# Ink reliability review and corrections

Opus 5.5 reviewed `02d946d` after physical ink remained unreliable in installed
development build 1.0.1.19. The parent verified the source findings below before
changing code. A passing synthetic renderer fixture had not reproduced the
physical problem. This work does not establish a single cause for every report.

## Confirmed source defects corrected

1. The capture worker held the presentation request until PNG encoding ended.
   The input loop deferred drawing while that request existed. It now releases
   the hold as soon as a matching WGC frame is retained. PNG encoding remains
   synchronous on the capture worker, which owns those pixels throughout the
   save. Finishing that save cannot clear a following burst's request.
2. Burst and clean-summary fences were timestamped before publishing the
   overlay. Both now use the QPC timestamp immediately after
   `UpdateLayeredWindow`, before `DwmFlush`. A fence taken after the flush could
   reject the composition just awaited on a static screen.
3. Discard and failure paths could enter the media join with hooks installed.
   Every exit now releases hooks and destroys the owned overlay before joining
   media. GDI+ remains initialized until the capture worker exits. The worker
   checks discard/abort before capture work and avoids deliberately finalizing
   a discarded MP4. An already-running native operation still must be joined.
   An explicit finalization flag now prevents an unfinished MP4 from being
   offered as a video link after an aborted take.
4. An invalid hook handle remained owned forever. `ERROR_INVALID_HOOK_HANDLE`
   now counts as released. Other unhook errors remain visible and retained
   resources continue to block unsafe reuse. Windows silently removing a timed
   out hook remains a hypothesis, not an observed event in these checks.
5. A drag leaving and re-entering the recorded monitor joined disconnected
   segments. Re-entry now starts a separate stroke.

The changes are in `tools/record-capture/record_capture.cpp`. No replacement
renderer, hook thread split, polling-based ink, or model change was introduced.

## Foreground occlusion reproduced and corrected in 1.0.1.21

The user narrowed the symptom to ink being covered by whichever app had focus.
A new `--foreground` native fixture left the focused app visible. The earlier
fixture's synthetic backdrop had hidden this case. The unfixed run reproduced
missing live ink and missing burst ink, while the separately composited summary
contained both strokes. Runtime window metadata showed an ordinary ink HWND
behind the foreground app and a topmost recording indicator.

A further probe found `SetWindowPos(HWND_TOPMOST)` reported success while the
inactive ink HWND still lacked `WS_EX_TOPMOST`. The production change creates
both ink and indicator HWNDs with `WS_EX_TOPMOST` from the start. No focus
activation, repeated restacking timer, or renderer rewrite was added.

The corrected foreground fixture passes over Chrome and T3 Code. The final T3
run checks topmost state before/during a held drag and preserves foreground
identity. Desktop pixels contain ink before mouse-up; WGC burst pictures remain
isolated and the summary contains both marks. Runtime metadata also places the
topmost ink HWND above T3. The actual silent MP4 decodes successfully. Evidence
is ignored under `.runtime/record-capture/foreground-app-baseline-20261105`,
`foreground-create-topmost-20261105`, and `foreground-t3-fixed-20261105`, with
window-order traces in `.runtime/record-spikes`.

These tests use synthetic geometry through production input handling. They
prove foreground presentation and capture for those runs, not every physical
touchpad sequence or a performance improvement under inference load.

The final 1.0.1.21 published NativeAOT package passes an isolated capture under
identity `BumpyClock.Ansible.RecordSpike.ink21f1`, with dynamic code disabled,
released resources, and an independently decoded silent MP4. Concurrent
read-only window metadata confirms its topmost ink HWND is above the foreground
app. Core tests pass 1532 checks; 17 focused portable checks pass on Boat. The
synthetic desktop fixture supplies the visible stroke checks; the isolated
package entry point itself supplies no ink.

Signed development package 1.0.1.21 was installed and restarted. Its native
helper matches the tested helper. Settings, dictation history, and usage hashes
were preserved during installation. The desktop app exposes its enabled Start
dictation action after restart. Physical drawing with inference active remains
a hand check.

## Remaining hypotheses and limits

The hook/message thread still performs rendering and presentation. Long work
or scheduling delays can interfere with low-level hook delivery. Windows
documents a timeout and silent removal on supported systems in its
[mouse hook contract](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc).
No hook timeout or removal was measured here. The capture fixtures supplied
synthetic geometry directly and recorded zero real hook events. They cannot
qualify physical touchpad delivery or behavior with ASR active.

Drawing failures still stop the native input pump and abort remaining media.
The transcript and completed media remain available through the existing
failure path. Lock-screen, secure-desktop, RDP, and display-change triggers were
not reproduced. Competition from another topmost app activated later is also unmeasured. These remain
separate investigation paths if the physical failure persists.

## Diagnostics

The native status is now 112 bytes; the burst status remains 24 bytes. Managed
bindings, both hosts, and ABI checks use the same layouts. Each take reports
mouse event/move counts, maximum event age at the hook callback, callbacks over
100 ms, maximum presentation and message-loop work durations, maximum frame
fence wait, and whether hooks were released before joining media.

Event age uses the Windows event timestamp and `GetTickCount`, with unsigned
wraparound arithmetic. Presentation and loop durations use QPC. The loop work
counter excludes its intentional message wait. Presentation includes initial
surface publication, which happens before hooks are installed. Fixture timers
also run inside the message loop, so fixture maxima are not production drawing
benchmarks. No performance improvement is claimed from these checks.

Counters stay in memory unless **Settings > Privacy and data > Local diagnostics** is enabled.
One `Application / RecordCapture` entry then uses the existing bounded,
source-generated JSON writer. Consent is snapshotted at take start; changing
consent invalidates the old generation. Entries contain numeric counters and
outcomes, not geometry, screen pixels, transcript text, paths, or audio.

If physical failure persists, collect a real take with diagnostics enabled
and compare it with a record-only engineering probe. Inspect callback delay
alongside presentation/loop durations and frame-fence wait. A zero real-event
count must not be described as a successful input test. A dedicated hook thread
and incremental drawing should follow actual delay or hook-loss evidence.

## Verification

- Native no-UI checks pass input forwarding, monitor re-entry, cancellation,
  ABI sizes, PNG acknowledgement ordering, preservation of the next request,
  PNG failure reporting, and invalid/failed unhook ownership.
- Real WGC synthetic drawing passes two isolated burst pictures, all-mark
  summary, and visible ink before mouse-up. All capture HRESULTs are zero.
  The MP4 independently decodes to 107 frames without audio.
- A separate Esc-metadata fixture passes discard, zero summary, released
  ownership, and hook release before media join. It injects no OS input.
- Core tests pass 1532 checks. Seventeen focused checks pass on Boat Linux;
  they do not exercise Windows hooks, capture, or touchpads.
- NativeAOT MSIX publishing for 1.0.1.20 passes. Physical reliability under
  dictation load still requires an installed-app hand check.

The final isolated NativeAOT MSIX check uses identity
`BumpyClock.Ansible.RecordSpike.ink20r2`. It verifies the production managed/native
adapter with dynamic code disabled, released ownership, a real PNG, and an
independently decoded silent MP4. It opens no microphone and tests no physical
stroke delivery. The final discard fixture also requires the MP4-finalized
flag to stay clear.

Signed development build 1.0.1.20 was installed and restarted over the existing
identity. Settings, dictation history, and usage hashes were preserved at
installation. The installed helper matches the tested native binary. Record
capture and its independent toggle remain enabled. Physical drawing with ASR
active is still a hand check; these changes are not evidence that every
intermittent failure has been resolved.

Ignored evidence is under `.runtime/record-capture/reliability-20261105`,
`reliability-discard-20261105`, and `.runtime/record-spikes`. Screens and media
are not committed.
