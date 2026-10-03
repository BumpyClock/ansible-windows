# Local Dictation POC

## Desktop experience

The native WinUI shell includes **Insights**, **Dictation**, **Speech models**, and
**Settings + verification**. Its light neutral surfaces and teal accents follow
the supplied visual direction without copying another product's branding.

The main window uses the native Mica system backdrop. The main content container
uses WinUI's in-app acrylic resource. The transient preview clips a native host
backdrop and tint to a rounded waveform pill, without a rectangular window frame.
High Contrast and disabled system effects use solid semantic colors instead.
Semantic color and chart tokens have Light, Dark, and HighContrast definitions.
The app follows the system theme instead of forcing Light. Dynamic navigation,
model bars, activity cells, and preview glyphs use theme-resource styles. The native
waveform surface updates its colors when the system theme changes.

Insights uses completed local sessions only. It shows recognized words, recorded
dictation pace, model usage, and a daily activity calendar. It does not invent
correction counts, accuracy scores, rankings, or leaderboard data. Pace uses
microphone recording time, including pauses; file verification is excluded.

During dictation, a topmost, non-activating waveform pill shows audio energy
history from actual PCM samples. For models with incremental recognition, the
latest six words float above and outside the pill. New words reveal left-to-right,
with transparent fades at the text edges. Existing words keep visual continuity
instead of restarting the full line's entrance animation. Final-only models do not
simulate streaming text.
The pill contains no command buttons, timer, model label, or status heading;
Finish and Cancel remain in the main window. Reduced-motion mode disables text
animation and shows a level meter instead of scrolling history. High Contrast
disables the edge fades.
Successful previews hide after four seconds. After warnings or errors, the preview
remains until dismissed by tapping or right-clicking the idle pill.
The preview's accessible description distinguishes WAV replay from microphone capture.
Native desktop composition renders the waveform. A separate layered window
renders per-pixel glyph alpha above it; WinUI shapes the text in a hidden source
window. Glyph pixels and transcript content remain in memory. Window shutdown
joins the preview's owned composition queue before closing the main window.

**Verify public sample** in Settings replays the bundled, public validation WAV
through streaming models or transcribes it through offline models. This exercises
the native backend and floating panel without recording ambient microphone audio.

Statistics persist per user in the package's `LocalState\usage.json`.
Only dates, model IDs, authoritative speech-word counts, session type, and
durations are stored. Speaker annotations are not speech content. If native
speech content cannot be established, its count is unknown and excluded from
word totals and pace, not silently replaced with zero. Audio and transcript
content are never persisted. Disable collection without deleting prior counts.
Failed and cancelled sessions are not counted. Existing prototype statistics
beside older executables remain untouched; the foundation does not dual-write
or silently import that development data.

Native inference runs asynchronously, but a blocking native kernel cannot be
forcibly interrupted. Cancel stops capture and waits for the current native step
before releasing state. The microphone backlog is bounded to approximately
9.6 MB, allowing slower CPU models to finish short utterances without dropping
samples. The overall operation limit remains five minutes.

A native WinUI 3 application published with .NET NativeAOT. Speech recognition
runs in-process through the audio.cpp C ABI DLL. This is an x64 integration
proof, not the production dictation application.

## Included

- Default microphone capture, final transcription, and live preview for models
  that emit partial text during capture.
- WAV recording transcription, including streamed decoding where the model supports it.
- Model selection, downloads, and management from the pinned ASR catalog.
- Cancellation, microphone level, elapsed time, and explicit connection/inference errors.
- Final-text copying. Transcript and microphone audio remain in memory.
- Direct native inference. No HTTP service, listener, or audio upload is required.

The UI uses native controls and the Windows theme. The floating preview adds only
a brief word-arrival animation, respecting the system's reduced-motion setting.
Microphone capture uses the Windows `waveIn` API with source-generated P/Invoke,
16 kHz mono PCM16, and a bounded queue. The POC stops with an error rather than
dropping audio when the consumer cannot keep up.

## Run

