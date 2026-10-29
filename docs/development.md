# Development

Run these commands from the repository root in PowerShell. See the
[README](../README.md) for the app overview, the [user guide](user-guide.md)
for workflows, and [native validation](native-validation.md) for model probes
and known backend failures.

## Requirements

- Windows x64 and the .NET 10 SDK.
- Visual Studio with Desktop development with C++ and single-project MSIX tooling.
- Git and `curl.exe` for source and model downloads.
- For the default Vulkan build: a Vulkan SDK with `Bin\glslc.exe`. The build
  reads `VULKAN_SDK`; pass `-VulkanSdk <SDK directory>` to select it explicitly.

The native runtime requires AVX2, FMA, F16C, BMI1/BMI2, and OS-enabled AVX state,
including when GPU inference is selected. ARM64 and x86 are not supported by
the current deployment path. A compatible Vulkan driver is required for GPU
recognition; a CPU-only build does not need the Vulkan SDK.

## Prepare the runtime

```powershell
.\tools\Setup-AudioBackend.ps1
.\tools\Build-AudioNative.ps1
```

Setup downloads and verifies `nemotron-asr-0.6b-q8` and
`nemotron-3-diarization-q8` by default. Choose another catalog entry with
`-Models <model-id>`, including `moonshine-tiny`. Setup does not build the DLL.
When no model ID is saved, dictation uses the installed Nemotron ASR. The
diarization companion cannot be selected for dictation, replay, or WAV
transcription. There is no meeting model picker. Start honors an explicit saved
streaming **Use** selection only when that model can stream meetings. An
automatic dictation default is not a meeting override. Explicit Nemotron or
Kroko without the companion fails Start rather than switching models.
Unconfigured meetings use Nemotron ASR with that companion when both are
installed. If Nemotron ASR is missing, installed Kroko uses the same companion.
If no paired Nemotron or Kroko ASR is available, meetings keep VibeVoice
streaming, including when the companion is absent.

The native build downloads pinned CMake and Ninja tools, checks out audio.cpp
v0.9.0, applies the tracked Vulkan loader and Nemotron streaming-timestamp
patches, and builds CPU plus Vulkan support. Compiled families include
`nemotron_asr` and `nemotron_3_diar`. It stages `audiocpp.dll`, its app-local
dependencies and notices in `.runtime\native`, and the public WAV in
`.runtime\validation`.

A Vulkan CMake configure can fail on a long worktree path when nested
shader-generator sources exceed `MAX_PATH`. Mapping a drive letter onto the
worktree (`subst Z: <worktree>`) and building from that root has progressed
past that failure. That is not evidence that the native build completed.

To build CPU only:

```powershell
.\tools\Build-AudioNative.ps1 -Backend cpu
```

Build the native runtime before building or launching the WinUI app.
Downloaded models and generated native files stay in the ignored `.runtime`
folder. The app package includes the runtime and public sample; it excludes
model weights. Select `.runtime\models` through **Speech models > Model folder >
Choose folder** to use setup's weights in the packaged app.

## Run in Visual Studio

Open `Ansible.slnx`, set `Ansible` as the startup project, select Debug/x64, and use
the **Ansible (Packaged)** launch profile. Packaged development deployment
requires Windows Developer Mode. F5 uses managed code for debugging; the
Release package workflow below verifies NativeAOT output.

Building alone does not refresh an already registered development app. Launch
**Ansible (Packaged)** from Visual Studio to deploy current files before testing
from the Start menu. NativeAOT compilation requires optimization: unoptimized
Debug AOT can deadlock inside WinUI reference tracking during garbage collection.
The build rejects that configuration; use the Release package workflow for AOT.

The solution's Any CPU platform maps the WinUI app to x64 because the app has
no Any CPU project configuration. ARM64 and x86 map to their named project
configurations, but the deployment checks reject them until separately qualified.

## Build and verify a package

```powershell
.\tools\Publish-Ansible.ps1
```

Publishing stages an existing native runtime; it does not compile one.
The default publish requires CPU and Vulkan support in the native runtime
record. A CPU-only artifact is rejected before packaging, even when a compatible
GPU and Vulkan SDK are installed. Rebuild the native runtime to add Vulkan.
To publish an intentional CPU-only build, use:

```powershell
.\tools\Publish-Ansible.ps1 -Backend cpu
```

The native build checks the compiled `GGML_VULKAN` setting before staging.
`native-runtime.json` records patch names and SHA-256 hashes plus compiled
model families. Staging and package verification reject a DLL whose hash
differs from that record, and reject a record whose patches or families do
not match the current recipe. Rebuild with `.\tools\Build-AudioNative.ps1`
after changing patches or compiled families; a matching DLL hash is not
enough. `.\tools\Test-NativeBuildRecipe.ps1` checks that stale and tampered
records are rejected. It does not compile the native runtime.
The script builds an unsigned, self-contained NativeAOT MSIX and prints its
verified path under `Ansible\bin\AppPackages`. Change the output directory with:

