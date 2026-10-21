# Changelog

## Unreleased

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
