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
```

The self-check validates ABI sizes, pre-run cancellation, and single-use
ownership. It does not open the overlay or capture anything.

The following checks display capture UI and block mouse input on every monitor.
The first saves the real desktop. The second displays a clearly labeled synthetic
background and supplies synthetic geometry directly on the production input
thread. Neither injects operating-system input, records audio, pastes, or sends a
message. Coordinate these checks with the person using the computer.

```powershell
.\.runtime\record-capture\record_capture_host.exe --capture .runtime\record-capture\actual-no-ink 4
.\.runtime\record-capture\record_capture_fixture.exe .runtime\record-capture\synthetic-ink-fixture
```

The fixture requires a monitor at least 1600 by 800 physical pixels. It checks
two burst pictures independently and checks that the summary contains both
bursts. Its pixel checks fail if the second picture retains the first burst.
Both hosts independently decode the actual MP4 and reject an audio stream.
Output directories should be unique when preserving evidence.

## Ownership and bounds

`create` allocates one handle using the pointer's monitor and accepted-chord QPC
origin supplied by managed code. `run` runs on a managed-owned worker. Its input
message pump starts and joins a separate capture/encode worker. `stop` latches
even before `run` enters. `get_status` and `get_burst` read bounded snapshots.
`destroy` refuses an executing handle. A second executing native take is rejected.

Mouse hooks apply only while the take is active. Left input supplies geometry;
all mouse buttons and the wheel are swallowed on every monitor. Only geometry
on the recorded monitor is accepted. Esc discards the take. Neither ink nor the
recording indicator activates a window. The indicator requires display-affinity
exclusion. The ink window is deliberately included in capture.

A burst ends after 1.5 seconds without drawing. Its window is held through a
WGC presentation fence before saving its picture. New points arriving during
that short fence are retained for the next burst. Strokes then fade for one
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
