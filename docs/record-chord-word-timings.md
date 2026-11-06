# Dictation word timestamp spike

Measured on 2026-11-04 from application base revision `66e7a6a` plus the
record-chord spike changes. This is spike 3. It does not implement ink placement
or establish synchronization with microphone start, QPC, or video.

## Result

Both installed native streaming models supplied cumulative timestamps that
mapped onto every final lexical word of the 14.0719375-second public sample.

| Model | Mapped non-final snapshots | Final mapped words | Revisions in overlapping words |
| --- | --- | --- | --- |
| `nemotron-asr-0.6b-q8` | 26 of 26 | 28 of 28 | 3 |
| `kroko-en-64l-q8` | 10 of 10 | 28 of 28 | 9 |

The counts come from the probe logs below. The revision count compares each
mapped snapshot with its predecessor at the same lexical word positions. A
revision can change text offsets or sample bounds. It does not mean the final
text was inserted more than once.

Nemotron's native source assigns decoded subword tokens to 80 ms decoder
emission frames. Combining the subwords supplies word bounds, not forced
acoustic alignment. In the measured final, `Some` maps to samples
15360..16640 (0.960..1.040 s), and the first `nature` maps to
32000..34560 (2.000..2.160 s). These are observed native anchors.

Kroko supplies whole words on a 40 ms encoder-frame grid. Its source explicitly
sets each word's end to the next word's start, and the final word's end to at
least the input duration. The observed first `nature` spans 1.920..3.160 s;
the second `Nature` spans 4.840..6.160 s. These spans include pauses. Kroko
therefore supplies word-position anchors, but its ends must not be presented
as measured speech endings. Keep the settled approximate marker behavior for
Kroko until acoustic accuracy is separately qualified. No human-labeled word
boundaries were available in this spike.

## Implementation

`TranscriptUpdate.WordTimings` and `RecognitionResult.WordTimings` carry a
replacement snapshot, including the revisable tail. Each word contains offsets
into the unchanged `SpeechText` and cumulative 16 kHz input sample bounds.
The snapshot labels its source `NativeTokenEmission` or `NativeWordSpan`.
Streaming final callbacks also carry the total `ProcessedSamples`.

`NativeAudioEngine` reads the C ABI word metadata before disposing the native
result. It reads only Nemotron and Kroko timestamp metadata. Other families
keep unavailable word timings. Each update replaces the snapshot; callers must
not append snapshots or keep a previous mapping when the new mapping is absent.

`MeetingWordAlignment.MapSpeechWords` reuses the existing token-to-word reader
and lexical comparison. The complete ordered lexical sequences must agree.
Case, punctuation, and apostrophe spelling follow that existing comparison.
The original speech and character offsets remain unchanged. There is no
interpolation, guessed timestamp, or numeric rewrite. Unmatched, reordered,
malformed, or out-of-range lexical anchors yield no mapping. Out-of-range
trailing punctuation is ignored by the existing reader. The boundary limits
are five minutes, 8192 native anchors, and 65536 text characters. These limits
were checked with deterministic tests, not a five-minute native recording.

`Ansible.Probe --word-timings` prints snapshot coverage, replacement revisions,
the final callback sample count, and final word offsets and sample bounds.
This option observes real dictation streaming without a diarization companion.

## Reproduce

Install the catalog-pinned models if missing, then run each probe to completion
before starting the next. Both commands explicitly choose CPU.

```powershell
.\tools\Setup-AudioBackend.ps1 -Models nemotron-asr-0.6b-q8,kroko-en-64l-q8
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav nemotron-asr-0.6b-q8 `
  --backend cpu --stream-only --word-timings
dotnet run --project .\Ansible.Probe -- `
  .\.runtime\native\audiocpp.dll .\.runtime\models .\tools\audio-models.json `
  .\.runtime\validation\sample_16k.wav kroko-en-64l-q8 `
  --backend cpu --stream-only --word-timings
```

The actual runs used separate `--artifacts-path` directories to avoid a
concurrent baseline test's output locks. Paced input uses the existing bounded
8-packet channel and 40 ms packet-end schedule. Builds and other work overlapped
the checks. Their elapsed times do not qualify performance or capture overhead.

## Evidence

- [Nemotron first run](../.runtime/word-timing-nemotron.log) records all 26
  mapped preview snapshots and the 28 final words.
- [Nemotron final callback check](../.runtime/word-timing-nemotron-final.log)
  repeats that coverage after final sample propagation was added and records
  final callback `processedSamples=225151`.
- [Kroko run](../.runtime/word-timing-kroko.log) records all 10 mapped preview
  snapshots, the 28 final words, and final callback `processedSamples=225151`.
- [Final core test results](../.runtime/word-timing-results/word-timing-final-core.trx)
  record 1467 passes, no failures or skips, after final `ProcessedSamples`
  propagation was added.
- [Final focused test results](../.runtime/word-timing-results/word-timing-focused.trx)
  record 73 passes after that addition. They cover the new mapping, existing
  meeting alignment, and streaming probe behavior.

The local evidence files are ignored build artifacts. Preserve them separately
when sharing the report. Native inference was run sequentially. No microphone
audio was captured, saved, or uploaded for these tests.

Machine evidence was Windows build `26300`, AMD Ryzen AI 7 PRO 350, CPU backend,
four inference threads, unchanged 512 MB memory headroom. An OS snapshot during
the checks showed 5,751,252 KiB free physical memory out of 58,349,232 KiB.
The library enumerated AMD Radeon 860M but inference explicitly used CPU.

| Input | SHA-256 |
| --- | --- |
| `audiocpp.dll` | `220b0ed18769c6cef3929f4b6a4fc6468189e911cae92f7895e24987c02dc022` |
| Public `sample_16k.wav` | `a8096a0c2ac136bcaf70143dc80ba576ade8dfbf7f9f30ecdec29b920ab3e3cb` |
| Nemotron ASR Q8 | `c58b62c1bdd6c5d7b14b08126c5bd16c1289fee6e0d6ac0761dbd3aa6d714935` |
| Kroko Q8 | `596c749fcb6c582f2d7aaa9aefcff2929e405867a4d04864a55b2c9c2963baa5` |

The native runtime record pins audio.cpp commit
`795c45fbde0a7d29c93b22199728ff5caaec02e5`, with the tracked Vulkan loader and
Nemotron cumulative timestamp patches. Read source for the observed timing
semantics in `.runtime/audio-native-src/src/models/nemotron_asr/decoder.cpp`
(`build_token_timestamps`) and
`.runtime/audio-native-src/src/community_models/kroko_asr/session.cpp`
(`build_word_timestamps`). Model lengths and hashes passed the existing
catalog integrity admission. Kroko was downloaded with the existing setup
script for this check.
