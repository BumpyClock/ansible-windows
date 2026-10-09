# Validate native recognition

Build the runtime and download the model under test using the
[development guide](development.md). Run commands from the repository root in
PowerShell. Probes use local model weights and a public sample, not the microphone.

The native build pins audio.cpp v0.9.0 and supports CPU and optional Vulkan.
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
| `--backend cpu` or `--backend vulkan` | Select the backend explicitly |
| `--device <index>` | Select a nonnegative device index; default 0 |
| `--dictionary <text>` | Supply newline-separated recognition hints; unsupported models reject them |

Vulkan example:

```powershell
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav moonshine-tiny --backend vulkan --device 0
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

## Meeting qualification

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

## Known limits

- Moonshine streaming input is final-only; Qwen buffers input. Neither promises
  continuously updated recognition text.
- Nemotron GPU streaming has a recorded failure on Intel Arc 140V with driver
  `32.0.101.8425` (2026-10-08): WAV transcription completed, but streaming cleanup
  terminated with native `vk::DeviceLostError` from `vk::Queue::submit`. This can
  terminate the app. Use CPU for Nemotron dictation. Other model/GPU combinations
  require separate qualification.
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
- The dedicated meeting path uses VibeVoice streaming with a 4096-step rolling
  native history and a two-hour capture limit. It processes chunks throughout,
  preserving one stream across Pause/Resume. This limit is not evidence of
  two-hour speaker accuracy or latency. Validate CPU and Vulkan separately with
  `NativeMeetingTests`, then qualify long meetings and multiple speakers.
- Dictation/file recognition operations have a five-minute timeout; the probe uses three
  minutes. WAV input is limited to five minutes, 100 MiB on disk, and 64 MiB of
  decoded floats. Native calls can outlast these deadlines while cleanup waits.
- ARM64, NPU, clean-machine deployment, and physical microphone-driver failure
  behavior require separate qualification.

See [model behavior](user-guide.md#model-behavior) for the supported app workflows
and [tests](development.md#tests) for the separate native integration test.