Requirements: Windows x64, .NET 10 SDK, and Visual Studio 2026 with the Desktop
development with C++ workload and single-project MSIX tools. The app is packaged
as MSIX and includes its NativeAOT executable, Windows App SDK runtime, and native
inference dependencies. Users do not need to install .NET separately.

From this folder:

```powershell
.\tools\Setup-AudioBackend.ps1
.\tools\Build-AudioNative.ps1
```

The setup script installs models only. The native build produces
`.runtime\native\audiocpp.dll`, stages its required app-local MSVC/OpenMP
dependencies, and records their import closure and redistributable notices.
Then build and verify an unsigned NativeAOT package:

```powershell
.\tools\Publish-Poc.ps1
```

The default output is
`DictationPoc\bin\AppPackages\DictationPoc_1.0.0.0_x64_Test\DictationPoc_1.0.0.0_x64.msix`.
`-OutputDirectory` changes the parent package directory. Packaging does not
install the app, create certificates, change certificate trust, or enable
Developer Mode. It never bundles downloaded models or development model paths.

For development, open `DictationPoc.slnx` in Visual Studio, set `DictationPoc`
as the startup project, select x64 and the **DictationPoc (Packaged)** launch
profile, and run it. Packaged development deployment requires Windows Developer
Mode. Enable that setting yourself if needed. Normal builds and F5 deployment
use managed code for debugging; creating a Release MSIX compiles NativeAOT.
Do not run the executable from its build folder as an unpackaged app.

To sideload the generated MSIX, sign it with a code-signing certificate whose
subject matches `Package.appxmanifest`'s `Identity.Publisher`, and trust that
certificate on the test machine. Signing and trust are separate, explicit steps.
The default unsigned MSIX cannot be installed by double-clicking it.

Select **Start dictation**, speak, then select **Finish dictation**.
The first recognition can take longer while model
graphs are prepared. **Cancel** aborts the operation and retains visible partial
text. **Copy text** copies the completed result or retained text after an error.
No clipboard contents are read.

Leave **Language hint** blank for Moonshine, which is English-only. Other model
families can accept a language hint, but the option is model-dependent. Unsupported
options surface as native engine errors.

The bundled audio.cpp v0.9.0 Moonshine adapter buffers audio and recognizes it
after **Finish dictation**. Its `streaming` mode describes input ingestion, not
incremental recognition. The native streaming API supports partial text, but a
cache-aware model such as Nemotron or VibeVoice streaming is needed to exercise
that behavior. The POC does not simulate a live transcript.

The default setup downloads 60,408,448 bytes of model weights and verifies
exact lengths and SHA-256 checksums:

