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

The experimental format is a 1920-pixel long edge, even dimensions, 6 Mbps, and
a requested 30 fps media type. These are spike parameters. The product encoder
settings still need qualification. Media Foundation can duplicate frames.
Separate input and decoded counts must not be presented as 30 unique captured
frames per second.

The native host smoke test passed with the corrected row orientation. It
submitted 92 WGC frames and decoded 147 output frames, at 1920 by 1200 pixels,
with a 4.9-second duration and zero audio streams. The report is
`.runtime/record-spikes/encoder/host-corrected.log`. A Windows MediaComposition
playback thumbnail was inspected locally. It showed upright, readable content
with the expected whole-monitor composition. The first implementation flipped
the RGB rows; that defect was found visually and removed before packaged
validation. Screen images and videos remain ignored local artifacts.

The corrected helper SHA-256 is
`02CF144CD5CAB98DA5A02932CC79F22AC542336BF1D1482883698BC825C37F67`.

## Packaged NativeAOT result

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
is rejected. It registers only `BumpyClock.Ansible.RecordSpike`, using already
enabled Developer Mode. It leaves existing Ansible installations and certificate
trust alone.

## Limits

No capture-overhead comparison, hardware-encoder selection proof, long-session
run, monitor reconfiguration recovery, or capture-border hand acceptance is
claimed. The CPU scaling path tests feasibility, not the final GPU conversion
design. Input blocking, ink, automatic paste, retention, and record-chord
settings are outside this spike.
