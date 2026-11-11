# Validate native recognition

Build the runtime and download the model under test using the
[development guide](development.md). Run commands from the repository root in
PowerShell. Probes use local model weights and a public sample, not the microphone.

Default setup installs `nemotron-asr-0.6b-q8` and `nemotron-3-diarization-q8`.
Moonshine examples below need `moonshine-tiny` installed. The diarization
companion is not a probe ASR model. Probes take an explicit ASR model ID.
App meetings have no model picker; Start may honor an explicitly saved
meeting-capable streaming dictation model, otherwise the unconfigured
Nemotron pair, Kroko pair, then VibeVoice path. Dictation, replay, and WAV
stay plain ASR.

The native build pins audio.cpp v0.9.0, applies tracked patches including
`tools/patches/audio-nemotron-stream-timestamps.patch`, and supports CPU and
optional Vulkan. Compiled families include `nemotron_3_diar`. The patch publishes
cumulative Nemotron token timestamps on stream updates. Paired meetings combine
playback ASR timestamps with the Nemotron 3 diarizer on the same playback stream.
Microphone ASR runs separately and retains local-source identity. Qualification
of the playback pair alone does not qualify the combined two-stream workload.

The [catalog](../tools/audio-models.json) pins each model's revision, file length,
SHA-256, license, task, and capabilities. Use those capabilities rather than assuming
that an upstream model feature exists in this backend. Omitted task means ASR.
Only family `nemotron_3_diar` may be `diar`.

## ARM64 qualification

Cross-publish the probe on the x64 build host with the VS ARM64 toolchain installed:

```powershell
dotnet publish .\Ansible.Probe -c Release -r win-arm64 --self-contained true `
  -p:PublishAot=true -o .\.runtime\arm64-probe
```

On an ARM64 Windows device, copy the published probe directory, the complete
`.runtime\native-arm64` directory, the public sample, catalog, and selected model
weights. Keep the relative layout below, or adjust the paths explicitly.
Run these commands on that device, not the x64 cross-build host:

```powershell
.\.runtime\arm64-probe\Ansible.Probe.exe --cpu-check
.\.runtime\arm64-probe\Ansible.Probe.exe `
  .\.runtime\native-arm64\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav nemotron-asr-0.6b-q8 --backend cpu
```

The second command checks WAV and paced streaming recognition. Successful
cross-compilation is not evidence that either check passes on ARM64.
Follow with packaged microphone, meeting loopback, insertion, and record-chord
checks. Keep model memory safeguards enabled. For an ARM64 CPU/Vulkan runtime,
repeat the recognition command with `--backend vulkan` on a device with a
compatible ARM64 Vulkan driver. CPU success does not qualify GPU recognition.

## Run the probe

This command selects Moonshine explicitly, transcribes the WAV, then feeds the
same audio at microphone cadence:

```powershell
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav moonshine-tiny
```

Use the directory containing your weights instead of `.runtime\models` when
needed. Supply an exact ASR model ID to avoid the probe's default model selection.
A `diar` model ID is rejected; the companion does not transcribe. Run probes
sequentially and wait for each process to exit before starting another.

The probe defaults to CPU, four threads, a blank language hint, and 512 MB of
memory headroom. Each recognition operation has a three-minute timeout. It
reports the requested backend, model, stage, elapsed time, transcript, word count,
and previews received before audio input ended. Unknown word counts remain
unknown. Cancellation waits for an executing native call to finish.

