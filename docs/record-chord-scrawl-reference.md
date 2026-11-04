# Scrawl implementation reference

Inspected the local Scrawl clone at
`b5fa0705f6e5724ede414727797b4b6ed274d3d2` for the record chord spikes.
These findings describe source behavior. Scrawl was not built or exercised
against the acceptance targets in this session.

## Windows capture and encoding

`src/win/media.rs` implements `capture_source`, `Recorder`, and `Encoder`.
It uses WGC `CreateForMonitor` or `CreateForWindow`, a free-threaded two-frame
pool, D3D11 conversion to NV12, and a Media Foundation sink writer with H.264.
The encoder requests hardware transforms. That request alone does not prove
which encoder runs. Output dimensions align to 16 pixels. Its bitrate scales
with resolution and frame rate, bounded between 2 and 16 Mbps.

Captured frame `SystemRelativeTime` supplies QPC-relative timestamps. The MP4
starts at its first real frame. The capture worker records `video_offset_ms`
to put that first frame back onto the capture session's clock. This explicit
offset is useful for Ansible's future shared audio/ink/video anchor.

Scrawl attempts `SetIsBorderRequired(false)` and ignores its result. That is
not a borderless qualification. Ansible's spike records the HRESULT and getter;
visual border behavior remains a separate observation.

## Ink and input

`src/win/overlay.rs` creates a layered, topmost, no-activate, click-through
window. It calls `WDA_EXCLUDEFROMCAPTURE` for the overlay itself.
`src/capture/windows.rs` uses mouse and keyboard hooks to collect input and
swallows Shift drawing input. Shift-click creates a pin. Its shortcut code
uses an unassigned virtual key, `0xE8`, to mask Alt/Win release effects.

Completed ink stays fully visible for 650 ms and fades over the next 650 ms.
The saved stroke geometry remains available after the live ink fades.
Frames are annotated later by `src/render.rs`. This supports keeping stroke
data independent of the live overlay.

Ansible's settled contract differs here. The overlay must block input and
appear in frames and video. We can reuse the stroke/session separation and
modifier masking idea, while implementing the required capture behavior.
For the later summary frame, this implies acquiring a clean final background
after the live overlay clears, then compositing saved strokes once. Compositing
over a frame that already contains live ink would draw those marks twice.

## Frame selection and text

`src/keyframes.rs` groups marks on an unchanged screen, using luma changes
near the ink. A group closes on intervening click/drag, after 20 seconds, or
after 12 strokes. Groups beyond the frame budget merge with neighbours.
The budget grows from six frames for short captures to at most twelve.
Marks remain represented after merging.

`src/render.rs` composes selected annotated frames into one contact sheet.
Its documented layout caps the sheet at a 1600-pixel long edge. Individual
frame numbers are burned into the images; times and descriptions stay in text.
Ansible retains its settled burst and summary frame rules and proposed
number-plus-time labels.

`src/agent_context.rs` has an optional event-aware summary. It uses timed words
to interleave marks and events with speech and can move the split to a nearby
clause boundary. The feature is experimental. Scrawl's Hex integration estimates
word positions by length within speech islands and marks them estimated.
That estimation does not meet Ansible's Nemotron timing requirement.

## Clipboard delivery

`src/handoff.rs` normally copies the text summary. The explicit Paste action
publishes one contact-sheet image, sends paste, waits 160 ms, publishes text,
then sends paste again. It also waits briefly around publication and modifier
release. These delays are pacing, not evidence of consumption or insertion.

The Windows-only `clipboardImage` experiment instead publishes one PNG plus
`CF_DIBV5` and `CF_UNICODETEXT` together. `src/win/clipboard.rs` owns the Win32
clipboard allocation and transfer. Scrawl's documentation reports a check in
Claude desktop. It does not establish multi-file `CF_HDROP` support or
acceptance in Ansible's five requested target surfaces.

The useful takeaway is to exercise both one-image combined publication and
contact-sheet fallback in the manual harness. Keep the attachment cap and
fallback choice open until that matrix has hand results. Preserve Ansible's
clipboard exclusion requests and insert-once contract.

## Source pointers

All paths are relative to the cloned reference repository.

- `docs/windows.md` describes Windows platform boundaries.
- `docs/capture.md` describes session startup, candidate bounds, and video offset.
- `docs/keyframes-and-text.md` describes frame selection, text, and clipboard experiments.
- `src/win/media.rs` owns WGC and Media Foundation.
- `src/win/overlay.rs` owns the Windows overlay and fade.
- `src/capture/windows.rs` owns Windows input hooks and modifier masking.
- `src/keyframes.rs` owns frame selection and mark grouping.
- `src/agent_context.rs` owns optional event-aware text.
- `src/handoff.rs` and `src/win/clipboard.rs` own clipboard delivery.

Scrawl's `LICENSE` is MIT, copyright 2026 Kit Langton. Preserve its notice if
copying source. These spikes use it as an architectural reference.
