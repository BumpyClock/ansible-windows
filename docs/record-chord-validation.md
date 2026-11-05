# Record chord validation

The opt-in record chord is implemented. The user workflow is documented in
[the user guide](user-guide.md#record-chord). The normal Ctrl+Win chord remains
voice-only. The reference clone and feasibility evidence are recorded in
[the phase 0 report](record-chord-spikes.md).

## Implemented ownership and delivery

One `ShortcutDictation` coordinator routes Voice and Record activations.
The admitted microphone operation owns `RecordTakeSession`, which owns native
capture startup, ink events, finalization, and immutable bundle preparation.
Shutdown joins capture, encoding, inference, destination inspection, clipboard
input, and Settings storage work. Retained native resources block admission.
Ordinary media errors preserve authoritative text.

The microphone exposes a QPC software anchor immediately before `waveInStart`.
Ink and WGC share that clock. Nemotron markers use final native word anchors.
Other live models use the partial text observed before the ink event and label
placement approximate. Final-only models and missing anchors use a timeline.
These are software anchors, not a measurement of microphone acoustic onset.
Speech text and speech-word counts stay separate from bundle formatting.

Prepared text is published before the successful completion signal consumed by
history. History stores this text without media. Current-result Copy uses the
same payload. Escape before possible input retracts the result and history;
after possibly queued input the app retains evidence and warns to check the
destination. No input is recalled or replayed.

Combined file/text publications dropped text in the observed targets. Automatic
delivery therefore uses text first, then one bulk CF_HDROP paste only after
exact same-field text insertion is observed. If verification is unavailable,
text stays on the clipboard and pictures require manual Copy. The picture
stage never reports attachment verification and cannot cause text to retry.
All automatic publications request the same three clipboard exclusions.
Manual Copy uses the ordinary clipboard. File leases protect delayed readers
until the clipboard publication changes.

## Verification on 2026-11-04

The final Windows non-native core suite passed 1523 tests, with no failures or
skips. The result is `.runtime/record-spikes/tests/record-chord-verified.trx`.
It covers timing placement and abstention, cross-chord ownership, Escape before
and after possible input, deferred picture delivery without speech replay,
prepared history text, retained-resource admission, and capture storage leases.
A regression also checks discard after Finish has already stopped media.

The focused portable take, retention, and bundle-delivery check passed 28 tests
in a Boat Linux sandbox using the .NET 10 SDK container. The raw result is
`.runtime/record-spikes/boat-record-core-result.json`. Two preceding attempts
exposed test-fixture defects: a hardcoded Windows path and a shared static clock
origin. The fixtures now use platform paths and an independent origin per test.
Their failed outputs remain in the ignored evidence folder. This is not a full
Linux, WinUI, or Windows capture qualification. No GitHub CI run was triggered
because no branch was pushed.

The native ABI/lifecycle self-check passed. A four-second real desktop capture
produced one readable 1920 by 1200 final PNG and a silent H.264 MP4. Independent
decoding observed 98 frames and 3.2666 seconds. The first WGC image arrived
748.7 ms after the host origin. This genuine startup gap is reported rather
than backdating a later image. This is a functional check, not a performance
claim or a unique-frame-rate measurement.

A labeled synthetic geometry fixture exercised real WGC, rendering, frame
fences, PNG generation, summary composition, and video encoding. Its first
picture contained only the left rectangle; its second contained only the right
arrow; its summary contained both, numbered once. Pixel checks and direct image
inspection passed. An extracted MP4 frame contained ink. No OS input was
injected. These checks do not establish physical pointer handling. See the
[native helper guide](../tools/record-capture/README.md) for commands and artifacts.

Full NativeAOT publishing passed after updating the welcome practice target to
the payload contract and hardening pill exclusion during record takes. The log
is `.runtime/record-spikes/publish-record-chord-privacy.log`. Native executables,
XAML resources, native runtime closure, and `record_capture.dll` were verified.

The actual published executable and helper ran through `WindowsRecordTakeFactory`
under isolated identity `BumpyClock.Ansible.RecordSpike.takefinal1`. It reported
`dynamic-code=False`, `released=True`, no discard, and no warnings. One PNG was
1920 by 1200 and 700192 bytes. Independent MP4 decoding found 73 samples, zero
audio streams, and 2.4333 seconds. The first video image arrived 1647.8 ms after
the accepted software origin, explaining the shorter video during this busy
machine's four-second check. No startup or performance guarantee is inferred.
The summary was visually inspected; its final label was readable and the
excluded indicator was absent. The worker exited. The pre-existing normal app
process was left running.

The runtime report is under that identity's
`LocalState/record-spikes/take-ab60bd3bfc6a40468731c595ea794bc9/result.txt`.
The extracted verified package is
`.runtime/record-spikes/package-layout-takefinal1`. Hash evidence is
`.runtime/record-spikes/package-hashes-takefinal1.txt`.

| Artifact | SHA-256 |
| --- | --- |
| MSIX | `17D28943D9A8B469A0D30BCC82D9CFAE38372A8CDACB6A5896F04F5C3EA3554F` |
| NativeAOT app | `9F5B691076B384F0AE227E17FCD8754CE9C77D1A9E353AD376F1A0069844A6AB` |
| Capture helper | `30912FA032B304F49F1778F76F09A896C83FBA76FE6C4658D4B1487BA29B1BD6` |

## Target evidence and remaining hand checks

The independent record-toggle update passed 1526 Windows core tests and six
focused Boat checks. Its NativeAOT publish log is
`.runtime/record-spikes/publish-record-toggle-settings.log`. Signed development
package `1.0.1.17` was installed over the existing identity and opened. Existing
settings, history, and usage hashes matched immediately after installation.
The new Settings switch was then enabled through the actual app UI; disk
readback confirmed `RecordToggleMode=true` and the existing voice activation
and shortcut values. Physical Win+Alt and laptop touchpad behavior remain hand
checks.

This validation exposed a saved-settings load failure when record fields were
absent. The loader now supplies the record-shortcut and video-link defaults at
that boundary. A regression preserves the existing voice shortcut and microphone
boost while loading a document without record fields. The app subsequently
loaded its existing preferences without the fallback warning.

[Clipboard results](record-chord-clipboard-results.md) record at least 16
synthetic files in Claude desktop and eight in chatgpt.com and Copilot desktop.
Separate file and text pastes preserved both. The user separately confirmed
matching web paste behavior. Per-cloud attachment counts, paced bitmaps,
larger files, repeated fallback reliability, and all-target maxima remain
unmeasured. The eight-picture cap stays provisional.

Computer Use stopped while opening the Copilot agents page because it could
not determine the current Windows browser URL confidently enough to enforce
policy. No further browser automation bypassed that stop. No test message was
sent. The earlier unsent synthetic drafts remain as documented in the target
report.

Still pending are physical hold/latch and Alt menu masking, physical ink/ping
and Escape, multi-monitor input blocking and scaling, five-minute takes,
monitor removal, capture-border appearance, and real full-bundle insertion
through the app's exact-text verifier in every target. The silent recording
and ink fixtures do not replace those checks. The overhead comparison remains
[inconclusive for production performance](record-chord-overhead.md).

## Reproduce the isolated package check

Run `tools/Publish-Ansible.ps1`, then:

```powershell
.\tools\record-spikes\encoder\Invoke-PackagedEncoderSpike.ps1 `
  -Msix .\Ansible\bin\AppPackages\Ansible_1.0.1.14_x64_Test\Ansible_1.0.1.14_x64.msix `
  -Take -RunId <fresh-alphanumeric-id>
```

This registers a separate developer package identity. It does not replace the
installed app or change certificate trust. Its explicit four-second entry point
uses `WindowsRecordTakeFactory`, blocks mouse input, and saves only local PNG/MP4
evidence under that identity's private `LocalState/record-spikes` folder. It opens
no microphone and sends no paste or chat message. `result.txt` records NativeAOT
status, released ownership, PNG dimensions, startup offset, decoded frame count,
and audio stream count. Capture screens remain ignored.