```powershell
.\tools\Publish-Ansible.ps1 -OutputDirectory .\.runtime\packages
```

Verification inspects the actual package for a native x64 executable, required
WinUI XAML embedded in `resources.pri`, resources, native dependencies, notices,
and `DeviceProbe\Ansible.DeviceProbe.exe`. Building the WinUI app publishes that
worker automatically (NativeAOT in Release). A managed build alone does not
verify this deployment. Publishing does not install or sign the package. Use the
packaged launch profile for development; the unsigned artifact is not ready for
direct installation.

## Design resources

`Ansible/DesignTokens.xaml` owns app colors, typography, shared dimensions, and
control styles. It is merged after the WinUI resource dictionary. Pages consume
semantic resources rather than palette literals. Brand roles are separate from
light/dark surface roles. A future color scheme can replace those brand roles
without rewriting page layouts. High Contrast uses Windows system colors.
The existing Windows font is retained.
`NavigationStyles.xaml` owns shared sidebar button and icon styles. Its selected
button template keeps the current page visible during hover and press. Control
interaction colors resolve through themed XAML resources, not copied brushes
from the application's current theme.

Every page renders into `PageCanvas`. This component owns the shared title,
subtitle, responsive header actions, content width, padding, scroll bounds, and
bottom space. Pages supply `Title`, `Subtitle`, `HeaderActions`, and body content;
they do not implement their own header grids or heading breakpoints. Pages with virtualized
lists use its bounded mode and let the list own scrolling. The design check
rejects page roots that bypass the shared canvas. `DeviceComparisonView` renders
structured benchmark snapshots without parsing presentation text.

