# Validate native recognition

Build the runtime and download the model under test using the
[development guide](development.md). Run commands from the repository root in
PowerShell. Probes use local model weights and a public sample, not the microphone.

The native build pins audio.cpp v0.9.0, applies tracked patches including
`tools/patches/audio-nemotron-stream-timestamps.patch`, and supports CPU and
optional Vulkan. Compiled families include `nemotron_3_diar` and `kroko_asr`.
The [catalog](../tools/audio-models.json) pins each model's revision, file length,
SHA-256, license, and capabilities. Use those capabilities rather than assuming
that an upstream model feature exists in this backend.

## Run the probe

This command selects Moonshine explicitly, transcribes the WAV, then feeds the
same audio at microphone cadence:

```powershell
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav moonshine-tiny
```

Use the directory containing your weights instead of `.runtime\models` when
needed. Supply an exact model ID to avoid the probe's default model selection.
Run probes sequentially and wait for each process to exit before starting another.

The probe defaults to CPU, four threads, a blank language hint, and 512 MB of
memory headroom. Each recognition operation has a three-minute timeout. It
reports the requested backend, model, stage, elapsed time, transcript, word count,
and previews received before audio input ended. Unknown word counts remain
unknown. Cancellation waits for an executing native call to finish.

| Option | Effect |
| --- | --- |
| `--file-only` | Skip streaming; offline models always skip it |
| `--stream-only` | Skip WAV transcription; prepare once, then stream |
| `--repeat <1..20>` | Repeat the paced stream for warm first-preview and input-end-to-final timings. Missing previews stay missing. Cannot combine with `--file-only`. Use `--stream-only` with `--repeat` for buffered offline dictation. |
| `--backend cpu`, `--backend vulkan`, or `--backend auto` | Select the backend; default CPU. Auto is not implied. |
| `--device <index>` | Select a nonnegative device index; default 0 |
| `--device-probe <executable>` | Required for `--backend auto`. Path to `Ansible.DeviceProbe.exe`. The probe does not discover the worker. |
| `--benchmark-cache <json>` | Optional Auto cache file. Omit to keep the selection in this process only. |
| `--dictionary <text>` | Supply newline-separated recognition hints; unsupported models reject them |

Vulkan example:

```powershell
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav moonshine-tiny --backend vulkan --device 0
```

Auto example. Build the app first so the worker exists. Use the public sample as
the positional WAV; Auto uses that file for isolated qualification. Use the
`Debug` worker folder after a Debug app build. Auto probes CPU, then GPU;
compared times exclude load and warmup. Each device has a five-minute deadline
that includes load, warmup, recognition, and teardown. The CLI also has a
three-minute timeout for each recognition operation, so it can cancel qualification before
a device deadline. A GPU worker crash or
timeout disqualifies GPU only. Shared validation, integrity, memory, protocol,
or ownership errors abort calibration and do not write a device cache entry:

