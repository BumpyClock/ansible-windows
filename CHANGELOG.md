# Changelog

## Unreleased

- Shrink the dictation overlay to a 128 by 32 DIP pill and drive every bar from
  the current microphone level through a fixed centre-weighted shape, with a
  bounded release, instead of scrolling energy history across the pill.
- Add **Settings > Overlay position**. By default the pill appears under the
  destination text cursor, read once at shortcut press from the Windows caret or
  the field's UI Automation text provider, and flips above the line when the
  work area ends. The bottom-centre placement remains the fallback and the only
  placement when the option is off.
- Queue local usage writes on a 32-entry background channel so disk work never
  delays final insertion. Drain and join that writer at shutdown. Show
  `UsageError` in Insights after a failed or overflowed write.
- Show only area-scoped warning and error notices, including app-wide
  capture-privacy failures. Suppress routine success and cancel notices.
- Honor an explicitly saved meeting-capable streaming **Use** selection at
  meeting Start. Start snapshots the processor, language hint, and vocabulary.
  An automatic dictation default is not a meeting override. Explicit Nemotron or
  Kroko without the diarization companion fails Start rather than switching
  models. Unconfigured meetings still use the Nemotron pair, Kroko pair, then
  VibeVoice path, including when the companion is absent. There is no meeting
  model picker.
- Remove meeting `NamingStatus`, `RefinementStatus`, and
  `DeviceSelectionSummary` diagnostic cards. Keep editable speaker labels.
  Optional naming and timing-refinement failures are trace warnings.
- Prefer installed Nemotron ASR (`nemotron-asr-0.6b-q8`) when no dictation model
  is saved. Default setup downloads and verifies that ASR and Nemotron 3
  Diarization (`nemotron-3-diarization-q8`). The companion cannot be selected for
  dictation. The native build compiles `nemotron_3_diar` and applies the
  cumulative Nemotron streaming timestamp patch.
- Pair unconfigured meetings with installed Nemotron ASR and Nemotron 3
  Diarization. If Nemotron ASR is missing, installed Kroko uses that companion
  before VibeVoice. Keep VibeVoice streaming when no paired Nemotron or Kroko
  ASR is available, including when the companion is absent.
  Native paired meetings publish cumulative timed anonymous speaker turns and
  abstain on unknown or overlapping speech. Dictation, replay, and WAV stay
  plain ASR. There is no meeting model picker.
- Qualify meetings for live pace. Auto compares the selected device's
  public-sample measurement with the sample's duration and rejects any meeting
  model above 0.50x real time. Unconfigured meetings then continue to the next
  documented candidate (paired Kroko, then VibeVoice) and the Meetings window
  says which model was skipped and why; an explicit choice fails Start instead.
  The Nemotron 3 diarizer now streams in four-second chunks, which measured
  0.31x real time for paired Nemotron on a 4-thread CPU where the stock
  low-latency profile measured 0.95x; speaker labels trail speech by up to
  about five seconds. Speaker attribution no longer counts silence inside a
  word and no longer lets a late punctuation token stretch a word into the
  pause. Nemotron words emitted within 320 ms after their speaker stopped keep
  that speaker. Kroko word spans, which run from the previous word's close to
  the next word's open, are judged by the speech around their opening, so a
  phrase-final word no longer follows the next speaker's onset. The paired
  final is published only after the native reset succeeds.
- Add Ansible.Probe `--stream-only` and `--repeat` for warm paced first-preview
  and input-end-to-final timings. Missing previews stay missing.
- Reject stale or tampered `native-runtime.json` records whose patch names,
  hashes, or compiled model families do not match the current
  `Build-AudioNative.ps1` recipe. Rebuild after recipe changes.
  `tools/Test-NativeBuildRecipe.ps1` covers that rejection.
- Require a Vulkan-capable native runtime for the default `Publish-Ansible.ps1`
  path. Pass `-Backend cpu` for an intentional CPU-only package.
- Show microphone permission guidance in a compact, dismissible overlay without
  moving page content. Open Settings to show the guidance again.
- Restore Kroko English Community 64-L Q8 downloads and streaming dictation,
  with per-request custom hotwords and decoder-aware device qualification.
  Show incompatible saved vocabulary before capture starts, while preserving
  slash-bearing entries for models that support them.
- Separate Home history ownership and typed card projections from dictation
  controls. Reuse one adaptive Settings row. Derive shared theme roles from a
  canonical palette. Correct High Contrast selection visibility, dark caption
  contrast, localized history times, and recycled Dictionary action labels.
  Keep bounded page lists reachable when warnings reduce the window height.
- Match the Pencil AnsibleDesign Home and Settings layouts in light and dark
  mode with a Mica window base. Keep the existing Windows font. Centralize semantic colors, typography,
  spacing, and control styles in `Ansible/DesignTokens.xaml`, with separate brand
  roles for future color schemes and system colors for High Contrast.
  Replace the waveform app identity with the redesigned Ink / Citron conversation
  mark and native-size Windows icons. Add searchable, bounded dictation history
  held only in memory for the current app session.
  Adapt page padding, navigation, headers, and settings controls to compact windows.
  Keep Home's shortcut helper compact; move WAV transcription to Models.
  Use a shared page canvas for every destination. Refine Home card hierarchy in
  Pencil and show structured, measured CPU/Vulkan comparisons in Settings.
- Move custom words from Settings onto a Dictionary sidebar page. Add, import,
  and edit entries in dialogs, search a compact list, and undo the latest
  deletion. Existing saved words are kept.
- Add automatic CPU/Vulkan selection using isolated public-sample benchmarks,
  with cached results and explicit qualification failures.
- Keep final meeting transcription and native cleanup off the UI thread to
  prevent a deadlock when ending a meeting. Shutdown cancels and joins meeting
  preparation before it can open capture.
- Retain 500 ms of microphone input after push-to-talk release. Keep the
  waveform visible until capture stops, including when stop fails.
- Show delivery outcomes on Dictation and Settings, with shared dismissal.
- Preserve clipboard speech for delayed paste readers; refresh destination
  identity and distinguish verified, unverified, partial, and rejected delivery.
- Add configurable paste shortcuts with owned-key cleanup and no replay of
  uncertain delivery.
