# Changelog

## Unreleased

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