| Option | Effect |
| --- | --- |
| `--file-only` | Skip streaming. Offline models also skip it unless `--stream-only` is specified. |
| `--stream-only` | Skip WAV transcription; prepare once, then stream |
| `--repeat <1..20>` | Repeat the paced stream for warm first-preview and input-end-to-final timings. Missing previews stay missing. Cannot combine with `--file-only`. Use `--stream-only` with `--repeat` for buffered offline dictation. |
| `--backend cpu`, `--backend vulkan`, or `--backend auto` | Select the backend; default CPU. Auto is not implied. |
| `--device <index>` | Select a nonnegative device index; default 0 |
| `--device-probe <executable>` | Required for `--backend auto`. Path to `Ansible.DeviceProbe.exe`. The probe does not discover the worker. |
| `--benchmark-cache <json>` | Optional Auto cache file. Omit to keep the selection in this process only. |
| `--dictionary <text>` | Supply newline-separated recognition hints; unsupported models reject them |
| `--word-timings` | Print native streaming word snapshots, final unchanged-text offsets, and input sample positions. Nemotron uses token emission frames; Kroko word ends can include pauses. See the [timing spike](record-chord-word-timings.md). |

Vulkan example:

```powershell
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav moonshine-tiny --backend vulkan --device 0
```

Auto example. Build the app first so the worker exists. Use the public sample as
the positional WAV; Auto uses that file for isolated qualification. Use the
`Debug` worker folder after a Debug app build. Auto probes CPU, then GPU;
compared times exclude load and warmup. CPU has a 150-second deadline and GPU a
two-minute deadline, so both fit inside the app's five-minute recognition
operation; each includes load, warmup, recognition, and teardown. The CLI also has a
three-minute timeout for each recognition operation, so it can cancel qualification before
a device deadline. A GPU worker crash or
timeout, or GPU device-memory exhaustion (`VK_ERROR_OUT_OF_DEVICE_MEMORY`),
disqualifies GPU only. Shared validation, integrity, host memory, protocol,
or ownership errors abort calibration and do not write a device cache entry:

```powershell
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav moonshine-tiny --backend auto `
  --device-probe .\.runtime\device-probe\x64\Release\Ansible.DeviceProbe.exe `
  --benchmark-cache .\.runtime\device-benchmarks.json
```

Dictionary example, after installing `qwen3-asr-0.6b-q8`:

```powershell
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav `
  qwen3-asr-0.6b-q8 --dictionary "Mother Nature`nUnited States"
```

The probe reports dictionary support and whether context was supplied, without
printing the dictionary. Recognition output is printed to the console.

Warm stream example, after installing `nemotron-asr-0.6b-q8`. This checks
command wiring; it is not a performance result:

```powershell
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav nemotron-asr-0.6b-q8 `
  --backend cpu --stream-only --repeat 5
```

Kroko uses the same command with model ID `kroko-en-64l-q8` and `--backend cpu`.
Its dictionary selects modified beam-search decoding and supplies per-request
hotwords. Entries containing `/` are rejected before native work. Automatic
device qualification distinguishes the hotword decoder from ordinary decoding
and uses public sample hints rather than the user's vocabulary.

On 2026-10-20, the Kroko CPU probe passed WAV recognition and microphone-paced
streaming on the 14.072-second public sample with nonempty hotwords. Both final
results contained "Mother Nature" and reported 28 spoken words. Ten previews
arrived while streaming input was open. This checks recognition functionality;
the run occurred alongside builds and does not qualify performance or GPU support.

To check CPU eligibility without loading a model:

```powershell
dotnet run --project .\Ansible.Probe -- --cpu-check
```

## Interpret results

A successful build, installed file, or GPU listing is not a recognition check.
Verify the pinned weights, load the model, and check actual speech output.
WAV success does not establish streaming success. For models that advertise live
previews, inspect previews received while input is still open. No preview means
no observed live speech, not a zero-millisecond result. Shortcut insertion
still waits for the successful final result.

Record the source revision, model ID and hash, backend, CPU/GPU, driver, available
memory, sample, and command with each result. Separate cold loading from reused
session timing. A public-sample transcript does not establish accuracy across
speakers, languages, microphones, or noise conditions. Paced streaming elapsed
time includes audio delivery and is not pure inference latency.

## Auto qualification

