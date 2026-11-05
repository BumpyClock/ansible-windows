# Native record take

`record_capture.dll` owns a take's Windows Graphics Capture session, silent H.264
sink writer, no-activate ink window, excluded recording indicator, and scoped
mouse/Esc hooks. Its C ABI is in `record_capture.h`. Managed code owns the output
directory, lifetime, speech, text formatting, retention, and clipboard delivery.

Builds of the WinUI project build this DLL incrementally and include it in the
MSIX. To build its native test hosts separately:

```powershell
.\tools\record-spikes\encoder\Build-EncoderSpike.ps1
.\tools\record-capture\Build-RecordCapture.ps1 -TestHost
.\.runtime\record-capture\record_capture_host.exe --self-check .runtime\record-capture\self-check
.\.runtime\record-capture\record_capture_fixture.exe --input-self-check
.\.runtime\record-capture\record_capture_fixture.exe --reliability-self-check
```

The self-check validates ABI sizes, pre-run cancellation, and single-use
ownership. The input self-check calls the production input handler with synthetic
mouse metadata. It verifies movement forwarding while hover stays ink-free,
dragging records geometry, and button/wheel events never forward. Neither
self-check opens the overlay, captures the screen, or injects OS input.

The reliability self-check also supplies controlled PNG-save and unhook results.
It verifies that live presentation is acknowledged before encoding, a following
request is not cleared by that save, PNG errors surface, and invalid hook handles
release ownership while other errors remain owned. It opens no UI.

The following checks display capture UI and block buttons and scrolling on every monitor.
Pointer movement remains available.
The first saves the real desktop. The second displays a clearly labeled synthetic
background and supplies synthetic mouse metadata through the production input
handler on its actual input thread. Neither injects operating-system input, records audio, pastes, or sends a
message. Coordinate these checks with the person using the computer.

```powershell
.\.runtime\record-capture\record_capture_host.exe --capture .runtime\record-capture\actual-no-ink 4
.\.runtime\record-capture\record_capture_fixture.exe .runtime\record-capture\synthetic-ink-fixture
.\.runtime\record-capture\record_capture_fixture.exe --discard .runtime\record-capture\synthetic-discard-fixture
.\.runtime\record-capture\record_capture_fixture.exe --foreground .runtime\record-capture\foreground-ink-fixture
```

The fixture requires a monitor at least 1600 by 800 physical pixels. It checks
two burst pictures independently and checks that the summary contains both
bursts. It also reads the composed desktop into `during-drag.png` before sending
the synthetic button-up. Its pixel checks require visible red ink while the drag
is still held, and fail if the second picture retains the first burst.
Both hosts independently decode the actual MP4 and reject an audio stream.
Output directories should be unique when preserving evidence.

`--foreground` leaves the actual focused app visible instead of creating the
synthetic backdrop. It requires the ink HWND to remain topmost before and during
the held drag, checks visible ink and real WGC pictures, and fails if foreground
identity changes. It captures the current app locally, blocks buttons briefly,
and sends no OS input or chat message. This case is required for ink visibility
changes; a backdrop-only pass can hide foreground occlusion.

## Ownership and bounds

`create` allocates one handle using the pointer's monitor and accepted-chord QPC
origin supplied by managed code. `run` runs on a managed-owned worker. Its input
message pump starts and joins a separate capture/encode worker. `stop` latches
even before `run` enters. `get_status` and `get_burst` read bounded snapshots.
`destroy` refuses an executing handle. A second executing native take is rejected.

Mouse hooks apply only while the take is active. Left input supplies geometry;
all mouse buttons and the wheel are swallowed on every monitor. Movement is
forwarded to Windows so the cursor can move during hover and ink dragging.
Only geometry
on the recorded monitor is accepted. Esc discards the take. Neither ink nor the
recording indicator activates a window. The indicator requires display-affinity
exclusion. The ink window is deliberately included in capture.

Ink and indicator HWNDs are created with `WS_EX_TOPMOST`, alongside their
no-activate and tool-window styles. In the foreground regression, a later
`SetWindowPos(HWND_TOPMOST)` returned success without promoting an initially
ordinary ink window. Explicit topmost creation keeps ink above the focused app
while preserving that app's keyboard focus.

Live ink is drawn into one monitor-sized, reusable premultiplied BGRA DIB and
published with `UpdateLayeredWindow`. Transparent pixels remain transparent;
the fade changes the published surface's constant alpha. The ink HWND never
mixes this path with `SetLayeredWindowAttributes` or window-DC painting. The
indicator retains its separate window-DC renderer. Surface allocation has the
same 33,554,432-pixel bound as WGC, and teardown releases the selected bitmap and
DC before the capture-worker join. The worker owns separate frame pixels; GDI+
stays initialized until it exits. The status ABI is 112 bytes, including ink
diagnostic counters and an explicit successful-video-finalization flag. The
burst ABI remains 24 bytes. The pixel format follows
the [Windows alpha blend contract](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-blendfunction)
and [layered-window publishing API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow).

