# Record chord feasibility

This is phase 0. The record chord, ink overlay, automatic bundle delivery,
retention, and Settings controls are not implemented by these spikes.
The normal Ctrl+Win workflow remains voice-only.

## Source baseline

- Ansible starts from `origin/main` at `66e7a6a`.
- The reference clone is `~/Projects/references/scrawl` at
  `b5fa0705f6e5724ede414727797b4b6ed274d3d2`.
- Scrawl's Windows `src/win/media.rs` uses monitor capture, a two-frame WGC
  pool, GPU conversion to NV12, and a Media Foundation H.264 sink writer.
  Its frame timestamps use the QPC clock.
- Scrawl's Windows `src/win/clipboard.rs` publishes PNG and DIBV5 alongside
  Unicode text. Its bundle handoff uses one contact sheet. This source does
  not establish multi-image acceptance in any requested target.
- Scrawl is MIT licensed. These are architectural references, not evidence
  that Ansible's capture or target integration passes.

## Reproduction and findings

The encoder spike is under `tools/record-spikes/encoder`.
The [Scrawl implementation notes](record-chord-scrawl-reference.md) trace its
Windows capture, input, frame selection, timing, and clipboard paths.
The [word timing report](record-chord-word-timings.md) describes the native
dictation timing checks. The [clipboard guide](record-chord-clipboard.md)
contains the manual acceptance procedure and result matrix.

Spike 1 passed the published NativeAOT execution check. The
[encoder report](record-chord-encoder.md) records a successful WGC-to-H.264
run under an isolated developer package identity, using the executable and
corrected helper extracted from the verified MSIX. It produced 1920x1200 video,
148 decoded frames, 4.9333 seconds, zero audio streams, and 3702285 bytes.
The runtime reported `dynamic-code=False`. The 42 captured WGC input frames
are distinct from the decoded output count. Media Foundation can duplicate
frames; this does not establish 30 unique captured frames per second.

The default border getter was true. A second packaged borderless setter test
returned success and a false getter. These do not prove physical border
visibility or absence. Borderless consent and the associated manifest capability
were not requested. Both visual border observations remain pending hand checks.

Spike 3 passed on the public sample. Nemotron mapped all 28 final words and
26 of 26 preview snapshots. Kroko mapped all 28 final words and 10 of 10 preview
snapshots. Nemotron anchors are native 80 ms token emission frames. Kroko ends
extend to the next word's start, including pauses; keep Kroko placement
approximate pending acoustic qualification. Final callbacks carry 225151 input
samples. These checks do not qualify microphone/video synchronization.

The clipboard harness published as a native x64 executable. Exact clipboard
readback passed text, files, combined files/text, bitmap/text, and composite/text,
including all three privacy formats and Shell file-path readback. Eight-file
publication also passed. No paste input was sent. All 40 manual target cases
remain pending. The guide provides the commands and result sheet.

Screen recordings and native outputs stay under ignored `.runtime` paths or
the explicitly invoked packaged spike's private local data folder. Do not
commit captured screens or paste content into online chats automatically.

## Gates before product implementation

The clipboard attachment cap and fallback ladder remain undecided until the
manual target matrix is complete. Clipboard format readback establishes
publication only. It cannot establish attachment insertion or text preservation
inside another app.

Capture overhead remains unmeasured. Spike 4 must compare the same recognition
workload with and without capture and encoding, after encoder feasibility.
Build and recognition smoke checks are not performance qualification.

The timing spike provides sample-relative native metadata. A future take still
needs an explicit QPC start anchor shared with audio, ink, frames, and video.
No microphone packet timing or cross-media synchronization is inferred here.

## Verification

The final Windows core suite passed 1467 tests, with no failures or skips.
Results are in `.runtime/record-spikes/tests/record-spike-final.trx` and
`.runtime/word-timing-results/word-timing-final-core.trx`.
The focused mapping, meeting-alignment, and streaming-probe suite passed 73
tests on both Windows and a Boat Linux sandbox using the .NET 10 SDK container.
Boat output is in `.runtime/record-spikes/boat-portable-result.json`.

`Publish-Ansible.ps1` completed and verified the NativeAOT MSIX, including its
native executables, XAML resources, and native runtime closure. A prior x86
MSBuild invocation stalled after writing the package while waiting for an exited
MakeAppx process. The x64 MSBuild invocation with node reuse and build servers
disabled completed.
The publish script now uses that invocation. This is evidence for the combined
invocation change, not proof that process architecture alone caused the stall.
The successful output is `.runtime/record-spikes/publish-x64.log`.

An attempted full suite on Boat hit Windows-only file identity imports in
existing storage tests and did not complete. It is not a passing whole-suite
Linux qualification. The portable focused check passed separately. Capture,
WinUI, and MSIX runtime checks run on Windows. Boat's Linux environment cannot
qualify them.

## Assumptions awaiting confirmation

The following are proposed defaults, not implemented product settings.

1. Alt+Win is the opt-in record chord. Settings explains screen capture and
   input blocking. Validate conflicts with Ctrl+Win and mask Alt menu activation.
2. Fix the pointer's monitor at Start. Block input on every monitor and accept
   ink only on the recorded monitor.
3. End an ink burst after 1.5 seconds idle. Capture its frame before a one-second
   fade. Clicks create numbered pings. Block right/middle/wheel input. Esc
   discards the entire take.
4. With no ink, paste one plain final frame. Burn each frame's number and time
   into the picture.
5. Provisionally cap images at eight, seven bursts plus summary. Later bursts
   remain in the text and summary. Downscale pictures to a 1920-pixel long edge.
   The acceptance matrix sets the final cap.
6. Use the existing five-minute limit. Target silent H.264 near 30 fps, with
   resolution and bitrate chosen from encoder measurements.
7. Exclude the pill and recording indicator from capture. Include the ink overlay.
8. Paste a short frame legend and a local video URL. The video-link setting
   defaults on.
9. Capture, ink, or encode errors allow the authoritative text paste. Omit
   failed media and report warnings/errors under existing notice rules.
10. Apply the same clipboard monitor, history, and cloud-sync exclusions to images.
11. Without an editable destination, retain the bundle in the Dictation result
    with manual Copy actions.
12. Save the pasted text, including markers, in dictation history. Keep media
    outside `dictation-history.json`.
13. Choose the paste fallback after hand checks. Candidates are a single bundle
    paste, text plus summary, a composite contact sheet, or sequential paced
    image pastes only on targets where measured successful.

The settled product decisions in the implementation request remain the contract.
Confirming these defaults does not replace the outstanding feasibility gates.
