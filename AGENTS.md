# Project context

## Product direction

This repository builds a Windows dictation and transcription app, not Ansible automation.
The intended experience is a WisprFlow-style workflow with a native Windows interface.
Use WinUI 3 and .NET NativeAOT for fast startup and efficient managed execution.
Use [audio.cpp](https://github.com/0xShug0/audio.cpp) as the local native inference backend.
Support multiple speech-recognition models through that backend rather than coupling the app to one model.

The current application is an integration proof of concept, not a complete production dictation product.
Treat the product direction as the goal, not a claim that every workflow already exists.

## Rapid prototype development

This project is in rapid prototype development, with no default backward-compatibility requirement.
Existing code is exploratory work, not evidence of intentional architecture or a contract to preserve.
The code map below describes the current layout, not required module boundaries.

- Breaking changes and substantial refactors are welcome when they improve the product or simplify the design.
- Replace weak designs directly. Do not add compatibility shims, deprecated wrappers, or parallel legacy paths to protect prototype code.
- Change APIs, model IDs, data formats, and module boundaries as needed. Require compatibility only when the user explicitly requests it.
- Update affected callers, tests, and documentation together. Remove obsolete code rather than maintaining both implementations.
- Keep the product goals and correctness requirements below. Prototype freedom does not justify unsafe resource handling or fabricated recognition results.

## Current implementation

- The app targets .NET 10 and uses single-project MSIX with self-contained .NET NativeAOT and Windows App SDK deployment.
- The build scripts target Windows x64 with CPU and Vulkan inference. Qualify each model/GPU combination separately; ARM64 and NPU support remain separate work.
- Recognition runs in-process through the audio.cpp C ABI, using `audiocpp.dll`.
- The native build pins audio.cpp v0.9.0. Upstream support does not imply support in this compiled backend.
- The UI includes Dictation, Settings, Insights, and a floating dictation preview.
- Microphone capture uses Windows `waveIn` with 16 kHz mono PCM16 and bounded audio queues.
- The session preloads the selected streaming model and a reusable native session, unloads them after 10 idle minutes, and opens the microphone before the model is ready so early speech is buffered, not lost.
- Transcripts and captured audio stay in memory. Local Insights persist session metadata, not transcript text or recordings.
- Shortcut insertion pastes through the clipboard by default, backing up and restoring every format and excluding dictated text from clipboard history and cloud sync. Paced Unicode typing is the alternative; burst typing garbles slow targets such as Windows 11 Notepad.

Read `README.md` for detailed setup, model limitations, and native probe instructions.
Use the current source to determine implemented UI behavior.

## Code map

- `Ansible\` contains the WinUI app, XAML views, floating window, and Windows microphone integration.
- `Ansible.Core\DictationSession.cs` owns the shared operation lifecycle, including startup, recognition, preferences, cancellation and shutdown.
- `Ansible.Core\RecognitionContracts.cs` defines testable engine, audio and storage boundaries. `SessionState.cs` defines immutable UI snapshots and terminal outcomes.
- `Ansible.Core\` contains native interop, verified model admission, shared audio input parsing, and per-user usage storage. `SpeechExtraction.cs` maps each native family's raw output to display text and insertable speech text.
- `Ansible.Core\Windows\` owns Windows capture bindings and retained-resource teardown. `WaveInCaptureFactory.cs` composes the native capture adapter.
- `Ansible\AppPaths.cs` supplies installation resources and per-user data paths. `UiSessionObserver.cs` dispatches versioned snapshots to the UI.
- `Ansible.Core\NativeAudioEngine.cs` owns model sessions and recognition. `NativeAudioApi.cs` defines the C ABI bindings.
- `Ansible.Probe\` exercises actual native WAV and streaming recognition independently of the UI.
- `Ansible.Tests\` contains xUnit tests for core behavior.
- `tools\` contains model setup, native build, and NativeAOT publish scripts.
- `tools\audio-models.json` owns model IDs, capabilities, pinned downloads, checksums, and licenses.
- `.runtime\` holds downloaded models, upstream source, native tools, and generated binaries. Keep it out of Git.

## Development commands

Run commands from the repository root in PowerShell.
Native publishing requires Windows x64, the .NET 10 SDK, and Visual Studio's Desktop development with C++ workload.

Run the core tests when changing core behavior:

```powershell
dotnet test .\Ansible.Tests\Ansible.Tests.csproj
```

Prepare the runtime when the native DLL or models are missing:

```powershell
.\tools\Setup-AudioBackend.ps1
.\tools\Build-AudioNative.ps1
```

Default setup installs only `moonshine-tiny`.
Use `-Models <model-id>` for another catalog entry.
Do not install every model for routine checks; `-Models all` downloads large weights.

Build and verify an unsigned NativeAOT MSIX after changes to the WinUI app, native interop, or deployment:

```powershell
.\tools\Publish-Ansible.ps1
```

The package uses a development identity until the project is associated with the Microsoft Store.
Use the packaged Visual Studio launch profile for development; signing, certificate trust, installation, and Store submission are separate operations.

Use the native probe instructions in `README.md` when changing model integration or streaming behavior.
Run model probes sequentially and wait for each process to exit.
Documentation-only edits do not require model downloads or a native rebuild.

## Engineering constraints

- Keep NativeAOT compatibility. Use source-generated JSON and `LibraryImport`; avoid reflection-dependent serialization and runtime code generation.
- Ensure NativeAOT publishing includes required WinUI `.pri` and `.xbf` resources. A successful managed build does not prove publish correctness.
- Keep the native C ABI integration as the default architecture. Do not introduce an HTTP server or Python runtime for recognition.
- Keep capture and inference off the UI thread. Dispatch UI updates and make session ownership explicit.
- Intercept the final window close before shutdown. Join every owned operation; unresolved native resources must remain owned and block new work.
- Support cancellation, release native handles, and bound audio queues. Surface failures instead of dropping samples or silently changing models.
- Respect each model's offline, streaming, language, and preview capabilities. Streaming input does not guarantee incremental recognition.
- Never simulate transcripts or usage statistics. Distinguish installed weights, successful loading, successful recognition, and measured live performance.
- When adding models, update the pinned catalog, compiled native families, capability handling, and relevant tests together.
- Pin upstream revisions and verify download lengths and SHA-256 checksums. Include upstream notices; model licenses are separate from audio.cpp's license.
- Bound inference by available memory. Do not close unrelated applications or disable memory safeguards to force model loading.
- Keep speech local. Do not add audio uploads, transcript persistence, or external telemetry without an explicit product decision.
- Count speech content, not presentation labels. Unknown speech counts remain unknown rather than becoming zero.
- Keep family-specific output cleanup in `Ansible.Core\SpeechExtraction.cs`. Insertion and usage code consume only `RecognitionResult.SpeechText` and must never inspect raw model output.
- Measure startup, recognition latency, and memory before making performance claims. NativeAOT does not remove native model costs.

## Version control

Create local checkpoint commits at coherent, verified milestones during substantial work.
Commit task-related changes only; preserve unrelated user edits. Do not push unless requested.