On 2026-10-25, the review-fix CPU build passed the Nemotron paired functional
test and the paced 126.6-second public-sample test. The paced test used four
threads on an AMD Ryzen AI 7 PRO 350. Peak backlog was 14 packets, first speaker
label was at 5.5 seconds, final delay was 1.0 second, and all nine speech anchors
were recognized. These tests include incremental attribution, excluding meeting
storage and WinUI. No Vulkan or multi-hour qualification was performed.

Current qualification warms up once, then validates three measured runs and
uses their median. Streaming measurements include generation of incremental
managed transcript/speaker updates. They still exclude meeting storage, the UI,
model loading and warmup. A short sample is not a whole-app or two-hour
qualification. CPU and GPU workers remain sequential and retain their existing
memory checks and deadlines.

Each engine caches runtime content hashes against opened-file identity. Auto
still checks dependency membership, file changes and hardware/driver identity
before reusing a selection. The main DLL is hashed once. Cache identity version
4 invalidates earlier qualification entries after this measurement change.

On 2026-10-11, Auto qualification on pinned Moonshine Tiny Q8 used the
14.07-second bundled Mother Nature sample, CPU and Vulkan, and four threads.
The native GPU listing was AMD Radeon(TM) 860M Graphics.

Warm sample benchmarks excluded loading and warmup. Offline WAV: CPU 279.3 ms,
Vulkan 153.1 ms, selected Vulkan. Unpaced streaming: CPU 187.3 ms, Vulkan
162.2 ms, selected Vulkan. Actual WAV transcription and paced replay both
returned the same authoritative speech containing "Mother Nature". Moonshine
produced zero incremental previews, which is expected.

A fresh engine run reused the same measurement timestamps and results. The WAV
whole-operation time, including calibration, was 8598 ms cold and 1284 ms with
the cache reused. That is this run only. Paced streaming elapsed time includes
audio feed time and is not pure inference latency. These measurements do not
qualify other models, long live dictation, or meeting history.

## Meeting qualification

Paired Nemotron or Kroko speaker checks use the native DLL, the public sample,
and the selected ASR plus `nemotron-3-diarization-q8`:

```powershell
.\tools\Test-SpeakerModels.ps1 -Model nemotron
.\tools\Test-SpeakerModels.ps1 -Model kroko
.\tools\Test-SpeakerModels.ps1 -Model both
```

The suite requires exactly one passing native test per selected model. Missing
weights, DLL, or sample fail the run; tests are not skipped. On 2026-10-20,
`.\tools\Test-SpeakerModels.ps1 -Model nemotron` parsed and then rejected a
skipped test because local native assets were missing.

On 2026-10-22, unpaced CPU runs of the 14.07-second public sample through
`NativeAudioEngine.StreamMeetingAsync` (4 threads, AMD Radeon 8060S host)
measured plain Nemotron ASR at 1.95 s and paired Nemotron at 13.36 s (0.95x
real time) with the diarizer's stock `low` profile (9-frame chunks). One
diarizer step cost about 0.65 s whatever the chunk length: 25-frame chunks
measured 6.64 s (0.47x), 50-frame chunks 4.33 s (0.31x), and the batch-style
`very_high` profile 2.78 s (0.20x) but with 27-second label lag. Paired Kroko
with 25-frame chunks measured 5.29 s. Two threads with 25-frame chunks measured
11.23 s (0.80x). The engine now streams the diarizer in 50-frame (4 s) chunks
with 4 frames of right context, and Auto rejects meeting candidates above 0.50x
real time on the selected device. The same run showed native token timing:
Nemotron stamps each token on its 80 ms emission frame and emits phrase
punctuation at the end of the following pause; Kroko ends each word where the
next word starts. Attribution therefore ignores silence inside a word and
keeps punctuation out of word timing. A Nemotron word whose emission frames
hold no speech is credited to the speaker active in the 320 ms before it. A
Kroko span is credited by the speech within 320 ms of its opening, then by
the speech in the rest of the 450 ms start lag before it (the largest
measured), then by the first run of speech inside it, so a span that runs
through a pause into the next speaker's onset stays with its own speaker
(checked on the real spans with alternating speaker labels). Both paired
functional tests passed on that date.
With the shipped 50-frame chunks, the same path measured paired Nemotron at 4.38 s (0.31x) and paired Kroko at 3.06 s (0.22x). `NemotronPairedCpuKeepsPaceWithTwoMinutesOfLiveAudio` then paced the public sample nine times (126.6 s) in 80 ms packets through a 300-packet channel on CPU: the backlog peaked at 8 packets, no packet waited, the first speaker label arrived 5.0 s after start, the final arrived 0.7 s after input ended, and all nine Mother Nature anchors were recognized across 36 turns. That is one single-speaker run on one machine, not a multi-speaker or two-hour qualification.