```powershell
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav moonshine-tiny --backend auto `
  --device-probe .\.runtime\device-probe\Release\Ansible.DeviceProbe.exe `
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

To check CPU eligibility without loading a model:

```powershell
dotnet run --project .\Ansible.Probe -- --cpu-check
```

## Interpret results

A successful build, installed file, or GPU listing is not a recognition check.
Verify the pinned weights, load the model, and check actual speech output.
WAV success does not establish streaming success. For models that advertise live
previews, inspect previews received while input is still open. Shortcut insertion
still waits for the successful final result.

Record the source revision, model ID and hash, backend, CPU/GPU, driver, available
memory, sample, and command with each result. Separate cold loading from reused
session timing. A public-sample transcript does not establish accuracy across
speakers, languages, microphones, or noise conditions. Paced streaming elapsed
time includes audio delivery and is not pure inference latency.

## Auto qualification

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
qualify other models, long live dictation, or meeting history. A rebuilt native
runtime changes Auto content-hashed cache keys. Existing CPU-only calibration
entries are not reused for a Vulkan runtime; do not delete the cache or force
GPU selection to pick up the new keys. Auto Kroko dictionary mode qualifies
`modified_beam_search` with a fixed public hint only. The user's dictionary
never enters worker arguments or the cache. The paired Auto key includes the
diarizer hash and profile.

## VibeVoice streaming timing

On 2026-10-20, five prepared reused CPU streams then five Vulkan streams used
`vibevoice-streaming-1.5b-q4` from the pinned catalog, native audio.cpp v0.9.0
plus both tracked patches, four threads, Vulkan device AMD Radeon 8060S, the
14.0719-second public WAV, and no dictionary. Packets were 40 ms with absolute
packet-end availability on a bounded channel. Native DLL SHA-256
`533dc550f1d7e36b3f55bbbc0b6d173730717ddec952c47b51d69ad1145cb0fe`. Sample
SHA-256 `a8096a0c2ac136bcaf70143dc80ba576ade8dfbf7f9f30ecdec29b920ab3e3cb`.
Model SHA-256 is the catalog pin.

All ten streams produced the same normalized 22-word transcript. Each published
four speech previews before input closed. Timings exclude model preparation, the
500 ms shortcut tail, and UIA, clipboard, or input delivery.
Source-duration-to-final includes source backlog. It is not the channel-end-only
metric. Values are median (min–max) milliseconds across five reused streams.

| Metric | CPU | Vulkan |
| --- | --- | --- |
| First preview | 7621.0 (6901.1–11934.7) | 4099.1 (3968.9–4319.8) |
| Source-duration-to-final | 9265.7 (4923.5–16736.2) | 327.7 (299.9–747.1) |

These are bounded observed wall-clock cases only. They are not a speedup ratio,
p95, or pure-compute result, and they do not qualify Nemotron or Kroko pairs.
CPU variance was high. The first CPU trial may have overlapped a short managed
build. Contention is possible. The cause is not known. Native model buffering
remains inside first-preview latency.

## Meeting qualification

Meetings default to Nemotron ASR with the Nemotron 3 Diarization companion.
VibeVoice streaming remains selectable. Kroko plus the same diarizer is an extra
option, not the default. Nemotron and Kroko diarization applies to meetings only.
The measurements below are historical VibeVoice CPU runs. They do not qualify
the new default pair, Kroko, two-hour sessions, or multi-speaker accuracy.

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

Rerun CPU speaker-pair checks with installed weights and the public sample:

```powershell
.\tools\Test-SpeakerModels.ps1
```

`-Model nemotron`, `kroko`, or `both` (default) runs `NativeSpeakerModelTests`
sequentially. Install Kroko with `.\tools\Setup-AudioBackend.ps1 -Models kroko-en-64l-q8`.
A non-default setup also needs `nemotron-3-diarization-q8` for the pair. The
script is CPU only. The public sample cannot qualify two-hour latency or
multi-speaker accuracy.

On 2026-10-20, `.\tools\Test-SpeakerModels.ps1` passed exactly one native CPU
case each for `nemotron-asr-0.6b-q8` and `kroko-en-64l-q8` on pinned audio.cpp
v0.9.0 plus tracked patches and verified downloaded weights. The public sample
is 14.072 seconds. Each case reported speech while the input channel stayed
open, a final transcript containing "mother nature", nonempty bounded timed
speaker labels, and every final turn was non-provisional. The Kroko case supplied
nonempty `Mother Nature\nUnited States` hotwords. Delivery was unpaced. Another
app may have calibrated at the same time. These runs are not performance
results. They do not qualify GPU, multi-speaker accuracy, long sessions, or
live latency.

## Known limits

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
  terminate the app. Native Auto streaming skips Nemotron Vulkan qualification
  and uses a qualified CPU. Manual GPU has no fallback. Other
  model/GPU combinations require separate qualification. Isolated
  `Ansible.DeviceProbe` qualification cannot catch a later in-process driver
  crash; live GPU work still runs in the app process. Auto does not replay
  consumed live audio after a GPU failure.
- VibeVoice streaming can return speaker annotations without speech metadata.
  The app parses its speaker-attributed text for shortcut insertion. Unexpected
  output remains unknown and is not inserted; its speech-word count stays unknown.
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
- The dedicated meeting path defaults to Nemotron ASR plus Nemotron 3
  Diarization, with VibeVoice streaming as an alternative, a 4096-step rolling
  native history, and a two-hour capture limit. It processes chunks throughout,
  preserving one stream across Pause/Resume. This limit is not evidence of
  two-hour speaker accuracy or latency. Kroko plus the Nemotron diarizer is an
  extra CPU-focused option; upstream notes it may be slow, and Vulkan needs
  contributor testing. Validate CPU and Vulkan separately with
  `NativeMeetingTests` and `.\tools\Test-SpeakerModels.ps1`, then qualify long
  meetings and multiple speakers. The public sample cannot establish those
  results.
- Dictation/file recognition operations have a five-minute timeout; the probe uses three
  minutes. WAV input is limited to five minutes, 100 MiB on disk, and 64 MiB of
  decoded floats. Native calls can outlast these deadlines while cleanup waits.
- ARM64, NPU, clean-machine deployment, and physical microphone-driver failure
  behavior require separate qualification.

See [model behavior](user-guide.md#model-behavior) for the supported app workflows
and [tests](development.md#tests) for the separate native integration test.
