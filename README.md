# Ansible

Ansible is a local Windows dictation and WAV transcription prototype. It uses
WinUI 3, .NET 10, and [audio.cpp](https://github.com/0xShug0/audio.cpp) through an
in-process native DLL. Release MSIX packages use .NET NativeAOT.

Focus a text field, hold Ctrl + Win (either Windows key), speak, and release.
New installs use that default. A previously saved F9 or other custom shortcut
still works. Ansible opens the microphone as soon as that shortcut is accepted
and keeps early audio in a bounded memory buffer while it checks the destination
field. It inserts the final speech after recognition succeeds. Speech before the
shortcut, and the time the hardware microphone needs to start, can still miss
the first word. The app also provides manual transcription, meeting transcripts,
model downloads, custom vocabulary for supported models, and local usage insights.

Microphone permission is checked at launch. Ansible requests missing consent
before recording and offers Windows Settings guidance when access is blocked.

Enable **Record chord** in Settings to dictate with screen context. Hold
**Alt + Win** to capture the pointer's monitor, draw with the left mouse button,
then release. The result includes numbered ink markers, up to eight pictures,
and a local link to a silent MP4. Enable **Press once to start and stop** under
**Record chord** to release the keys while drawing, then press again to finish.
This setting is independent of voice dictation's activation mode. See the
[record chord workflow](docs/user-guide.md#record-chord).

Closing the main window with X or Alt+F4 hides Ansible to a Windows
notification-area icon. Recognition, global dictation shortcuts, downloads, and
meetings keep running. Click the icon to reopen the window. Right-click it for
**Open Ansible** or **Exit**. **Exit** stops active capture and recognition and
joins owned work. Closing the window never starts recording. If the icon cannot
be created, the window stays visible and Ansible reports an error. Later close
attempts retry tray creation. Windows may put the icon in the hidden-icons
overflow.

Recognition runs on the device. Successful dictation transcripts persist locally
as text and metadata. Meeting transcripts save automatically on this device.
The app never saves or uploads audio. Optional meeting transcript cleanup can
send finalized text to GitHub Copilot CLI or the OpenAI API after you enable it
in **Settings > Transcript cleanup** and a meeting ends successfully. Text you copy or insert is subject to the
clipboard and destination app's storage policies. Model downloads require network access.

Local diagnostics are off by default. You can enable them in Settings to save
bounded operational metadata for recognition, delivery, meetings, capture,
downloads, and storage on this device. They are separate from usage statistics
and saved transcripts. See [local data and diagnostics](docs/user-guide.md#local-data-and-privacy).

## Build and run

Run these commands in PowerShell from the repository root. You need:

- Windows x64 with AVX2, FMA, F16C, BMI1/BMI2, and OS-enabled AVX state.
- The .NET 10 SDK and PowerShell 7 for native build and publish scripts.
- Visual Studio with Desktop development with C++ and single-project MSIX tools.
- Git and `curl.exe` for source and model downloads.
- The Vulkan SDK, including `glslc`, for the default CPU/Vulkan native build.
  A CPU-only build does not need it.

```powershell
.\tools\Setup-AudioBackend.ps1
.\tools\Build-AudioNative.ps1
```

Setup downloads and verifies `nemotron-asr-0.6b-q8` and
`nemotron-3-diarization-q8` into `.runtime\models`. After that install, an
unconfigured dictation session uses Nemotron ASR. A saved model choice is kept.
The diarization companion cannot be selected for dictation, replay, or WAV
transcription. There is no meeting model picker. Start uses an explicitly saved
**Use** selection only when that model is meeting-capable streaming ASR
(Nemotron, Kroko, or VibeVoice streaming). An automatic dictation default is
not a meeting override. Explicit Nemotron or Kroko requires the installed
companion; a missing companion fails Start instead of switching models.
Unconfigured meetings use Nemotron ASR with that companion when both are
installed. If Nemotron ASR is missing, installed Kroko uses the same companion.
If no paired Nemotron or Kroko ASR is available, meetings keep VibeVoice ASR
Streaming 1.5B, including when the companion is absent.

The native build produces the DLL, dependencies, notices, and public validation
sample. It compiles `nemotron_asr` and `nemotron_3_diar` and applies the tracked
Vulkan loader and Nemotron streaming-timestamp patches.

Optional English streaming Kroko weights are available in the app, or through
`tools\Setup-AudioBackend.ps1 -Models kroko-en-64l-q8`. Kroko supports custom
hotwords. GPU support and performance remain unqualified.
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
verifies the package; it does not install it. The default path requires a native
runtime recorded with Vulkan support and rejects a CPU-only artifact. Pass
`-Backend cpu` for an intentional CPU-only package. Sideloading requires signing
and certificate trust.

For an x64-only MSIX bundle, use `tools\Publish-Ansible.ps1 -Bundle`.
A bundle does not require x86 or ARM64 packages. Use
`tools\Publish-Ansible.ps1 -Architecture x64,arm64 -Bundle` to include both.
ARM64 CPU/Vulkan cross-builds are available, but recognition and capture still
need ARM64 hardware qualification.
See the [development guide](docs/development.md#arm64-cross-build) for ARM64
toolchains, commands, and precompiled runtime storage.

## Current limits

- Meetings start manually from **Meetings**. There is no meeting model picker.
  Start snapshots processor, language hint, and vocabulary, and honors an
  explicitly saved streaming **Use** selection when that model can stream
  meetings. An automatic dictation default is not a meeting override. Explicit
  Nemotron or Kroko without the diarization companion fails Start rather than
  switching models. Unconfigured meetings use installed Nemotron ASR plus
  Nemotron 3 Diarization, then installed Kroko with that companion, then
  VibeVoice ASR Streaming 1.5B, including when the companion is absent. Auto
  skips a candidate that measured slower than 0.50x real time on the selected
  device and says so in the Meetings window; speaker labels trail speech by up
  to about five seconds. Ordinary dictation stays plain ASR.
  Automatic Teams detection offers a notetaker prompt when Teams holds an active
  microphone session; it never starts recording. Disable it in Settings.
  Capture includes the default microphone plus endpoint loopback from every
  active render device. A selected Teams process is used only for speaker
  naming. Other app audio on those outputs can be included. Pause/Resume keeps
  one recognition stream; End meeting finishes pending speech. Audio stays in
  bounded memory.
  The capture limit is two hours; long-session speaker accuracy and the 5–10
  second update target need qualification on each model/backend combination.
  Teams names use visible accessibility evidence matched to confident local
  timing anchors; unsupported windows retain editable speaker labels. Optional
  Nemotron CPU timing refinement needs its installed weights and additional memory.
  Transcript cleanup is off until enabled in Settings. Copilot CLI uses the
  installed authenticated CLI account. OpenAI API uses a Credential Locker key
  and OpenAI model IDs. There is no provider fallback. Originals are kept.

- x64 CPU/Vulkan is the default build. ARM64 CPU/Vulkan binaries and NativeAOT packages
  cross-compile, but have not been run on ARM64 hardware. x86 and NPU inference
  are not supported.
- File transcription accepts WAV only, up to five minutes, 100 MiB on disk,
  and 64 MiB decoded audio.
  App recognition operations have a five-minute timeout.
- Microphone input does not imply live text. Moonshine returns final text;
  Qwen buffers input. Shortcut insertion always waits for the final result.
- Auto is the default recognition processor. Settings without a processor field
  load Auto. An explicit saved CPU or GPU choice is kept. It benchmarks CPU,
  then GPU, on a bundled public speech sample, using the median of three warm
  runs per device. GPU wins when it is within five
  percent of CPU. A GPU worker crash or timeout keeps a working CPU; shared
  calibration errors abort without writing a device cache entry. Manual GPU has
  no automatic fallback. Nemotron GPU streaming has a recorded native crash on
  one GPU and driver; Auto qualifies it like other models. Isolated qualification cannot catch a later
  in-process driver crash. See [native validation](docs/native-validation.md#known-limits).
- Both VibeVoice models support shortcut dictation with speaker labels removed.
  The offline 7B model buffers microphone audio and recognizes it after Finish;
  it needs substantial free memory.
- Insertion supports regular desktop text fields. Elevated apps and the secure
  desktop are unsupported. Default paste replaces the clipboard and leaves the
  dictated text available for delayed readers. Use **Type characters** to preserve
  the clipboard or for apps that block paste. Settings offers Ctrl+V, Shift+Insert,
  and Ctrl+Shift+V; choose the chord accepted by the actual destination. Bounded
  UI Automation text checks distinguish verified insertion from unverified input.
  Check the destination after an unverified or partial send.

## Documentation

- [Use Ansible](docs/user-guide.md): dictation, models, settings, privacy, and recovery.
- [Develop Ansible](docs/development.md): toolchain, builds, tests, and source ownership.
- [Validate native recognition](docs/native-validation.md): probes, model capabilities,
  and hardware limits.

The [model catalog](tools/audio-models.json) pins downloads, hashes, tasks,
capabilities, and licenses. audio.cpp uses Apache-2.0; model licenses are separate. Preserve
runtime and model notices when redistributing. Model artwork attribution is in
[NOTICE.txt](Ansible/Assets/ModelIcons/NOTICE.txt).