The shell and meeting window use one Mica backdrop per window with transparent
chrome. Main pages use Microsoft's card pattern through the shared `CardStyle`;
the transcript window uses a contiguous `PageSurfaceStyle`. Both use WinUI's
translucent `LayerFillColorDefaultBrush` above Mica. Keep
these platform resources and Acrylic flyout resources unmodified so their
theme, High Contrast, inactive-window, transparency, and power fallbacks remain
available. Acrylic belongs to transient flyouts and menus, not the main page.
This follows Microsoft Learn's [Mica layering](https://learn.microsoft.com/windows/apps/design/style/mica#app-layering-with-mica),
[layering and elevation](https://learn.microsoft.com/windows/apps/design/signature-experiences/layering),
and [Acrylic guidance](https://learn.microsoft.com/windows/apps/design/style/acrylic).

Debug builds accept `--design-theme=light` and `--design-theme=dark` as launch
arguments for screenshot qualification. This scopes appearance to the app
window and does not change Windows settings. Release builds follow Windows.

Independent visual judges must invoke the `ux-designer` skill before reviewing
actual screenshots. Check both themes and responsive, error, and empty states.
Showing or dismissing transient notices must not change the page viewport bounds.

The app mark uses exact Pencil geometry. `Ansible/Assets/Brand` retains SVG
artwork derived from that geometry and native-size PNGs exported through Pencil MCP. There is no design
JSON snapshot in the repository. Regenerate package icons with the pinned
renderer, then check tokens:

```powershell
npm install --prefix .runtime/design-renderer --no-save @resvg/resvg-js@2.6.2
node .\tools\Generate-DesignAssets.cjs
.\tools\Test-DesignTokens.ps1
```

The generated Windows assets use transparent backgrounds and optical cuts for
small sizes. Tray artwork is available for future integration; the app does not
currently create a notification-area icon. Successful dictation history persists
locally in `dictation-history.json` beside `settings.json`, newest first, at most
100 entries within a 500,000-character budget. An oversized entry stays
current-result only. WAV results stay outside that store. The session emits a
typed successful dictation completion after
cleanup. One app-owned history instance receives it independently of page
lifetime and reloads after restart. History uses `CoalescingSnapshotWriter<T>`
after loading and merging saved rows. Changes made during loading are admitted
as one merged snapshot; shutdown waits for loading, drains and joins the writer.
Corrupt or unreadable history is left
untouched, a warning is shown, and new rows stay in memory until storage can be
recovered. History and Insights views consume typed projections; the dictation
page owns recording and file actions. Failed, cancelled, and unreleased
operations never enter history. Usage writes use a 32-entry bounded queue so
disk work never delays that completion or final insertion. Shutdown drains and
joins the writer. `UsageError` is shown in Insights after a failed or overflowed
write. Session notices display only undismissed warnings and errors, scoped by
area; capture-privacy failures are app-wide. Routine success and cancel notices
are not shown. Bounded `PageCanvas` layouts retain a finite minimum
height and allow outer overflow in short windows, so warnings cannot collapse their lists.
`SettingsRow` owns the shared adaptive setting
layout; pages provide labels, descriptions, and controls.
`Badge` sizes to its label up to 240 effective pixels, trims overflow, and uses
6px rounded corners with a 28px minimum height.

## Tests

### Dictation insertion

`WindowsTextTarget` uses UI Automation for destination safety and observation,
not `ValuePattern.SetValue`. The latter replaces a field's value and is not
caret insertion. The paste shortcut is an explicit preference, not a
process-name-based terminal detector.

UIA2 connection and transaction waits are set to 150 ms. Per-read and
post-delivery observation budgets are 750 ms and 600 ms; snapshots are limited
to 8192 UTF-16 code units. These are provider settings and checks between calls,
not hard deadlines for arbitrary in-process COM code or release. All calls and
COM ownership remain joined on the delivery worker; shutdown waits for it.
Missing, changing, oversized, or unsupported snapshots leave delivery unverified.
Only an exact document and caret delta in the original field is verified.
No destination text is logged or persisted.

Tests cover target identity loss, field changes, safety properties, exact
selection replacement, unknown verification, selected paste chords, partial
input, cleanup-only retries, cancellation, and outcome propagation. Real
Teams/Copilot field and terminal behavior still requires application qualification;
a passing unit suite or package build does not establish it.

### Core and native checks

Run the deterministic tests without native model dependencies:

```powershell
dotnet test .\Ansible.Tests\Ansible.Tests.csproj --filter 'Category!=NativeIntegration'
```

An unfiltered test run also executes native integration tests. Those require
the built DLL, public sample, and installed model weights. The mandatory
integration tests need both Moonshine and Nemotron ASR. Default setup does not
install Moonshine:

```powershell
.\tools\Setup-AudioBackend.ps1 -Models moonshine-tiny,nemotron-asr-0.6b-q8
dotnet test .\Ansible.Tests\Ansible.Tests.csproj --filter 'FullyQualifiedName~NativeEngineTests&Category=NativeIntegration'
dotnet test .\Ansible.Tests\Ansible.Tests.csproj --filter 'FullyQualifiedName~NativeStreamingDeliveryTests'
```

Wait for each command to finish before starting the next. These tests use the
public sample and do not open a microphone. `NativeStreamingDeliveryTests`
accepts `DICTATION_MODELS_DIRECTORY` for an existing model folder;
`NativeEngineTests` reads `.runtime\models`. They do not require the
diarization companion.

Paired meeting speaker checks need the native DLL, the public sample, and the
selected ASR plus `nemotron-3-diarization-q8`. The suite requires exactly one
passing native test per selected model. Missing weights, DLL, or sample fail
the run; tests are not skipped. This does not qualify GPU, multi-speaker, or
performance behavior. The paced two-minute CPU check runs separately through
`dotnet test --filter "FullyQualifiedName~NemotronPairedCpuKeepsPace"` with
the same native assets:

```powershell
.\tools\Test-SpeakerModels.ps1 -Model nemotron
.\tools\Test-SpeakerModels.ps1 -Model kroko
.\tools\Test-SpeakerModels.ps1 -Model both
```

Use `--stream-only` to skip WAV transcription, prepare once, then stream.
Add `--repeat 5` for five sequential paced streams on that session. The
selected ASR weights must be installed. Missing previews stay missing:

```powershell
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav nemotron-asr-0.6b-q8 `
  --backend cpu --stream-only --repeat 5
```

Paced elapsed time includes audio delivery and backpressure; it is not pure
inference latency. Nemotron Vulkan streaming remains unsafe; use CPU for that
model. The probe rejects a `diar` model ID.

Dictionary integration tests run for installed Qwen and VibeVoice weights,
and skip entries with missing weights. They also accept
`DICTATION_MODELS_DIRECTORY`. Run them separately after checking available
memory; installing the 7B model does not establish that it can load:

```powershell
dotnet test .\Ansible.Tests\Ansible.Tests.csproj --filter 'FullyQualifiedName~NativeDictionaryTests'
```

For native recognition changes, use the [probe commands](native-validation.md)
to check the affected model and backend. A passing CPU test does not qualify
Vulkan or another model. For WinUI, interop, or deployment changes, also run
`Publish-Ansible.ps1`. Documentation-only edits do not require downloads or
a native rebuild.

## Source and ownership

`DictationSession` has one admission and shutdown owner. Model replacement owns
candidate and previous engines. Recognition snapshots its admitted engine,
model and options. Streaming owns its pipe and tasks; only dictation owns
microphone capture and stop controls. Preview publication uses `StateChanged`;
shortcut insertion consumes the final operation outcome.
`ShortcutDictation` owns each shortcut attempt and its tasks. `MainWindow`
forwards hotkey events, and `WindowsShortcutTarget` supplies the Windows
destination. Final speech is sent at most once, only a deferred attempt
retries, and shutdown joins inspection, press, delivery and release-tail work.
Shortcut admission opens bounded in-memory microphone capture before Windows
target inspection finishes. Packets stay off the recognizer until inspection
succeeds. Rejected or failed inspection cancels or fails the operation and
discards that buffer, with no transcript, history row, or insertion. Hold-to-talk
release still uses the 500 ms tail and may Finish while inspection is pending.
Native model-load buffering is unchanged. The overlay waveform is shown after
capture opens. Accessible startup status is Starting microphone. Removing the
inspection wait does not prove a missing first word is gone, and it cannot
remove hardware microphone startup delay or speech before the shortcut.

`MeetingStore.ListAsync` returns summaries rather than transcript documents.
The in-memory cache validates file identity on each listing and reads changed
files one at a time. Opening a meeting loads and validates the full transcript.
Interrupted-session recovery is an owned startup task independent of dictation
initialization. It must finish successfully before a meeting can start.

`MeetingTranscriptProjection` retains positional presentation rows. Adjacent
source turns with the same speaker id become one visible block; a speaker
change or a later return after another speaker starts a new block. Typed
bindings update only changed properties in a constrained virtualized ListView.
ASR and speaker corrections can revise older turns; unchanged row text is not
reassigned. Expanding original wording is presentation state, not saved speech.

Meeting capture, persistence, speaker parsing, and lifecycle tests live in
`MeetingCaptureTests`, `MeetingCaptureDiagnosticsTests`, `MeetingCaptureWorkerTests`,
`MeetingPacketTimelineTests`, `MeetingStoreTests`,
`MeetingTranscriptTests`, `MeetingSessionTests`, and
`DiarizedMeetingSessionTests`. `MeetingCaptureDiagnosticsTests` is
deterministic. It covers status labels, missing versus silent samples, JSONL
field allowlists, string enum names, 1 MiB rotation, the nonblocking 64-entry
queue, and shutdown drain. `MeetingPacketTimelineTests` covers packet-clock
continuity, native-rate and zero counters, and real gaps.
`MeetingCaptureWorkerTests` runs deterministic tests of the real worker against
fake devices and a manual clock. Coverage includes mixing, stop-position drain,
contiguous activity, output discovery and thread ownership, apartment balance,
output loss, microphone timing and read failures, queue overflow, startup
cancellation, and retained ownership after release failure. It also covers
output Stop/Dispose exceptions, terminal health-handler exceptions, cancellation
during stop-drain, slow output-stop timing, and failing-clock teardown.
Scanner cases cover release of prepared but unadopted sources and transient
catalog retry. Output cases cover start retry with a new instance, fatal start
failure, and removed outputs. Timing cases reject invalid QPC/device positions.
These three deterministic classes open no devices and do not prove real WASAPI
behavior. The `NativeIntegration` test in `MeetingCaptureTests` remains the
hardware check. Shared session fakes `FakeMeetingCapture` and
`FakeMeetingCaptureFactory` live in `MeetingCaptureFakes.cs`. Live capture tests are marked `NativeIntegration` and generate an
in-memory tone. They open a microphone and play that tone, so they need
consent and an output device. Do not treat them as routine tests. Repeating
those native checks does not qualify Teams-selected missing audio.
`TeamsSpeakerObserverTests` covers marker parsing and conservative matching.
The read-only NativeIntegration test
`ExistingTeamsWindowCanBeReadWithoutInteraction` observes an already-open
Teams window. It does not click, record, upload, or change Teams settings:

```powershell
dotnet test .\Ansible.Tests\Ansible.Tests.csproj --filter 'FullyQualifiedName~ExistingTeamsWindowCanBeReadWithoutInteraction'
```

If Teams is not open, the test logs that and returns. A populated read does
not prove real-call name mapping.
`NativeMeetingTests` exercises the retained VibeVoice streaming path with the
public sample and checks that speech appears before input ends. Set
`DICTATION_MODELS_DIRECTORY` when using an existing model folder. Run native
probes sequentially. Use `Test-SpeakerModels.ps1` for paired Nemotron or Kroko
speaker checks.

`MeetingSession` owns the meeting operation and reserves `DictationSession`
before loading its own native engine. Reservation joins/unloads dictation
preparation and prevents dictation/settings/model maintenance until all meeting
capture and inference resources are released. Recognition callbacks queue saves
without waiting for disk. `MeetingPersistence` uses `CoalescingSnapshotWriter<T>`
with one active snapshot and one replaceable pending snapshot. It retains
meeting-specific diagnostics and failure handling. The worker starts writes
without a timer; Pause, End and Close flush the required revision. Disposal
joins attempts; only a successful flush confirms saving. History and Meetings
share scheduling, not their retention, serializers or recovery rules. Usage and
diagnostics keep their event queues and never coalesce entries.
Atomic replacement and disk flush remain required. Disk latency determines the
unsaved interval, so no fixed crash-recovery time bound is promised. Failed
completed-meeting edits retain their committed status and original wording.
Session tests
exercise completion from a single-thread UI context to catch save/refinement
deadlocks. Start snapshots recognition options before reserve. It honors an
explicit saved streaming dictation model only when that saved ID matches the
current selected meeting-capable ASR; otherwise it uses the unconfigured
Nemotron pair, Kroko pair, then VibeVoice path. Explicit Nemotron or Kroko
without the companion fails Start rather than switching models. Capture privacy is a visual
setting that can change during a meeting. Pause gates the same channel
reader; it does not reset the native stream. Capture writes 16 kHz mono PCM16
into bounded queues. `MeetingPacketClock` maps each packet start. When the
native device counter advances by the previous PCM sample count, the next
start continues from that length. Native-rate counters that do not match the
PCM count, and unavailable zero counters, keep the previous timestamp mapping.
Genuine device-counter gaps remain gaps. An 80-sample (5 ms) QPC snap remains
only as fallback. QPC correlation can move by more than five milliseconds
while native frames remain contiguous. This must not become an artificial
overlap or gap. `MeetingCaptureDiagnostics` and
`Windows\MeetingCaptureMetrics` publish source health at about 1 Hz. Capture
and downstream summaries log about every five seconds, plus Starting,
Recording, Failed, Stopped, and final pipeline events. Capture stats and
elapsed time reset on resume. Downstream counters span the whole meeting.
`ProcessedSeconds` stays null unless a recognition update reports its processed
sample count. Consumed seconds
count engine input. They are not proof of recognized speech. Window RMS fields
are maxima since the previous health snapshot (about one second).
`PeakInputRms` is cumulative. `MissingMixedSamples` counts timeline slots
absent from capture and filled as silence. That is distinct from WASAPI
silent samples. The RMS signal threshold is 0.001 (-60 dBFS). Recent-signal
status uses a five-second window. Local JSONL uses one bounded 64-entry
nonblocking queue under `diagnostics\` beside `settings.json`. Collection is
opt-in and off for missing or unreadable settings. Opt-out invalidates queued
records, while a write already in progress may finish. Existing files are kept.
Live capture health does not depend on logging. Session operation start and
terminal records share the queue and use monotonic millisecond durations.
The same queue carries typed meeting lifecycle, shortcut delivery, microphone
capture, native selection and session, model download, settings save, usage and
history load and save, meeting load, save, and flush,
permission, hotkey, startup, shutdown, and optional wording-cleanup terminal
records. Cleanup stages are `CleanupCopilot` and `CleanupOpenAI`. Those records
carry outcome, duration, and numeric HRESULT only. They never include transcript
text, keys, or raw stderr. Delivery latency begins when the recognition result becomes
available, before operation cleanup. Send duration ends when input handling
returns before verification. Microphone
backlog high-water is sampled after enqueue and can miss a concurrent dequeue.
Native preload records use a separate operation ID and set `BackgroundPreload`.
Download transfer and verification share an attempt ID. Installation has a
separate attempt ID. These records do not yet cover every app workflow.
The current file
`local-diagnostics.jsonl` rotates to `local-diagnostics.jsonl.previous` when it is
at least 1 MiB, two files total. Size can exceed 2 MiB by bounded record overhead. MainWindow
joins the logger on shutdown. Logging I/O failures do not stop capture. They
surface as `DiagnosticsError`. Dropped events are counted numerically. Write
failures emit a Trace warning with the HRESULT at most once per minute. Kind, stage, and pipeline phase serialize as names, not
numbers. Meeting playback is endpoint loopback of every active render device,
summed with saturation into one playback stream. Teams process loopback was
measured silent (about 0.000015 RMS) while endpoint loopback carried the call,
so `MeetingCaptureRequest.ProcessId` is used only for speaker naming. The
first output scan runs before the microphone starts. Later scans run every two
seconds on a dedicated MTA thread that opens, initializes, and services
endpoints, then queues them. The capture thread only starts queued endpoints,
because endpoint open measured 73 to 454 ms and would otherwise overflow the
microphone buffer. WASAPI buffers are 500 ms; packets stay about 10 ms. A late
device starts at the shared timeline cursor. Health adds `OutputDevices`,
`SkippedOutputDevices`, and `OutputDevicesLost`. Events add `OutputAdded`,
`OutputSkipped`, and `OutputLost`, plus stage `EnumerateOutputs`. Each source health may include bounded `LastPacketTiming` numeric
fields (`TimestampSample`, `ExpectedSample`, packet `Samples`, current and
previous device position, native `Flags`). Records
never include audio, transcript content, participant names, window titles,
device names, paths, or raw exception messages. The `ModelId` field carries only
an ID from the fixed catalog allow-list, and is null for any other model.
Transcripts use atomic source-generated JSON saves; audio never reaches disk.
Startup marks unfinished readable documents Interrupted.
Listing isolates unreadable files and returns their identifiers separately, so
they do not block healthy history or capture. The Meetings recovery action
exports the original bytes, flushes and completes the destination file, then
removes the app copy. Cancellation or export failure preserves the source.

`IMeetingCaptureFactory.StartAsync` returns one `IMeetingCapture` combining
`IAudioCapture`, `IMeetingAudioSourceMetadata`, and `IMeetingCaptureDiagnostics`.
`MeetingSession` does not probe optional capture capabilities at runtime.
Positions are capture-relative 16 kHz samples, with zero at microphone start.
Each capture after resume restarts at zero; the consumer owns meeting offsets.
`Activity` fires on the capture thread before each packet enters `Audio`.
Packets normally contain 640 samples; the last drain packet can be shorter.
`StartSample` is contiguous from zero. A throwing handler fails capture.
`ClockSamplePosition` is thread-safe. Emission uses a 250 ms holdback (4000 samples).
Packet granularity and scheduling can make the clock-to-emitted gap larger.
Normal stop freezes the clock and emits exactly to that position. `HealthChanged` fires on the capture thread. Subscribe first,
then read `Health` to replay the latest snapshot.
`StopAsync` and `DisposeAsync` are idempotent and join the worker.
`IsReleased` becomes true before `Audio` completes. A device release exception
keeps it false and faults `Audio` with an `AggregateException` stating that
resources remain owned. Failed adapter `Dispose` is terminal for this worker
after its COM owner thread exits. The capture retains failed resource references
and keeps `IsReleased` false. There is no cross-thread retry; new work stays
blocked. Startup returns a recording capture, throws after full release, or
throws `MeetingCaptureOwnershipException` carrying retained capture.

The internal `IMeetingAudioDevices` boundary owns per-thread COM apartment entry,
microphone open, and output catalog open. `IMeetingOutputCatalog` lists active
output IDs and opens loopback sources. `IMeetingAudioSource` starts, stops,
reads packets, and disposes. `WasapiMeetingAudioDevices` implements production
device access. An internal `MeetingCaptureFactory` constructor injects devices,
a 100 ns clock, and the output scan interval. The public constructor is unchanged.
Transient scanner catalog or apartment acquisition failures retry at the scan
interval on the scanner's own MTA thread. Fatal scanner failures surface instead
of silently leaving permanent microphone-only capture.

Paired Nemotron or Kroko meetings load the Nemotron 3 diarizer beside the ASR.
Both consume the same captured PCM, share memory admission, and reset or
dispose together. Native paired output is cumulative timed anonymous speaker
turns; unknown or overlapping speech stays unlabeled. The VibeVoice streaming
path remains when no paired Nemotron or Kroko ASR is available. That path still bounds native
history with `vibevoice_asr_streaming.max_history_steps=4096`. Capture admits
at most two hours of audio. The model's rolling history can affect speaker
continuity. The app parses native speaker labels and records approximate native
input-window timestamps. Optional Nemotron token anchors refine confident
untimed VibeVoice bounds; Teams observation matching names only supported,
source-exclusive aligned turns. Optional naming and refinement failures write
trace warnings. The meeting UI keeps editable speaker labels and does not show
`NamingStatus`, `RefinementStatus`, or `DeviceSelectionSummary` cards. Update
latency, memory over two hours, and multi-speaker accuracy require separate
qualification.

| Location | Responsibility |
| --- | --- |
| `Ansible` | WinUI pages, floating waveform, shortcuts, text insertion, composition and UI dispatch |
| `Ansible.Core\DictationSession.cs` | Operation lifecycle, settings, cancellation, preloading and shutdown |
| `Ansible.Core\ModelSelectionPolicy.cs` | Dictation and meeting model eligibility, default preference, meeting candidate order and companion requirement |
| `Ansible.Core\ModelStore.cs`, `HttpModelSource.cs` | Staged model files, resume metadata, verified commit/removal and pinned HTTP transfer |
| `Ansible.Core\ModelDownloadManager.cs` | Download UI snapshots, supported-family policy, one owned operation and joined cancellation |
| `Ansible.Core\ShortcutDictation.cs` | Shortcut admission, release tails, destination inspection, insert-once delivery, warnings and shutdown join |
| `Ansible.Core\CoalescingSnapshotWriter.cs` | Latest-pending snapshot scheduling, revision acknowledgement and joined shutdown for History and Meetings |
| `Ansible.Core\RecognitionContracts.cs`, `SessionState.cs` | Testable boundaries and immutable UI snapshots |
| `Ansible.Core\NativeAudioEngine.cs`, `NativeAudioApi.cs` | Native model/session ownership and C ABI |
| `Ansible.Core\AutoDeviceQualifier.cs` | Auto device identity, benchmark caches, isolated probe sequencing and exact-selection GPU rejection |
| `Ansible.Core\MeetingCaptureContracts.cs` | Meeting capture contract, factory, and ownership transfer on failed startup |
| `Ansible.Core\MeetingCaptureDiagnostics.cs` | Meeting source-health records and bounded local JSONL |
| `Ansible.Core\MeetingTextCleanup.cs`, `CopilotMeetingTextCleanup.cs`, `OpenAIMeetingTextCleanup.cs` | Meeting wording cleanup contract and Copilot CLI / OpenAI Responses providers |
| `Ansible\MeetingCleanupService.cs` | Provider routing and OpenAI Credential Locker key storage |
| `Ansible.Core\Windows` | Microphone capture, meeting device boundary, packet clock, capture metrics, and retained-resource teardown |
| `Ansible.Probe` | Real WAV and paced streaming recognition outside the UI |
| `Ansible.DeviceProbe` | Isolated CPU/Vulkan qualification worker, packaged under `DeviceProbe\` |
| `Ansible.Tests` | Deterministic contract tests and tagged native integration tests |
| `tools\audio-models.json` | Pinned model IDs, tasks, capabilities, downloads, checksums and licenses |
| `tools` | Setup, native build and package verification |

`App` supplies installation resources and per-user storage through `AppPaths`.
Pages share a session; `UiSessionObserver` dispatches versioned snapshots.
Capture, downloads and inference run off the UI thread. The session owns each
operation and rejects overlapping work or unsafe settings changes.

`AutoDeviceQualifier` has no native model handles. The engine verifies admitted
models before qualification and retains its operation gate. On a cache miss,
the engine releases resident weights and checks memory before the qualifier
starts a worker. Preparation, offline recognition and streaming share
`NativeAutoRecovery`; a GPU failure updates only its qualified-device ticket's
cache entry. Consumed live audio is never replayed. Qualifier contract tests use
temporary cache files and a fake worker without loading speech models.

`ModelStore` owns one directory's app-side file protocol. `ModelDownloadManager`
serializes its operations and joins transfer or verification before disposing
the store. Resume metadata is flushed to `.partial.json.pending`, then renamed
over `.partial.json`. The final model rename is the installation commit point.
Metadata cleanup failure leaves the verified model installed and publishes a
warning; Verify installed files retries cleanup when no partial body remains.
The directory lock coordinates app stores only. `Setup-AudioBackend.ps1` uses
a separate protocol and must not write the same folder concurrently.

`NativeModelIntegrity` remains the native-read authority. Catalog discovery
checks presence and length, not readiness for native use. Resident read leases
still block deletion, and session maintenance releases the engine before
installation or removal. `ModelStoreTests` uses a fake source and temporary
files; HTTP-handler tests cover resume and redirect rules without network access.

Launch owns a microphone permission preflight before initializing the session,
shortcut, and meeting detector. `AppCapability.CheckAccess` is passive;
`RequestAccessAsync` runs on the UI thread only for `UserPromptRequired`.
Both capture factories recheck admission without requesting permission. The
permission request, explicit retry, and Settings launch remain owned through
shutdown. Permission denial leaves file/model workflows available. Windows below
build 18362 use a passive `DeviceAccessInformation` check and Settings guidance;
no capture device is opened merely to provoke consent. See Microsoft's
[permission request API](https://learn.microsoft.com/en-us/uwp/api/windows.security.authorization.appcapabilityaccess.appcapability.requestaccessasync).

Closing the main window rejects new work and joins owned operations before
exit. Pending meeting wording cleanup is cancelled and joined. Disposal does
not start a new cloud cleanup.
Failed native teardown retains the resource owners and blocks new work;
it must not turn into a false idle state. Native cancellation waits for the
current blocking kernel before release. Keep capture queues and memory
admission bounded.

Meeting naming uses the selected Teams process on a dedicated worker. A
provider-side FindAllBuildCache query, scoped to each selected Teams window's
descendants, finds candidate MenuItems or nodes with known participant/video
automation IDs. Bounded raw traversal then runs only inside validated tiles.
Node, depth, and time budgets remain 256
nodes, depth 32, and 750 ms. Classification recognizes current Teams labels
such as `, video is on/off`, `Myself video, <name>, ...`, mute state, and
`Has control of production tools`, and it keeps comma-containing names.
Unmuted, video on, and roster presence do not prove speaking. Explicit
speaking markers in named tiles or their descendants remain necessary.
Mapping still requires aligned source-exclusive turns and independent
consistent evidence. Names are not inferred from transcript text. Popups,
minimized or hidden tiles, or Teams not exposing speaking metadata can prevent
naming. A baseline live read of 256 raw UIA nodes across Teams windows
returned Unavailable. Live success on a real call is not yet established. Optional
Nemotron timing runs on CPU through the existing C ABI; deterministic alignment
tests do not prove live ASR accuracy. Keep its 30-second audio ring in memory.
Meeting wording cleanup is `IMeetingTextCleanup`. Core snapshots the cleanup
choice at meeting Start. Automatic cleanup runs only after a successful End and
a local save of a completed original. Failed, discarded, or empty meetings are
not sent. Reopened meetings do not upload. Saving automatic OpenAI cleanup
requires a key and an OpenAI API model ID. No OpenAI model is guessed.
`CopilotMeetingTextCleanup` runs an isolated Copilot CLI subprocess. Obsolete
`--no-remote` is omitted. Obsolete `--no-remote-export` is `--sync-session off`.
Those flags were checked against Copilot CLI 1.0.103 with synthetic text.
Incompatible CLI stderr is mapped to a sanitized message. Isolation of tools,
hooks, and instructions applies only to that subprocess. It does not edit the
user's global Copilot config. `OpenAIMeetingTextCleanup` posts to the OpenAI
Responses API with `store` false. It uses source-generated JSON and `HttpClient`.
No extra package is required. `MeetingCleanupService` selects the provider and
stores the OpenAI key in Windows Credential Locker. Do not write the key to
`settings.json` or logs. There is no provider fallback. Manual retry confirms
the currently saved Settings provider and model. It does not edit them in the
meeting dialog. Test Copilot cleanup in `CopilotMeetingTextCleanupTests` with
synthetic text. Cover OpenAI cleanup in `OpenAIMeetingTextCleanupTests` for
`store` false, sanitized HTTP failures, and completed Responses parsing. Never
use personal meeting text in diagnostics or print authentication values.
Opt-in diagnostics may emit Application stages `CleanupCopilot` and
`CleanupOpenAI` with outcome, duration, and numeric HRESULT only. They never
include transcript text, keys, or raw stderr.

Automatic Teams detection enumerates active WASAPI capture sessions without
reading audio. It resolves capture owners through a bounded process ancestry
snapshot, checking creation-time order before identifying a Teams root. It
identifies Teams by process ID and start time, debounces a
candidate for three seconds, and rearms dismissed identities after ten seconds
of observed absence. `MeetingSuggestions` owns polling and offer lifetime on the
UI thread; `TeamsMeetingDetector` runs native enumeration off-thread. The shell
supplies availability and active selections, renders offers, and opens the notetaker.
Busy, failed, disabled, and stopped monitoring do not establish absence. Busy
metadata scans still suppress suggestions for an already-active meeting.
Opening rechecks the candidate. A stale or rejected open invalidates the offer
and requires fresh stable observations. Only explicit **Start** captures audio.
Shutdown cancels polling and confirmation before startup joins, then joins both
before disposal. Failed-shutdown recovery restarts only a previously started
monitor. `MeetingSuggestionsTests` uses a fake detector, UI synchronization
context, and `TimeProvider` to cover these contracts without Windows devices.
Keep the tagged native enumeration smoke test separate; real Teams join/mute
behavior requires live qualification.
Microsoft documents that session enumeration can race with newly created
sessions; this polling detector is best-effort. See
[GetSessionEnumerator](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessionmanager2-getsessionenumerator).

Preserve NativeAOT compatibility: use `LibraryImport` and source-generated
JSON, with no reflection-dependent serialization or runtime code generation.
When adding models, update catalog capabilities, compiled families, affected
callers and tests together. Keep speech local, and preserve runtime and model
license notices when redistributing them. See [AGENTS.md](../AGENTS.md) for the
full engineering constraints.
