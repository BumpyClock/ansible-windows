# Screen encoder feasibility

This phase-0 spike uses the existing WinUI app's explicit
`--record-encoder-spike` entry point. Normal app launch does not capture a screen
or change dictation. The entry point runs the C ABI helper on a joined worker,
records five seconds, inspects the resulting MP4, writes a report, and exits.
No microphone, loopback, dictation model, or network upload is involved.

## Implementation

`tools/record-spikes/encoder/record_encoder.cpp` uses monitor-targeted
Windows.Graphics.Capture, a two-frame free-threaded pool, D3D11 staging readback,
bounded CPU conversion, and a Media Foundation H.264 sink writer. Source frame
times come from WGC `SystemRelativeTime`. The helper preserves a QPC start anchor
and first-frame timestamp. The MP4 starts at its first captured frame. This
establishes a clock source; it does not establish synchronization with microphone
packets, ink, or final dictation words.

The corrected encoder owns one latest immutable image and one pending sample.
It submits the latest image on its bounded target cadence even when WGC has no
new update, and extends the final sample through the actual stop clock.
Real WGC updates, held encoder inputs, successful input submissions, submission
QPC bounds, and stop QPC are reported separately. A held image is not a fresh
screen observation. The capture item's Closed event and device HRESULT errors
remain failures; absence of an update alone does not prove a static screen.

The experimental format is a 1920-pixel long edge, even dimensions, 6 Mbps, and
a requested 30 fps media type. These are spike parameters. The product encoder
settings still need qualification. Media Foundation can duplicate frames.
Separate input and decoded counts must not be presented as 30 unique captured
frames per second.

The initial native host smoke test passed with corrected row orientation. It
submitted 92 WGC frames and decoded 147 output frames, at 1920 by 1200 pixels,
with a 4.9-second duration and zero audio streams. The report is
`.runtime/record-spikes/encoder/host-corrected.log`. A Windows MediaComposition
playback thumbnail was inspected locally. It showed upright, readable content
with the expected whole-monitor composition. The first implementation flipped
the RGB rows; that defect was found visually and removed before packaged
validation. Screen images and videos remain ignored local artifacts.

The initial orientation-corrected helper SHA-256 was
`02CF144CD5CAB98DA5A02932CC79F22AC542336BF1D1482883698BC825C37F67`.

## Initial packaged NativeAOT result