- Moonshine Streaming Tiny Q8 from
  [audio-cpp/audio.cpp-gguf](https://huggingface.co/audio-cpp/audio.cpp-gguf),
  pinned to revision `351dbab8d8534675ee29440bb402e348b09e55e2`.

Models and native build outputs live in `.runtime`.
Interrupted downloads remain as `.partial` files and resume on the next setup
run. Only complete, verified files become installed models.
The setup script does not retrieve server packages or overwrite native binaries.
The native engine loads models on demand. A new packaged installation has no
weights until you download a model or choose an existing model folder.

## Microsoft Store preparation

`DictationPoc\Package.appxmanifest` currently uses the development identity
`BumpyClock.LocalVoice.Development` with publisher `CN=BumpyClock`. This is not
a reserved Store identity.

When ready, use Visual Studio's **Package & Publish > Associate App with the
Store** on `DictationPoc`. Association supplies the exact package name, publisher,
and publisher display name from Partner Center. Then use **Create App Packages**
for Microsoft Store distribution in Release/x64 and upload the resulting Store
package. Keep the fourth version component zero for Store submissions and
increase the version for updates. The command-line publishing script intentionally
builds a local unsigned MSIX, not a Store submission.

The package declares `runFullTrust` for the desktop/native application,
`microphone` for capture, and `internetClient` for user-requested model downloads.
Explain the full-trust requirement in the Store submission. Prepare the listing,
privacy policy, screenshots, license disclosures, and Windows App Certification
Kit results before submission. The current native backend requires
AVX2/FMA/F16C/BMI1/BMI2; document that CPU requirement and qualify clean-machine
installation and recognition separately. Packaging alone does not establish
Store certification or production readiness.

See Microsoft's [single-project MSIX guide](https://learn.microsoft.com/windows/apps/windows-app-sdk/single-project-msix),
[product identity requirements](https://learn.microsoft.com/windows/apps/publish/view-app-identity-details),
and [MSIX signing guide](https://learn.microsoft.com/windows/msix/package/signing-package-overview).

## Model catalog and controls

Open **Speech models** to choose weights without a command-line setup step.
An app with no installed models opens this page and remains disconnected until
weights are installed. The catalog comes from `tools\audio-models.json`; the
packaged copy is the same catalog, not a second model registry. The page shows
compiled-backend support, languages, precision, input mode, preview behavior,
license notes, pinned source revision, exact size, and SHA-256.

Select **Download** explicitly. The app does not automatically retrieve large
weights. Transfers and hashing run outside recognition ownership, so dictation
can continue with an existing model during a download. Progress shows transferred
bytes and the measured transfer rate. **Pause** retains the partial file and its
resume metadata. **Resume** and **Retry download** use validated byte ranges and
strong ETags when provided. HTTP responses and reads each have a 30-second
deadline; model transfers do not inherit the five-minute recognition deadline.
The app rejects mismatched ranges, validators, encodings, lengths, and hashes
rather than restarting or overwriting weights silently.

Only an exact-length, SHA-256-verified `.partial` can reach **ReadyToInstall**.
A read lease keeps these staged weights unchanged. Installation promotes the
file atomically and refreshes the native inventory while recognition is idle.
If a recording is active when a transfer completes, the model waits for
**Install verified model**; the app does not cancel the recording. Partial
files are never native model inventory entries. Downloads require free disk
space for the remaining weights plus 64 MiB of reserve. Errors remain visible
and no alternate model is selected silently.

**Choose folder** saves the selected directory in the package's
`LocalState\model-settings.json`. A fresh installation defaults to
`LocalState\models`. `LocalState` is Windows' per-user package data folder,
normally `%LOCALAPPDATA%\Packages\<package-family-name>\LocalState`.
Package updates preserve this data; uninstalling the package removes its local
data, including models in the default folder. Changing folders does not move
or delete existing weights. Model folders explicitly chosen outside package
storage remain outside that uninstall lifecycle.
Older unpackaged `%LOCALAPPDATA%\LocalVoice` data and `.runtime\models` remain
untouched. Choose `.runtime\models` explicitly to reuse development weights.
**Verify installed files** checks exact lengths and hashes without downloading.
**Remove file** and **Discard partial download** each require confirmation.
Removal releases the native resident model first; installation and removal are
rejected while recognition owns the session. Discarding a partial file does not
remove a complete installed model. Removing the last model returns the session
to Disconnected with no stale model selection. App close cancels and joins
transfers alongside native shutdown. Only model files are transferred over the
network, never audio, transcripts, or analytics.

`tools\audio-models.json` pins each public repository revision, filename, exact
byte length, SHA-256, license, and model ID. Available IDs:

- `moonshine-tiny`: Moonshine Streaming Tiny Q8, 60,408,448 bytes, MIT.
  Streaming input, final-only recognition, English only.
- `vibevoice-streaming-1.5b-q4`: VibeVoice ASR Streaming 1.5B Q4_K,
  2,117,525,248 bytes, MIT. The adapter supports incremental recognition.
- `vibevoice-asr-7b-q8`: original offline VibeVoice ASR 7B Q8,
  9,858,644,224 bytes, MIT. WAV transcription only; no microphone mode.
- `qwen3-asr-0.6b-q8`: Qwen3-ASR 0.6B Q8, 1,151,272,416 bytes,
  Apache-2.0. The streaming adapter buffers input before recognition.
- `nemotron-asr-0.6b-q8`: Nemotron 3.5 ASR Streaming 0.6B Q8,
  930,625,888 bytes, OpenMDW-1.1. The adapter supports incremental recognition.

The command-line setup remains available. To explicitly install all five models:

```powershell
.\tools\Setup-AudioBackend.ps1 -Models all
```

The four additional downloads total 14,058,067,776 bytes, approximately 14.06 GB
or 13.09 GiB. Downloads do not require an inference process to run.
Reopen the app or probe after installing new models so the native engine reads
the updated installed catalog. No service restart or model registration is needed.

Native initialization discovers installed catalog entries. Before loading a
model, admission verifies the pinned SHA-256 off the UI thread. Verification is
cached only while Windows file identity and metadata are unchanged; a resident
read lease prevents modification or replacement. Merely finding a file or
matching its length does not establish verified model identity.
`-Models` on setup installs a subset without removing other files. Select the
exact model ID in the app or probe. Failed requests do not select another model.

### CPU and memory limits

The qualified native x64 build requires AVX2, FMA, F16C, BMI1/BMI2, and OS-enabled
AVX state. Initialization checks eligibility before reaching native kernels.
Other architectures and CPU profiles require their own build and qualification.

The native harness uses CPU inference, four threads, and 512 MB of memory
headroom. Models load lazily; keep at most one model resident.
Keep the memory guard enabled. Being listed in the installed catalog does not
prove that a model can load or recognize speech.

The 7B weights alone occupy approximately 9.18 GiB on disk. Runtime graphs,
audio buffers, and decoding need additional memory. A machine with 32 GB total
RAM but only approximately 3.3 GiB available cannot be assumed to run this model.
Do not disable the guard or close unrelated applications to force a load.
The 1.5B model can also fail under memory pressure.
In the native validation run, the 7B load guard estimates 11.3 GiB including
headroom, while Windows reported 4.1 GiB available. The request was rejected
before loading. The model remains installed but its recognition is not validated.

The probe sets `threads: 4`. Cold loads and recognition can be slow,
especially for VibeVoice. Model switching unloads the previous model and can
repeat preparation costs. Adapter support for partial text does not guarantee a
preview before a short recording ends. Memory errors, unsupported operations,
and operation timeouts surface directly; model availability is not a CPU
real-time guarantee.

One `AudioInputReader` owns RIFF validation for offline recognition, live replay,
and the probe. It rejects malformed or duplicate mandatory chunks, unsupported
formats, non-finite samples, recordings longer than five minutes, files larger
than 100 MiB, and decoded float payloads larger than 64 MiB. Reading and chunked
decoding are cancellable and run off the UI thread.

WAV recognition receives bounded decoded audio. Live recognition feeds PCM
chunks through an operation-owned bounded channel. Memory admission includes
decoded input, native copying/resampling, model/workspace estimates, and reserve
headroom. This remains a conservative admission estimate, not a measured
guarantee against every native allocator peak.
Successful WAV recognition does not qualify a model's native streaming path.

### Observed native model behavior

Measured with the public `sample_16k.wav`, four CPU threads, blank language,
and 512 MB headroom on 2026-10-02. Models run sequentially in separate processes.
Streaming times include preparation, paced audio upload, and final decoding:

- `nemotron-asr-0.6b-q8`: WAV 3.778 seconds, live 18.308 seconds,
  25 previews during upload.
- `qwen3-asr-0.6b-q8`: WAV 8.227 seconds, live 22.169 seconds,
  zero previews during upload. Buffered recognition is not a live preview.
- `vibevoice-streaming-1.5b-q4`: WAV 33.582 seconds, live 41.364 seconds,
  four previews during upload. Output includes a `Speaker 0:` label.
- `moonshine-tiny`: earlier native baseline WAV 1.083 seconds,
  live 18.051 seconds, zero previews during upload.
- `vibevoice-asr-7b-q8`: installed and verified; the memory guard rejects the
  measured load. Recognition remains unvalidated.

The three revalidated streaming models return the sample's expected opening,
“Some call me nature”, and the duration/count phrases. File and live results
differ in punctuation and number formatting. This is a short integration check,
not an accuracy benchmark or a guarantee of real-time CPU performance.
The earlier missing-audio-contract streaming failure is resolved in the native
wrapper. No HTTP fallback is used.

## Reproduce the integration checks

```powershell
dotnet test .\DictationPoc.Tests\DictationPoc.Tests.csproj
New-Item -ItemType Directory -Force .\.runtime\validation | Out-Null
Invoke-WebRequest `
  https://raw.githubusercontent.com/0xShug0/audio.cpp/v0.9.0/assets/resources/sample_16k.wav `
  -OutFile .\.runtime\validation\sample_16k.wav
dotnet run --project .\DictationPoc.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav
```

The probe exercises real WAV recognition and then feeds public sample audio at
microphone cadence. It reports how many previews arrive before input ends.
To test a specific model without silent fallback:

```powershell
dotnet run --project .\DictationPoc.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav `
  qwen3-asr-0.6b-q8
dotnet run --project .\DictationPoc.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav `
  vibevoice-asr-7b-q8 --file-only
```

The probe uses a blank language hint and a separate three-minute limit for each
recognition operation. It reports the attempted model, stage, elapsed time, and
failure message. `--file-only` skips live recognition; offline models always do.
Run model probes sequentially in separate processes. Cancellation prevents
additional preparation work and waits for a currently executing native call;
it cannot force-abort that call. Allow the probe to exit before another run.

## Foundation ownership

`App` composes paths and concrete dependencies explicitly. Pages receive the
shared session; they do not retrieve services through a global application
locator. The composition root supplies installation resource paths and per-user
storage through `ApplicationData.Current.LocalFolder`. Packages default to the
user's package-local model folder and contain no `runtime-settings.json` or
machine-specific model paths. The app never searches ancestor directories for
production resources.

`DictationSession` owns one current operation: its cancellation, capture,
producer, inference, identity, and outcome. Startup, reconnection and preference
writes are also tracked work. Immutable versioned snapshots feed the UI through
one dispatcher adapter. Late callbacks cannot restore state or open capture
after cancellation/closing.

The main window intercepts the close request, rejects new work, and joins owned
work before allowing the last window to close. Failed capture teardown retains
native headers, device and callback roots. Unreleased resources block new work;
a close retry attempts recovery instead of pretending the app is idle.

The native engine validates the ABI before publishing a module, checks
cancellation between preparation stages, and resets or invalidates operation
state before reuse. Model weights may stay resident, but a completed operation
does not retain its transcript or decoding state.

Deterministic engine/capture/input/store doubles cover lifecycle and failure
contracts without loading model weights. The separately tagged native
integration test uses the real DLL and public sample.

```powershell
dotnet test .\DictationPoc.Tests\DictationPoc.Tests.csproj --filter Category!=NativeIntegration
dotnet test .\DictationPoc.Tests\DictationPoc.Tests.csproj --filter Category=NativeIntegration
.\tools\Publish-Poc.ps1 -OutputDirectory .\.runtime\packages
```

Publishing inspects the actual MSIX for its identity, native x64 executable,
native PE import closure, notices, and compiled XAML embedded in `resources.pri`.
Clean-machine Windows qualification and physical microphone-driver
fault behavior still require their respective environments; a build is not
evidence that those external conditions passed.

## Deliberate boundaries

- ARM64 backend qualification remains separate. Only x64 is validated here.
- No global hotkey, automatic target-app insertion, or LLM cleanup.
- A floating live-preview window is included.
- WAV files only, within the duration and decoded-memory limits above. Operations time out after five minutes.
- No audio/history persistence, cloud transcription, telemetry, or model auto-downloads inside the UI.
- Model weights are downloaded separately; runtime binaries ship inside the MSIX.

audio.cpp is Apache-2.0 licensed. Catalog entries record each model's license.
Model licenses are independent of the runtime. Preserve the upstream notices
when redistributing runtime packages or models.
