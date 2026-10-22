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
transcription. Unconfigured meetings use Nemotron ASR with that companion when
both are installed. If Nemotron ASR is missing, installed Kroko uses the same
companion. If no paired Nemotron or Kroko ASR is available, meetings keep
VibeVoice streaming, even when the companion is present.
There is no meeting model picker.

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

Every page renders into `PageCanvas`. This component owns content width,
responsive padding, scroll bounds, and bottom space. Pages with virtualized
lists use its bounded mode and let the list own scrolling. The design check
rejects page roots that bypass the shared canvas. `DeviceComparisonView` renders
structured benchmark snapshots without parsing presentation text.

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
currently create a notification-area icon. In-app history is bounded and held
only in memory. The session emits a typed successful dictation completion after
cleanup. One app-owned history instance receives it independently of page
lifetime. History and Insights views consume typed projections; the dictation
page owns recording and file actions. Failed, cancelled, and unreleased
operations never enter history. Bounded `PageCanvas` layouts retain a finite minimum
height and allow outer overflow in short windows, so warnings cannot collapse their lists.
`SettingsRow` owns the shared adaptive setting
layout; pages provide labels, descriptions, and controls.

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

Meeting capture, persistence, speaker parsing, and lifecycle tests live in
`MeetingCaptureTests`, `MeetingStoreTests`, `MeetingTranscriptTests`,
`MeetingSessionTests`, and `DiarizedMeetingSessionTests`. Live capture tests are marked `NativeIntegration` and
generate an in-memory tone; they require microphone access and an output device.
`NativeMeetingTests` exercises the retained VibeVoice streaming path with the
public sample and checks that speech appears before input ends. Set
`DICTATION_MODELS_DIRECTORY` when using an existing model folder. Run native
probes sequentially. Use `Test-SpeakerModels.ps1` for paired Nemotron or Kroko
speaker checks.

`MeetingSession` owns the meeting operation and reserves `DictationSession`
before loading its own native engine. Reservation joins/unloads dictation
preparation and prevents dictation/settings/model maintenance until all meeting
capture and inference resources are released. Recognition, including final
transcript callbacks and their saves, runs without the UI synchronization
context. End joins this work before releasing the reservation. Session tests
exercise completion from a single-thread UI context to catch save/refinement
deadlocks. Capture privacy is a visual
setting that can change during a meeting. Pause gates the same channel
reader; it does not reset the native stream. Capture writes 16 kHz mono PCM16
into bounded queues. Transcripts use atomic source-generated JSON saves; audio
never reaches disk. Startup marks unfinished readable documents Interrupted.
Listing isolates unreadable files and returns their identifiers separately, so
they do not block healthy history or capture. The Meetings recovery action
exports the original bytes, flushes and completes the destination file, then
removes the app copy. Cancellation or export failure preserves the source.

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
source-exclusive aligned turns. Snapshot `NamingStatus` and `RefinementStatus`
fields remain. Update latency, memory over two hours, and multi-speaker
accuracy require separate qualification.

| Location | Responsibility |
| --- | --- |
| `Ansible` | WinUI pages, floating waveform, shortcuts, text insertion, composition and UI dispatch |
| `Ansible.Core\DictationSession.cs` | Operation lifecycle, settings, cancellation, preloading and shutdown |
| `Ansible.Core\RecognitionContracts.cs`, `SessionState.cs` | Testable boundaries and immutable UI snapshots |
| `Ansible.Core\NativeAudioEngine.cs`, `NativeAudioApi.cs` | Native model/session ownership and C ABI |
| `Ansible.Core\Windows` | Microphone capture and retained-resource teardown |
| `Ansible.Probe` | Real WAV and paced streaming recognition outside the UI |
| `Ansible.DeviceProbe` | Isolated CPU/Vulkan qualification worker, packaged under `DeviceProbe\` |
| `Ansible.Tests` | Deterministic contract tests and tagged native integration tests |
| `tools\audio-models.json` | Pinned model IDs, tasks, capabilities, downloads, checksums and licenses |
| `tools` | Setup, native build and package verification |

`App` supplies installation resources and per-user storage through `AppPaths`.
Pages share a session; `UiSessionObserver` dispatches versioned snapshots.
Capture, downloads and inference run off the UI thread. The session owns each
operation and rejects overlapping work or unsafe settings changes.

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
exit. Failed native teardown retains the resource owners and blocks new work;
it must not turn into a false idle state. Native cancellation waits for the
current blocking kernel before release. Keep capture queues and memory
admission bounded.

Meeting naming uses the selected Teams process's cached UI Automation tree on
a dedicated worker. Tests cover marker parsing and conservative matching; a
visible live call is still required to qualify real Teams layouts. Optional
Nemotron timing runs on CPU through the existing C ABI; deterministic alignment
tests do not prove live ASR accuracy. Keep its 30-second audio ring in memory.
Copilot cleanup uses JSONL process output, validates a complete text-only result,
and isolates CLI home/cache/log/work directories. Test it with synthetic text;
never use personal meeting text in diagnostics or print authentication values.

Automatic Teams detection enumerates active WASAPI capture sessions without
reading audio. It resolves capture owners through a bounded process ancestry
snapshot, checking creation-time order before identifying a Teams root. It
identifies Teams by process ID and start time, debounces a
candidate for three seconds, and rearms dismissed identities after ten seconds
of observed absence. Failed or disabled scans are not evidence of absence.
The UI polls off-thread, suppresses suggestions during owned speech operations,
and joins the monitor before shutdown. Detection opens a configured notetaker;
the user still starts capture. Test policy separately from the tagged native
enumeration smoke test; real Teams join/mute behavior requires live qualification.
Microsoft documents that session enumeration can race with newly created
sessions; this polling detector is best-effort. See
[GetSessionEnumerator](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessionmanager2-getsessionenumerator).

Preserve NativeAOT compatibility: use `LibraryImport` and source-generated
JSON, with no reflection-dependent serialization or runtime code generation.
When adding models, update catalog capabilities, compiled families, affected
callers and tests together. Keep speech local, and preserve runtime and model
license notices when redistributing them. See [AGENTS.md](../AGENTS.md) for the
full engineering constraints.
