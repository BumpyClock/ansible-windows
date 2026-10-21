# Changelog

## Unreleased

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
- Default dictation and meetings to Nemotron ASR 0.6B Q8
  (`nemotron-asr-0.6b-q8`) with Nemotron 3 Diarization Q8 as a meeting companion.
  Setup downloads both. The diarizer cannot be selected for dictation. Nemotron
  and Kroko WAV and shortcut insertion stay plain ASR speech.
- Add optional Kroko English Community 64-L Q8 (`kroko-en-64l-q8`) as a second
  streaming ASR. Pair it with the Nemotron diarizer for meetings by selecting
  Kroko on Speech models before Start meeting. This is not the new default.
  Kroko uses custom words as hotwords and rejects `/` in an entry.
- Add `--stream-only` and `--repeat` to Ansible.Probe for warm paced first-preview
  and input-end-to-final timings. Missing previews stay missing.
- Require a Vulkan-capable native runtime for the default `Publish-Ansible.ps1`
  path. Pass `-Backend cpu` for an intentional CPU-only package.
- Write usage statistics on a bounded background worker after successful
  recognition, and join that worker on close.
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
- Remove routine insertion, cancellation, and background-diagnostic banners.
  Keep actionable failures on their relevant pages, usage-storage errors in
  Insights, and privacy warnings. Remove meeting device/speaker/refinement
  narration and persistent copy, export, and wording-cleanup confirmations.
  Keep validation, progress, privacy disclosures, and destructive-action consent.
- Preserve clipboard speech for delayed paste readers; refresh destination
  identity and distinguish verified, unverified, partial, and rejected delivery.
- Add configurable paste shortcuts with owned-key cleanup and no replay of
  uncertain delivery.
