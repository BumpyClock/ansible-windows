# Changelog

## Unreleased

- Prefer installed Nemotron ASR (`nemotron-asr-0.6b-q8`) when no dictation model
  is saved. Default setup downloads and verifies that ASR and Nemotron 3
  Diarization (`nemotron-3-diarization-q8`). The companion cannot be selected for
  dictation and is not wired into meetings. The native build compiles
  `nemotron_3_diar` and applies the cumulative Nemotron streaming timestamp
  patch.
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
