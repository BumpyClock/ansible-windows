# Ansible

Ansible is a local Windows dictation and WAV transcription prototype. It uses
WinUI 3, .NET 10, and [audio.cpp](https://github.com/0xShug0/audio.cpp) through an
in-process native DLL. Release MSIX packages use .NET NativeAOT.

Focus a text field, hold F9, speak, and release the key. Ansible inserts the final
speech after recognition succeeds. The app also provides manual transcription,
model downloads, custom vocabulary for supported models, and local usage insights.

Recognition runs on the device. The app does not save recordings or transcripts
or upload them. Text you copy or insert is subject to the clipboard and destination
app's storage policies. Model downloads require network access.

## Build and run

Run these commands in PowerShell from the repository root. You need:

- Windows x64 with AVX2, FMA, F16C, BMI1/BMI2, and OS-enabled AVX state.
- The .NET 10 SDK.
- Visual Studio with Desktop development with C++ and single-project MSIX tools.
- Git and `curl.exe` for source and model downloads.
- The Vulkan SDK, including `glslc`, for the default CPU/Vulkan native build.
  A CPU-only build does not need it.

```powershell
.\tools\Setup-AudioBackend.ps1
.\tools\Build-AudioNative.ps1
```

Setup downloads and verifies `moonshine-tiny` into `.runtime\models`. The native
build produces the DLL, dependencies, notices, and public validation sample.
For CPU only, replace the second command with:

```powershell
.\tools\Build-AudioNative.ps1 -Backend cpu
```

Open `Ansible.slnx` in Visual Studio. Set `Ansible` as the startup project, select
Debug/x64 and **Ansible (Packaged)**, then run it. Packaged development deployment
requires Windows Developer Mode.

In **Speech models > Model folder > Choose folder**, choose this repository's
`.runtime\models` to use the weights downloaded above. A fresh app otherwise uses
its own model folder; you can download models there from the app.

To create and verify an unsigned release package:

```powershell
.\tools\Publish-Ansible.ps1
```

The MSIX is written under `Ansible\bin\AppPackages`. This command builds and
verifies the package; it does not install it. Sideloading requires signing and
certificate trust. See the [development guide](docs/development.md) for build
options and checks.

## Current limits

- Windows x64 only. ARM64 and NPU support are separate work.
- File transcription accepts WAV only, up to five minutes, 100 MiB on disk,
  and 64 MiB decoded audio.
  App recognition operations have a five-minute timeout.
- Microphone input does not imply live text. Moonshine returns final text;
  Qwen buffers input. Shortcut insertion always waits for the final result.
- CPU is the default recognition processor. GPU support depends on the model
  and driver. Nemotron GPU streaming has a reported native crash; use CPU for
  Nemotron dictation. See [native validation](docs/native-validation.md#known-limits).
- Both VibeVoice models support shortcut dictation with speaker labels removed.
  The offline 7B model buffers microphone audio and recognizes it after Finish;
  it needs substantial free memory.
- Insertion supports regular desktop text fields. Elevated apps and the secure
  desktop are unsupported. Check the destination after insertion.

## Documentation

- [Use Ansible](docs/user-guide.md): dictation, models, settings, privacy, and recovery.
- [Develop Ansible](docs/development.md): toolchain, builds, tests, and source ownership.
- [Validate native recognition](docs/native-validation.md): probes, model capabilities,
  and hardware limits.

The [model catalog](tools/audio-models.json) pins downloads, hashes, capabilities,
and licenses. audio.cpp uses Apache-2.0; model licenses are separate. Preserve
runtime and model notices when redistributing. Model artwork attribution is in
[NOTICE.txt](Ansible/Assets/ModelIcons/NOTICE.txt).