The required `tools/Publish-Ansible.ps1` command completed successfully with
CPU and Vulkan runtime verification. Its log is
`.runtime/record-spikes/publish-x64.log`. It verified native x64 executables,
the absence of the managed runtime, packaged XAML resources, and native imports
in the actual MSIX.

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
.\tools\Publish-Ansible.ps1 -OutputDirectory .\.runtime\record-spikes\packages
```

The published package is
`.runtime/record-spikes/packages/Ansible_1.0.1.14_x64_Test/Ansible_1.0.1.14_x64.msix`.
The isolated developer registration uses its extracted executable and resources,
with only the generated manifest's identity and display name changed. The source
manifest remains unchanged. The extracted helper matches the corrected build.

| Evidence | Observed value |
| --- | --- |
| Package identity | `BumpyClock.Ansible.RecordSpike_1.0.1.14_x64__wcy52t58f5v2e` |
| Runtime | .NET 10.0.14, `dynamic-code=False` |
| Windows | 10.0.26300.0 |
| Capture and decode | Both returned HRESULT 0 |
| Input WGC frames | 42 |
| Decoded H.264 frames | 148 |
| Dimensions | 1920 by 1200 |
| Duration | 4.9333 seconds |
| Audio streams | 0 |
| File size | 3,702,285 bytes |
| Worker outcome | `passed`, then process exit |

These counts are functional evidence. They do not qualify capture cadence or
performance. A thumbnail from the packaged MP4 was decoded with Windows
MediaComposition and inspected locally. It was upright, readable, and showed
the whole monitor. No window was moved or closed to create that scene.

The runtime report is
`%LOCALAPPDATA%/Packages/BumpyClock.Ansible.RecordSpike_wcy52t58f5v2e/LocalState/record-spikes/8ddc988f1dfb42a6bfa1478c65972420/result.txt`.
The silent MP4 is beside it. The report was also copied to
`.runtime/record-spikes/packaged-result.txt`. The package hash record is
`.runtime/record-spikes/package-hashes.txt`.

| Artifact | SHA-256 |
| --- | --- |
| MSIX | `BA635D2B01B502D3DC1A77C0EC99E436953449253B5FBC4785C417CBC990F022` |
| NativeAOT executable | `D9C989508F071A93B0326E928C3DE236431172FF682E7A7032977A420B06A4F7` |
| C ABI helper | `02CF144CD5CAB98DA5A02932CC79F22AC542336BF1D1482883698BC825C37F67` |

## Quiet-input regression and corrected package

The first overhead attempt exposed missing video time. Its last WGC update
preceded recognition, and the resulting MP4 was only 3.3 seconds despite a
worker lifetime spanning a five-second lead-in, recognition, and a two-second
tail. The source stopped producing new updates; its underlying cause was not
established. The former helper wrote only when a new WGC frame arrived and
ended its last sample after 1/30 second. It therefore omitted a quiet tail.
Requiring new WGC updates during all of recognition was also an incorrect
benchmark premise. The recording must hold an existing image on the real clock.

The corrected `video_encoder` is shared by real capture and a deterministic
static-input fixture. The fixture supplies one labeled synthetic image and no
WGC updates. It passed with 100 input submissions, 99 held inputs, 121 decoded
frames, 4.0333 seconds, and zero audio streams. A thumbnail at 3.8 seconds showed
the readable `SYNTHETIC STATIC FIXTURE` label on the red top half and the blue
bottom half. This verifies a quiet tail and orientation without desktop input.
Evidence is `.runtime/record-spikes/encoder/static-held-v2.log` and local
`synthetic-static-held-v2-tail.jpg` beside it. No image is committed.

The C ABI result grew from 56 to 88 bytes. Native host, WinUI interop, and
overhead probe callers use the same layout. Existing WGC timestamps retain
their source meaning. Additional counters identify successful `WriteSample`
submissions and held images; they do not claim instantaneous codec completion.
The final MP4 is decoded independently after the worker joins.

The full `Publish-Ansible.ps1` command succeeded again. The corrected MSIX was
extracted and registered as the fresh `BumpyClock.Ansible.RecordSpike.heldv2`
identity. Old spike identities, their captures, and existing app installations
were preserved. The application reported NativeAOT `dynamic-code=False`,
capture/inspection HRESULT 0, 55 WGC updates, 113 encoder submissions,
58 held inputs, 150 decoded frames, 1920 by 1200 pixels, five seconds, and zero
audio streams. Its final timeline covered the actual stop within one output
frame. A packaged thumbnail at 4.8 seconds was inspected upright and readable.
The owned application exited after saving its report.

| Corrected artifact | SHA-256 |
| --- | --- |
| MSIX | `54564757D2C16DA0F3D8A96E9BD63FBE9BF2169D91BEEF8E326FE72D4979DDDF` |
| NativeAOT executable | `7D5B0D2FE19839AD8CF87D81616FFD50A0BF9F43285FFDC8079437D68E644C4D` |
| C ABI helper | `3B82F2A06C74FEEF683CEE16EB2902F6B8357954CDDE85189EF9417A4528DEB7` |

Evidence is in `.runtime/record-spikes/publish-held-v2.log`,
`.runtime/record-spikes/package-hashes-heldv2.txt`, and
`.runtime/record-spikes/packaged-held-v2-result.txt`. The actual private runtime
report is `%LOCALAPPDATA%/Packages/BumpyClock.Ansible.RecordSpike.heldv2_wcy52t58f5v2e/LocalState/record-spikes/c340cbfa9ac64d67878940fd6a5ef4ec/result.txt`.
The revised registration script generates a fresh suffixed identity/layout by
default and still refuses to overwrite evidence for a supplied `-RunId`.
The full deterministic core suite passed 1467 tests with no skips or failures,
recorded in `.runtime/record-spikes/tests/held-v2-core.trx`.

## Border status

The normal packaged run returned `IsBorderRequired=true`. An explicit packaged
setter test returned success from `IsBorderRequired(false)` and then read false.
That second run also produced silent H.264 and exited successfully. Its report
is in run folder `6244b81680e9422c887393324907a595` beside the normal run.
Those API observations do not establish the physical border's appearance.
WGC recordings do not independently show the monitor's external capture border.
The visible yellow-border check remains a hand observation.

Microsoft documents that borderless capture needs the
`graphicsCaptureWithoutBorder` capability and user consent through
`GraphicsCaptureAccess.RequestAccessAsync(Borderless)`. The setter may succeed
while Windows ignores the request. This spike declares no such capability and
opens no consent prompt. Borderless capture is therefore not qualified by the
setter result. See [IsBorderRequired](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired).

## Packaging evidence

The initial x86 MSBuild invocation reached NativeAOT generation and wrote an
MSIX, then stalled waiting in
`Microsoft.Build.Msix.MakeAppx.WinAppSdkMakeAppx` tool notifications. WinDbg
identified the waited process as PID 50912; process enumeration confirmed it
had already exited. The owned idle build and task host were stopped and joined.
No unrelated app was stopped. The initial MSIX predated the corrected helper
and was never registered or run.

The publish script now selects Visual Studio's amd64 MSBuild and disables node
reuse for the packaging invocation. The successful retry also disabled build
servers in its process environment. The evidence establishes the original
notification stall and the successful changed invocation. It does not isolate
one of these settings as its cause.

The [encoder harness guide](../tools/record-spikes/encoder/README.md) contains
the exact build and isolated-package invocation commands. Its registration
script compares the published helper hash with the latest built helper before
launch, and records the MSIX, executable, and helper hashes. A different helper
is rejected. It registers an isolated `BumpyClock.Ansible.RecordSpike` identity
with a unique run suffix, using already
enabled Developer Mode. It leaves existing Ansible installations and certificate
trust alone.

## Limits

The [separate overhead probe](record-chord-overhead.md) records the bounded CPU
comparison and its limits. No hardware-encoder selection proof, long-session
run, monitor reconfiguration recovery, or capture-border hand acceptance is
claimed here. The CPU scaling path tests feasibility, not the final GPU
conversion design. Input blocking, ink, automatic paste, retention, and
record-chord settings are outside this spike.