On 2026-10-09, live microphone plus system/application loopback tests captured an
in-memory 440 Hz tone in both modes and released their native resources. Core
tests cover incremental transcript saving, Pause/Resume, finalization, bounded
backlog failure, retained ownership, and crash recovery.

An initial VibeVoice CPU smoke attempt was blocked by the memory guard with
only 0.9 GiB commit capacity available. A later run passed the unchanged guard
and produced speech while the input stream remained open. For the 14.07-second
public sample, first speech arrived 20.00 seconds after test start and finalization
completed at 39.72 seconds; observed working set was 2,441.8 MiB at first speech
(78.1 MiB initially). These include startup and unpaced sample input, not a
measured live meeting update interval. No safeguards were changed to force loading.
Run `NativeMeetingTests` with installed streaming weights and adequate available
commit capacity. Two-hour memory, latency, and speaker accuracy remain unqualified.

Optional Nemotron CPU timing refinement uses recognized subword-token anchors,
not forced alignment. Only uniquely covered phrases receive refined bounds;
ambiguous/repeated/missing phrases keep approximate times. Deterministic tests
verify abstention and unchanged text. On 2026-10-09, native Nemotron CPU token
anchors matched an unchanged phrase from the public Mother Nature sample.
Decoder padding punctuation outside the clip is excluded; out-of-range lexical
anchors still abstain. English numeral comparison preserves original wording;
long-turn endpoint matching requires unique endpoints and at least 80% word
agreement, with previously aligned spans reserved. On 2026-10-09 the combined
CPU pipeline produced speech before End at 13.23 seconds and finalized at
33.22 seconds with one aligned turn for the 14.07-second public sample.
These include startup and unpaced input; live throughput still requires
separate performance qualification.
Teams UIA initialization/teardown passed, but the available Teams process had no
visible eligible meeting window. Live name attribution remains unqualified.

On 2026-10-10, automatic detection's native capture-session enumeration passed
without recording audio or reading participant names. No active Teams owner was
found in that check. Deterministic tests cover process ancestry, PID reuse,
debouncing, dismissal, and failure gaps. This does not qualify actual Teams
join/mute behavior. Its two-second enumeration budget is checked between native
calls; a blocked Windows call can delay shutdown while its owned worker is joined.

## Known limits

The opt-in record chord's native capture and managed NativeAOT package checks
are recorded in [record chord validation](record-chord-validation.md). Its
microphone anchor is a software QPC timestamp immediately before `waveInStart`.
Word mapping, a silent MP4, and an ink fixture do not establish measured
microphone/video acoustic synchronization or physical multi-monitor behavior.

- On 2026-10-10, a read-only `AppCapability.CheckAccess` probe ran under
  Ansible's registered package identity and returned `Allowed`. It did not
  request permission, open a capture device, or change the running app.
  Deterministic tests cover prompt-required, denied, unavailable, concurrent,
  cancelled, and revoked permission paths. Actual first-time consent UI and
  deny/Settings recovery still need interactive packaged validation.
