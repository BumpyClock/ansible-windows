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

Each attempt snapshots its activation mode. Voice uses `PushToTalk`; Record
uses the independent `RecordToggleMode` setting. Record toggle mode starts with
one qualified press and finishes with the next. Releasing its keys keeps the
take open and schedules no release tail, allowing touchpad inking without held
keyboard keys.

`DictationSession` remains the only microphone and recognition admission owner.
The admitted dictation operation owns a distinct `RecordTakeSession`. The take
owns its immutable options, media adapter, ink/burst timeline, frame finalization,
and prepared bundle. It does not own the hotkey or destination input. This leaves
an independent recording toggle possible without making the hook a session owner.

Target inspection is fatal for the existing voice-only shortcut. Record takes
never inspect a destination or automatically deliver. A separate configurable
Paste capture shortcut defaults to Alt+Shift+W and inspects the field focused
when that shortcut is pressed. It is registered only while Record chord is
enabled. All three activation chords must be nonoverlapping. Paste capture must
not intercept Ctrl+V bundle delivery or the configured voice paste chord.

`ShortcutDictation` retains one completed record outcome, including its original
operation ID and immutable bundle. Voice results do not replace it. Admission of
the next record take clears it, matching the native factory's capture lease
lifetime. It is not restored after app restart. Paste cannot queue during capture
or other delivery. A new explicit paste press can reuse the retained take; held
keys defer input, never choose a different destination. Shutdown cancels and
joins paste inspection and input through the same coordinator. `RecordPayload`
and `RecordResultChanged` expose that same retained bundle to Dictation's manual
Copy actions, including after a voice or WAV result replaces the current result.

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
too late. Completion, current-result Copy, explicit bundle delivery, and history must
consume the same prepared text. History must never serialize the media.

The current Dictation result must keep its media Copy actions visible even when
its text already has a history row. Never fabricate an empty recognized speech
result to disguise missing authoritative speech.

## Delivery

[The observed targets](record-chord-clipboard-results.md) do not preserve both
images and text from one combined clipboard publication. Separate bulk-file and
text publications are the starting candidate. Retain the user-confirmed
provisional eight-picture cap without calling it an all-target measured limit.

The explicit paste operation sends text first, followed by one bulk-file paste only
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
text. Esc during the record attempt discards the take and leaves no pending
paste. Cancellation and shutdown join every owned startup, capture, inference,
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
tails and latch mode, Esc during startup/finalization, destination-free recording,
fresh paste-time destinations, no automatic record insertion, retained bundle
reuse, rejected busy paste presses,
unchanged speech counts, identical payload/history/Copy text, real timing versus
approximate/timeline fallbacks, burst and summary isolation, picture caps,
retention, media degradation, exact-text verification before file publication,
no replay after file-stage failure, and joined shutdown. NativeAOT MSIX execution
and real target checks remain separate from deterministic core tests.
