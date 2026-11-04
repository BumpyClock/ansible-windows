# Record chord implementation contract

The product decisions and defaults are recorded in
[the feasibility report](record-chord-spikes.md). This document describes the
implementation boundary used by the record chord. Actual verification and
remaining hand checks are recorded in [the validation report](record-chord-validation.md).

## Ownership

One `ShortcutDictation` coordinator must route Voice and Record activations.
An admitted attempt records its owning chord. Only that chord can stop it,
release it, or replace its release tail. A second independent coordinator would
allow one chord to finish the other's dictation under the current latch logic.

`DictationSession` remains the only microphone and recognition admission owner.
The admitted dictation operation owns a distinct `RecordTakeSession`. The take
owns its immutable options, media adapter, ink/burst timeline, frame finalization,
and prepared bundle. It does not own the hotkey or destination input. This leaves
an independent recording toggle possible without making the hook a session owner.

Target inspection is fatal for the existing voice-only shortcut. Record takes
need optional destination inspection. Failure to find an editable destination
must retain successful speech and media for manual Copy.

## Clocks and media

Record the take origin on the QPC clock at accepted activation. The microphone
must expose a sample-zero software capture anchor taken immediately before
`waveInStart`. Its post-factory ready time and diagnostics-only timestamp cannot
substitute for that anchor. Do not describe software start as measured acoustic
onset.

Forward complete `TranscriptUpdate` snapshots to the active take. Preserve sample
positions and native word timings, replacing the revisable snapshot each time.
Unavailable mappings must not retain stale earlier timings.

Keep source capture timestamps separate from encoded sample timestamps. A quiet
screen must hold its last delivered image through the actual recording stop.
Repeated encoded samples are not fresh WGC captures. Preserve a genuine startup
gap explicitly rather than shifting the video clock and claiming it matches the
take clock. Never backdate a later screen image as observed earlier content.

Mouse input handling must stay independent of blocking frame conversion and
encoding. The overlay remains topmost and no-activate, blocks input on all
monitors, and accepts ink only on the monitor selected at activation. Keep the
ink overlay in capture. Force the pill and indicator out of capture for the take,
including when the general window privacy setting is off.

A burst frame needs a presentation fence so it contains only that burst's marks.
The summary needs a clean final background after the live overlay clears, then
all retained stroke geometry composited exactly once. Capturing already-visible
ink and painting it again would double the marks. The picture cap limits saved
individual frames, not burst numbering, text markers, or the summary geometry.

## Result and completion

Keep `RecognitionResult.SpeechText` authoritative and unchanged. Usage counts
continue using that speech. A separate immutable payload owns formatted text,
bounded PNG descriptors, and an optional video path. The video is not among the
clipboard picture files.

Finalize the take and prepare its text before successful dictation completion is
published. `HomeDictationHistory` currently subscribes before shortcut delivery
and immediately saves the result. Formatting only after awaiting recognition is
too late. Completion, current-result Copy, automatic delivery, and history must
consume the same prepared text. History must never serialize the media.

The current Dictation result must keep its media Copy actions visible even when
its text already has a history row. Never fabricate an empty recognized speech
result to disguise missing authoritative speech.

## Delivery

[The observed targets](record-chord-clipboard-results.md) do not preserve both
images and text from one combined clipboard publication. Separate bulk-file and
text publications are the starting candidate. Retain the user-confirmed
provisional eight-picture cap without calling it an all-target measured limit.

The safe automatic ordering is text first, followed by one bulk-file paste only
after exact same-field text verification. This proves the text has been consumed
before replacing its clipboard publication. A timer, render callback, or queued
Ctrl+V is not that proof. The image stage is queued, never attachment-verified.

If text verification is unavailable, leave its publication available and retain
pictures for manual Copy with an accurate notice. Recheck destination identity,
operation currency, cancellation, physical keys, and clipboard sequence before
the file paste. Publish the three exclusion formats before content and leave
the files and clipboard data available to delayed readers.

After any text input, a file-stage deferral must not become a whole-operation
`Deferred` outcome. The existing delivery state machine retries that outcome,
which would replay speech. Keep text evidence and picture evidence separate.

Text-first then eight files was observed in Claude desktop. The automated
ChatGPT and Copilot checks observed the reverse order. The user subsequently
reported matching web behavior. Keep this qualification scope explicit; neither
manual visual inspection nor that report proves the app's bounded UIA text
verifier works in every composer.

## Failure and release

Ordinary frame, ink, or encode failures degrade media and allow authoritative
text. Esc before delivery commitment discards the take and suppresses automatic
delivery. Cancellation and shutdown join every owned startup, capture, inference,
finalization, and input operation.

A retained native resource still blocks new work and must remain owned. Do not
fake an idle state to permit delivery. If authoritative speech is available after
a media release failure, represent the degraded result and blocked admission
explicitly so the text is not lost behind the recovery gate.

Private capture storage must protect active takes during cleanup, bound total
retention, and keep disk work off the input and UI threads. Manual deletion must
not race an active writer. Capture-file lifetime and clipboard publication must
be handled together; returning from `SendInput` is not permission to delete files.

## Behavioral checks

Cover cross-chord ownership, Alt/Win qualification and menu-mask cleanup, hold
tails and latch mode, Esc during startup/finalization, optional destinations,
unchanged speech counts, identical payload/history/Copy text, real timing versus
approximate/timeline fallbacks, burst and summary isolation, picture caps,
retention, media degradation, exact-text verification before file publication,
no replay after file-stage failure, and joined shutdown. NativeAOT MSIX execution
and real target checks remain separate from deterministic core tests.
