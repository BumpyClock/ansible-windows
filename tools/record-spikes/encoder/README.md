# NativeAOT screen encoder spike

This is an explicit engineering entry point. Normal app launch never captures
the screen. It does not implement the record chord or ink.

Build the small C ABI helper, then publish the actual WinUI NativeAOT MSIX.

```powershell
.\tools\record-spikes\encoder\Build-EncoderSpike.ps1
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
.\tools\Publish-Ansible.ps1 -OutputDirectory .\.runtime\record-spikes\packages
```

The helper DLL is included when present. The native host is an additional smoke
test. It is not a substitute for the packaged NativeAOT run.

```powershell
.\.runtime\record-spikes\encoder\record_encoder_host.exe `
  "$PWD\.runtime\record-spikes\encoder\host-capture.mp4"
```

Run the quiet-input regression through the same encoder. It provides one
clearly labeled synthetic image, sends no WGC updates, and requires a decoded
four-second silent video with held encoder samples.

```powershell
.\.runtime\record-spikes\encoder\record_encoder_host.exe `
  "$PWD\.runtime\record-spikes\encoder\synthetic-static-fixture.mp4" --static-fixture
```

Run the published package through a separate developer identity. Windows
Developer Mode must already be enabled. The script does not enable it or change
certificate trust. Registration leaves the existing Ansible identities alone.
Each invocation creates a fresh suffixed RecordSpike identity and layout. A
specific `-RunId` can be supplied; existing registrations or evidence for that
ID are never replaced.

```powershell
.\tools\record-spikes\encoder\Invoke-PackagedEncoderSpike.ps1 `
  -Msix .\.runtime\record-spikes\packages\Ansible_1.0.1.14_x64_Test\Ansible_1.0.1.14_x64.msix
```

The invocation captures the monitor under the pointer for five seconds. It opens
no microphone. `ApplicationData.Current.LocalFolder/record-spikes/<run>/` holds
the silent MP4 and `result.txt`. The report records the package identity,
NativeAOT dynamic-code status, real WGC update times/count, successful encoder
submissions, held-image count, real stop clock, decoded H.264 count, duration,
and audio stream count. Its `outcome=passed` requires decoded silent video that
covers the real stop clock within one output frame. Screen contents stay local.
Delete the generated files explicitly when
they are no longer needed. The product retention policy is not implemented.

The helper uses `IGraphicsCaptureItemInterop.CreateForMonitor`, a free-threaded
two-frame WGC pool, D3D11 staging readback, and an MF sink writer. It holds one
latest immutable image and one pending sample in addition to the pool and
conversion buffers. The encoder continues at its bounded target cadence when
WGC supplies no new frame and extends the final sample through its real stop.
Held samples are not reported as fresh WGC updates. Capture and
encoding execute on the caller's worker. Closing the session and finalizing the
writer complete before that worker returns. A passed native cancellation event
ends the loop. The app spike has a fixed five-second lifetime and exits only
after the worker joins. Monitor size changes, the capture item's Closed event,
and device HRESULT failures fail the take.

The spike uses a 1920-pixel long-edge cap, even dimensions, a requested 30 fps
media type, and 6 Mbps. These are test parameters, not settled product defaults.
CPU nearest-neighbor scaling is adequate to test the ABI, not a final encoder
design. Media Foundation can duplicate frames to satisfy the output media type.
Input WGC frame count and decoded output count are reported separately. Neither
proves 30 unique screen frames per second. WGC frames use their original
`SystemRelativeTime`; the MP4 starts at its first frame. The report preserves
the QPC start anchor and first-frame offset needed for a shared take clock.

Extract a playback thumbnail for visual orientation and scaling checks. The
thumbnail uses Windows MediaComposition's decoder. It does not inspect or
reorient raw RGB rows. Keep this screen image local.

```powershell
.\tools\record-spikes\encoder\Extract-VideoFrame.ps1 `
  -Video <absolute-mp4-path> -Image <absolute-new-jpg-path>
```

Use `--borderless` on the native host or pass `-Borderless` for the initial
packaged run to test the `IsBorderRequired(false)` setter. A successful setter
and a false getter do not establish that the border disappeared. Microsoft
documents that borderless capture also needs user consent through
`GraphicsCaptureAccess.RequestAccessAsync(Borderless)` and the
`graphicsCaptureWithoutBorder` package capability. This spike declares neither
and never opens that consent prompt. Visible yellow-border and borderless
acceptance remain hand observations until tested with explicit consent.

API references read for this spike:

- [CreateForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor).
- [IsBorderRequired](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired).
- [Sink writer tutorial](https://learn.microsoft.com/en-us/windows/win32/medfound/tutorial--using-the-sink-writer-to-encode-video).
