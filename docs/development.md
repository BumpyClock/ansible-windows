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

Setup downloads and verifies only `moonshine-tiny` by default. Choose another
catalog entry with `-Models <model-id>`. Setup does not build the DLL.
The native build downloads pinned CMake and Ninja tools, checks out audio.cpp
v0.9.0, applies the tracked Vulkan loader patch, and builds CPU plus Vulkan
support. It stages `audiocpp.dll`, its app-local dependencies and notices in
`.runtime\native`, and the public WAV in `.runtime\validation`.

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

## Build and verify a package

```powershell
.\tools\Publish-Ansible.ps1
```

Publishing stages an existing native runtime; it does not compile one.
The script builds an unsigned, self-contained NativeAOT MSIX and prints its
verified path under `Ansible\bin\AppPackages`. Change the output directory with:

```powershell
.\tools\Publish-Ansible.ps1 -OutputDirectory .\.runtime\packages
```

Verification inspects the actual package for a native x64 executable, required
WinUI XAML embedded in `resources.pri`, resources, native dependencies, and
notices. A managed build alone does not verify this deployment. Publishing
does not install or sign the package. Use the packaged launch profile for
development; the unsigned artifact is not ready for direct installation.

## Tests

Run the deterministic tests without native model dependencies:

```powershell
dotnet test .\Ansible.Tests\Ansible.Tests.csproj --filter 'Category!=NativeIntegration'
```

An unfiltered test run also executes native integration tests. Those require
the built DLL, public sample, and installed model weights. The mandatory
integration tests need both Moonshine and Nemotron:

```powershell
.\tools\Setup-AudioBackend.ps1 -Models moonshine-tiny,nemotron-asr-0.6b-q8
dotnet test .\Ansible.Tests\Ansible.Tests.csproj --filter 'FullyQualifiedName~NativeEngineTests&Category=NativeIntegration'
dotnet test .\Ansible.Tests\Ansible.Tests.csproj --filter 'FullyQualifiedName~NativeStreamingDeliveryTests'
```

Wait for each command to finish before starting the next. These tests use the
public sample and do not open a microphone. `NativeStreamingDeliveryTests`
accepts `DICTATION_MODELS_DIRECTORY` for an existing model folder;
`NativeEngineTests` reads `.runtime\models`.

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

| Location | Responsibility |
| --- | --- |
| `Ansible` | WinUI pages, floating waveform, shortcuts, text insertion, composition and UI dispatch |
| `Ansible.Core\DictationSession.cs` | Operation lifecycle, settings, cancellation, preloading and shutdown |
| `Ansible.Core\RecognitionContracts.cs`, `SessionState.cs` | Testable boundaries and immutable UI snapshots |
| `Ansible.Core\NativeAudioEngine.cs`, `NativeAudioApi.cs` | Native model/session ownership and C ABI |
| `Ansible.Core\Windows` | Microphone capture and retained-resource teardown |
| `Ansible.Probe` | Real WAV and paced streaming recognition outside the UI |
| `Ansible.Tests` | Deterministic contract tests and tagged native integration tests |
| `tools\audio-models.json` | Pinned model IDs, capabilities, downloads, checksums and licenses |
| `tools` | Setup, native build and package verification |

`App` supplies installation resources and per-user storage through `AppPaths`.
Pages share a session; `UiSessionObserver` dispatches versioned snapshots.
Capture, downloads and inference run off the UI thread. The session owns each
operation and rejects overlapping work or unsafe settings changes.

Closing the main window rejects new work and joins owned operations before
exit. Failed native teardown retains the resource owners and blocks new work;
it must not turn into a false idle state. Native cancellation waits for the
current blocking kernel before release. Keep capture queues and memory
admission bounded.

Preserve NativeAOT compatibility: use `LibraryImport` and source-generated
JSON, with no reflection-dependent serialization or runtime code generation.
When adding models, update catalog capabilities, compiled families, affected
callers and tests together. Keep speech local, and preserve runtime and model
license notices when redistributing them. See [AGENTS.md](../AGENTS.md) for the
full engineering constraints.