- Moonshine streaming input is final-only; Qwen buffers input. Neither promises
  continuously updated recognition text.
- Nemotron GPU streaming has a recorded failure on Intel Arc 140V with driver
  `32.0.101.8425` (2026-10-08): WAV transcription completed, but streaming cleanup
  terminated with native `vk::DeviceLostError` from `vk::Queue::submit`. This can
  terminate the app. Auto qualifies Nemotron Vulkan like every other model, so
  a GPU that passes isolated qualification runs live Nemotron streaming in the
  app process. Manual GPU has no fallback. Each model/GPU combination requires
  separate qualification. Isolated
  `Ansible.DeviceProbe` qualification cannot catch a later in-process driver
  crash; live GPU work still runs in the app process. Auto does not replay
  consumed live audio after a GPU failure.
- VibeVoice streaming can return speaker annotations without speech metadata.
  The app parses its speaker-attributed text for shortcut insertion. Unexpected
  output remains unknown and is not inserted; its speech-word count stays unknown.
  Meeting finalization rejects unknown speech from either source instead of
  replacing live turns with a successful empty transcript. Known empty speech
  remains a valid final result.
- Offline VibeVoice 7B microphone dictation buffers at most five minutes of PCM16
  in memory, then uses offline recognition after Finish. This does not enable
  native streaming or live previews for that model.
- Offline VibeVoice 7B recognition remains unvalidated in the recorded integration
  checks because the memory guard rejected loading. Its weights alone occupy
  about 9.18 GiB. Loading and decoding need additional memory.
- Host RAM and commit admission checks run on both backends. They estimate native
  workspace and reserve headroom; they do not bound every allocation or check a
  dedicated GPU-memory budget. Keep safeguards enabled. Do not close unrelated
  apps to force a model load.
- Unconfigured meetings use Nemotron ASR with the Nemotron 3 companion when
  both are installed. If Nemotron ASR is missing, installed Kroko uses that
  companion. Recognition and the diarizer share the captured stream, joint
  memory admission, and native reset or disposal. If no paired Nemotron or Kroko
  ASR is available, meetings keep VibeVoice streaming with a 4096-step rolling
  native history, including when the companion is absent. Capture is limited to two hours and Pause/Resume keeps one stream.
  Those limits are not evidence of two-hour speaker accuracy or latency.
  `NativeMeetingTests` covers the retained VibeVoice path. Paired speaker
  checks use `Test-SpeakerModels.ps1` when the native runtime and weights are
  present; `NemotronPairedCpuKeepsPaceWithTwoMinutesOfLiveAudio` paces the
  looped public sample through a meeting-sized channel on CPU. GPU pairs,
  longer meetings, and multiple speakers remain unqualified. A GPU that
  qualified may still fail in-process; the CPU fallback is held to the same
  real-time limit and the meeting fails rather than falling behind silently.
- Dictation/file recognition operations have a five-minute timeout; the probe uses three
  minutes. WAV input is limited to five minutes, 100 MiB on disk, and 64 MiB of
  decoded floats. Native calls can outlast these deadlines while cleanup waits.
- ARM64 CPU/Vulkan audio.cpp, capture helpers, DeviceProbe, and the NativeAOT MSIX bundle
  cross-compiled successfully on Windows x64. No ARM64 executable was run.
  ARM64 uses ARMv8-A/NEON without OpenMP; model throughput is unmeasured.
  Run recognition, capture, insertion, and clean-machine checks on ARM64 before
  shipping that architecture. See [ARM64 builds](development.md#arm64-cross-build).
  ARM64 Vulkan execution remains unqualified. NPU inference is not implemented. Clean-machine deployment
  and physical microphone-driver failure behavior remain unqualified.

See [model behavior](user-guide.md#model-behavior) for the supported app workflows
and [tests](development.md#tests) for the separate native integration test.