A burst ends after 1.5 seconds without drawing. Its window is held through a
WGC presentation fence before saving its picture. New points arriving during
that short fence are retained for the next burst. The hold ends when a matching
WGC frame is retained, before PNG encoding. Fences use the QPC time immediately
after surface publication and before `DwmFlush`. Strokes then fade for one
second, interrupted when a new burst starts. Finalization clears the live ink,
waits for a clean captured frame, and draws all retained geometry exactly once
with numbered markers. With no ink, the final frame remains plain. The first
seven bursts can save pictures; every burst still has metadata and summary ink.

Geometry is bounded at 256 bursts, 4096 strokes, and 65536 distinct points.
Exceeding a limit surfaces an ink error and releases input. No geometry is
silently dropped. WGC uses a two-frame pool. The encoder retains one image and
one pending sample. Pictures and video have a 1920-pixel maximum long edge.
The provisional video request is H.264, 30 fps, 6 Mbps. Encoding ticks are bounded
to at most 30 per second and never queue catch-up work.

Video timestamps are relative to the first real WGC image. `first_video_qpc`
exposes its actual position on the take clock. A genuine startup gap is retained,
not replaced with a later image backdated to the chord. Quiet screens repeat a
real held image through the real stop time. Source updates, encoded inputs, and
held inputs are counted separately. `video_encoder.h` is shared with the
published NativeAOT encoder spike, including its quiet-tail regression.

Every exit releases hooks and destroys the ink windows before waiting for media
teardown. Discard skips MP4 finalization. Only successful finalization qualifies
a video link. Invalid hook handles count as already released; other unhook
failures surface and retain ownership. Re-entry after leaving the monitor starts
a new stroke instead of connecting across the excluded area.

Native hosts report event age, event counts, presentation and loop work maxima,
frame-fence wait, and pre-join hook release. The production app uses its existing
opt-in local diagnostics writer for these numeric counters. Synthetic metadata
does not qualify physical hook delivery or recognition-load performance. See
[the reliability report](../../docs/record-chord-ink-reliability.md).

## Recorded validation

On 2026-11-04, Windows 11 build 26300, the native self-check passed with an
80-byte status and 24-byte burst layout. A four-second real-desktop run passed
with one readable 1920 by 1200 final PNG and a silent H.264 MP4 independently
decoded to 98 frames and 3.2666 seconds. Its first real image arrived 748.7 ms
after the host's origin. That gap explains the shorter MP4 and remains explicit.

The synthetic geometry fixture passed with two bursts and three saved pictures.
Pixel checks observed left/right geometry as `(true,false)` in picture 1,
`(false,true)` in picture 2, and `(true,true)` in the summary. All three pictures
were visually inspected. Labels were readable, burst ink was isolated, summary
numbers were visible, and the excluded indicator was absent. A decoded MP4
frame also visibly contained the second burst's arrow. The actual silent video
decoded to 98 frames and 3.2666 seconds. All capture/ink/video/frame HRESULTs were
zero. Every owned worker exited and joined.

The shared static encoder regression also passed after extracting the common
header. One synthetic image with zero WGC updates produced a four-second silent
video with held samples. These are functional checks, not performance claims.
The synthetic fixture exercises real WGC, rendering, fences, and encoding. It
does not qualify physical mouse delivery, Esc, multi-monitor behavior, five-minute
takes, or target attachment acceptance. NativeAOT/MSIX checks belong to the full
app validation, not the native host alone.

Ignored evidence is under `.runtime/record-capture/actual-no-ink-30fps`,
`.runtime/record-capture/synthetic-ink-fixture`, and
`.runtime/record-capture/shared-encoder-static.mp4`. The fixture's inspected video
frame is `synthetic-ink-fixture/video-ink.jpg`. No captured screen is committed.

The strengthened held-drag fixture passed on 2026-11-04 with both the prior
window-DC renderer and the per-pixel renderer. The per-pixel run saved two burst
pictures and one summary, with left/right red checks `(true,false)`,
`(false,true)`, and `(true,true)`. The composed desktop read before button-up
contained `(true,false)`, and visual inspection confirmed a red rectangle.
Every capture, ink, frame and video HRESULT was zero. The silent MP4 decoded to
107 frames and 3566.63 ms. Native lifecycle and input self-checks also passed.
Ignored evidence is under `held-drag-baseline-20261105` and
`held-drag-per-pixel-20261105` inside `.runtime/record-capture`.

The old renderer's passing held-drag test does not reproduce the reported
packaged-app failure. The per-pixel run proves native live presentation and WGC
capture in this fixture; it does not establish that physical drawing in the
installed WinUI app is fixed. That requires a packaged-app check.
