# Use Ansible

See the [README](../README.md#build-and-run) to build and launch the app.

## First launch

A new install opens a three-step welcome lesson. An existing `settings.json`
that has no welcome flag is treated as already complete, so current installs
are not forced through it. **Skip for now** and finishing the lesson both save
completion in settings.

1. Intro. Speak into a focused field. Recognition stays on this device.
2. Shortcut. An original Windows keyboard illustration highlights your chord
   and animates those keys. If Windows animations are off, the keys stay still.
3. Practice. Click the practice field, then use the shortcut. Final words go
   only into that field, not the clipboard or another app. A successful
   practice still stores in local dictation history.

Practice needs microphone permission and an installed microphone-capable model.
**Set up a speech model** opens Speech models. Returning to Home resumes the
lesson until you finish or skip. Home **Learn your shortcut** replays it from
the first step. Change the chord later in Settings.

## Close to the notification area

Closing the main window with X or Alt+F4 hides it to a Windows notification-area
Ansible icon. Recognition, global dictation shortcuts, downloads, and meetings
keep running. Closing the window never starts recording.

Click the icon to reopen the main window. Right-click it for **Open Ansible** or
**Exit**. **Exit** performs the existing coordinated shutdown. It stops active
capture and recognition and joins owned work.

If the tray icon cannot be created, the window stays visible and Ansible reports
an error instead of hiding. Later close attempts retry tray creation. Windows may
put the icon in the hidden-icons overflow.

## Home and appearance

**Home** shows the latest successful dictations, newest first, grouped as Today,
Yesterday, and older dates. Each row shows the time, transcript, **Copy
transcript**, and a **More** menu with **Remove from history**. Remove deletes
that entry from saved history. Removing the current dictation's row also hides
its transcript card. Shortcut dictations keep an insertion tag across restarts:
**Inserted** when the target field confirmed the text, **Not verified** when
the app accepted it without confirmation, **Partially inserted**, or **Not
inserted**. Failed injected-key release also shows **Not verified**. Check the
keyboard and destination field. None, some, or all of the text may have
arrived. The app never retries that uncertain input automatically. In-app
dictations have no tag. History reloads after restart. It keeps at most 100
successful dictations within a 500,000-character text budget. Large entries can
make fewer than 100 fit. An entry larger than that budget stays in the
current-result area only. Search covers every retained entry. WAV results stay
outside dictation history and remain in the separate transcript area. Audio is
never saved. Prior session-only transcripts from earlier app versions cannot be
recovered. **Learn your shortcut** reopens the welcome lesson from the first
step.

Use **Dictate** or your configured shortcut to record. Choose the active model
and use **Transcribe WAV file** on **Models**.
Insights show measured local counts, not estimates. The large number is
all-time dictated words on this device. Words per microphone minute, dictation
count, and microphone minutes sit with it. Pace and microphone time include
pauses. File transcription is excluded from those dictation totals and from
pace. Missing or unreadable usage stays unknown (`--`), not zero. A loaded
empty file can show zero. Unknown word counts stay unknown and blank the word
total and pace. There is no time-saved figure. Activity is a dotted 24-hour
clock of completed dictations by local completion hour over the last 7 or 30
calendar days, including today. Week and Month stay. The center is the period
total. Marks are 00, 06, 12, and 18. Accent dots scale to the busiest hour.
Hover an hour for its actual count. Files are not included. **Most-used models** counts dictation and WAV sessions
together, all time. A failed or overflowed background usage write appears
there as a warning; it does not delay transcript insertion. History and local
usage counts are separate. Turning off usage statistics does not change saved
dictation history.

Ansible uses a Mica window base and follows Windows light, dark, and High Contrast appearance. Pages sit
directly on that base; each section is a title above one borderless tonal card, and High Contrast adds
outlines. The default
Ink / Citron identity matches the Pencil design. Additional color schemes are
prepared through shared brand tokens; a scheme picker is not yet available.
Compact windows use an icon sidebar, smaller page padding, and stacked controls.

## Microphone access

Ansible checks microphone permission at launch, before recording can start. If
Windows requires consent, the app requests it then. Already granted access does
not prompt again. If Windows blocks access, a compact overlay offers **Open
settings** and **Check again** without changing page height. Dismiss it to reveal
the content underneath. Open **Settings** in Ansible to show the guidance again.
Enable the applicable microphone
access settings, then return to Ansible. Policy restrictions may require your
administrator. File transcription and model management remain available.

This check does not open a microphone or capture audio. Recording checks access
again to catch permission changes, without opening another consent prompt.
Older Windows versions without the capability-request API show Settings guidance
when access cannot be confirmed. Camera, screen-recording, and calendar access
are not requested because these features do not use them.

## Choose a model

Speech model cards keep their catalog order while files are verified and when
you select or download a model. Compact windows show one column.

Open **Speech models**. Select **Download** on a model card, or open **Model folder**
and use **Choose folder** to select a folder containing catalog weights. Select
**Use** on an installed model to make it active.

Downloads start only when requested. **Pause** keeps partial files for **Resume**.
The app verifies the length and SHA-256 before installing weights. If recognition
is active when verification finishes, use **Install verified model** after it
ends. You can dictate with an existing model during a download.

**Model folder > Verify installed files** checks the chosen folder without downloading.
Use the model card's remove icon to remove weights or discard a partial download;
both require confirmation. Installation, removal, and folder changes are available
only while recognition is idle. Removing the last model leaves the app disconnected
until another model is installed.

If a model installs but its resume data cannot be removed, the card reports
that cleanup failed. The verified model remains installed. Choose **Model folder >
Verify installed files** to retry cleanup without downloading or removing the
weights. An interrupted resume-data update never replaces the previous valid
metadata with a partially written file.

Do not run `Setup-AudioBackend.ps1` against the selected model folder while the
app is downloading or changing weights. Setup uses a separate download protocol;
its partial files cannot be resumed by the app.

The app remembers the selected model by ID. If none is saved and
`nemotron-asr-0.6b-q8` is installed, dictation uses it. Default setup installs
that ASR and `nemotron-3-diarization-q8`, so a fresh `.runtime\models` folder
already has the unconfigured dictation default. If the saved choice is missing
from the chosen folder, the app warns and uses the first available ASR model
for that session. The saved choice is retained until you explicitly select
another model.

`nemotron-3-diarization-q8` is in the catalog and can be downloaded from
**Speech models**. Installed diarization cards show **Meeting companion**. Use
is hidden; the companion cannot be selected for dictation, replay, or WAV
transcription. Installing only that companion leaves dictation disconnected.
There is no meeting model picker. Start honors an explicitly saved **Use**
selection only when that model is meeting-capable streaming ASR. An automatic
dictation default is not a meeting override. Unconfigured meetings use the
companion with installed Nemotron ASR, then installed Kroko, then VibeVoice ASR
Streaming 1.5B, including when the companion is absent.

### Model behavior

These capabilities apply to the pinned audio.cpp backend, not every upstream
implementation. Download sizes, language notes, and licenses appear on model cards
and in the [catalog](../tools/audio-models.json).

| Model ID | Input | Recognition text | Shortcut insertion |
| --- | --- | --- | --- |
| `moonshine-tiny` | Microphone and WAV | Final only; English only | Yes |
| `qwen3-asr-0.6b-q8` | Microphone and WAV | Buffered | Yes |
| `nemotron-asr-0.6b-q8` | Microphone and WAV | Can produce live previews | Yes, after final recognition |
| `kroko-en-64l-q8` | Microphone and WAV | Live English previews; custom hotwords | Yes, after final recognition |
| `vibevoice-streaming-1.5b-q4` | Microphone and WAV | Can produce live previews with annotations | Yes, after final recognition |
| `vibevoice-asr-7b-q8` | Microphone and WAV | Buffered until Finish; final only | Yes, after final recognition |
| `nemotron-3-diarization-q8` | Not a dictation model | Speaker-turn companion; does not transcribe | No |

Live previews can appear in the main transcript, but a short recording may end
before any preview arrives. Nemotron and Kroko shortcut dictation, replay, and
WAV stay plain speech. VibeVoice output can contain speaker labels. Shortcut
insertion uses speech metadata or parses the streaming model's speaker format to
remove labels. Unrecognized output is not inserted. The offline 7B model keeps
microphone audio in memory until Finish, then runs offline recognition; it has no
live preview and still requires substantial free memory. Installed weights do not
prove that a model can load or run on your hardware.

## Dictate into another app

1. Focus an editable field in the destination app.
2. For Ctrl + Win (either Windows key), hold both keys briefly until listening
   starts, then speak. Keep holding, then release to finish. If you already
   saved F9 or another shortcut, hold that instead.
3. Wait for recognition and check the inserted text.

The default activation mode is **Hold to talk**. In **Settings > Activation mode**,
choose **Press to start and stop** to finish with a second shortcut press instead.
Click the shortcut field to record a different key or key combination; Esc
cancels the change. **Reset** restores Ctrl + Win. Saved F9 and other custom
shortcuts stay until you change or reset them. The only modifier-only chord is
Ctrl + Win. Other shortcuts still need a primary key, including F9. A short
Ctrl + Win tap does not start dictation. A quick third key still goes to
Windows and does not activate Ansible. **Press to start and stop** uses that
same brief hold to start, then a second press to finish. It does not require
holding through speech.

After the shortcut is accepted, Ansible opens the microphone immediately and
holds PCM in a bounded in-memory buffer. That audio is not sent to the
recognizer until the destination field check succeeds. If the check is rejected
or fails, capture stops, the buffer is discarded, and nothing is transcribed,
saved to history, or inserted. This removes the wait on that software check. It
does not remove hardware microphone startup delay, and it does not capture
speech spoken before the shortcut. Shortcut dictation inserts authoritative
final speech once, after successful recognition. Cancelling or failing
recognition inserts no text. Held Ctrl, Alt, Shift, or Win keys delay insertion
until released. Model loading and recognition can continue after capture stops.

In **Hold to talk** mode, the microphone stays open for **500 ms after key
release** to capture the end of the phrase. Release can Finish the recording
while the destination check is still pending. Buffered packets are kept if that
check later succeeds. The waveform remains visible until capture stops.
Pressing the shortcut again during that tail keeps the current
recording open. Its next release starts a new 500 ms tail. Cancellation and
window shutdown do not wait for the tail. **Press to start and stop** and manual
Finish keep their immediate-stop behavior.

The waveform pill appears after the microphone opens, including in-app
recording. Until then, accessible status is Starting microphone. The pill shows
microphone energy while capture is active. Every bar
follows the current level through a fixed shape, tallest in the middle, so the
pill breathes with your voice instead of scrolling through time. Loud moments
settle at a bounded rate rather than snapping to silence. The pill hides when
capture stops, including after the hold-to-talk release tail. If stopping fails,
it remains visible until capture is released. It does not show transcript text.
Reduced-motion mode shows the same shape without bar animation.

**Settings > Overlay position** chooses where the pill appears. Looking up the
caret no longer delays the microphone. The pill may first appear at the bottom
centre of the screen, then move under the verified caret when that check
completes. By default it sits just under the text cursor of the destination
field. The cursor position is read from the Windows caret or the field's UI
Automation text provider. The pill does not follow later cursor movement. If
the pill would leave the work area it moves above the cursor line. When Windows
cannot report a cursor position, for dictation started from the app window, or
when the option is off, the pill stays at the bottom centre. The option applies
to the next dictation and persists across restarts.

### Text insertion

Only warning and error notices appear, each limited to the pages for its area.
Capture-privacy failures appear on every page. Routine success and cancel
messages are not shown.

After shortcut insertion, a verified send is not announced. Open **Home** if
delivery could not be confirmed. **Text sent; unverified**
means input was queued but the change could not be verified; check the
destination before inserting again. Partial input and rejected delivery also
produce warnings or errors. A notice stays until dismissed or replaced.

**Settings > Text insertion** offers two methods:

- **Paste (replaces clipboard)** is the default. Ansible puts the speech text on
  the clipboard immediately and sends the selected paste shortcut. The text remains available for
  asynchronous readers, including hosted editors. The previous clipboard content
  is replaced, not backed up or restored. It requests exclusion from Windows
  clipboard history, clipboard monitors, and cloud sync. Publication failures
  are reported and prevent the paste shortcut from being sent.
- **Type characters** sends Unicode keystrokes. **Gap between characters (ms)**
  ranges from 0 to 200, with a default of 20. A zero gap sends a burst that can
  garble text in slow targets such as Notepad. Use paced typing for apps that
  block Ctrl+V.

**Destination paste shortcut** selects **Ctrl+V** (default), **Shift+Insert**, or
**Ctrl+Shift+V**. Choose the chord accepted by the actual editor or terminal;
the choice applies to all paste dictation, not an automatically detected app.
The same application can contain fields with different shortcut behavior.
Paste holds the selected chord briefly before releasing its keys in reverse
order. Ansible never resends the paste to recover a failed key release.

Use **Type characters** to preserve your clipboard or for apps that block paste.
Ansible does not
restrict clipboard readers to the destination window's process, and does not
use clipboard reads or a timeout as proof of insertion. Another app or a later
dictation can replace the clipboard before a delayed paste reads it. Check the
destination before starting another dictation.

Ansible checks the original window and focused native handle before insertion,
and refreshes the field's UI Automation identity before sending input. Losing a
previously available field identity stops insertion rather than downgrading the
check. Some editors
do not expose that identity, so a move between fields in the same window can go
undetected. Known password and read-only fields are rejected. Elevated apps and
the secure desktop are unsupported.

Where the destination exposes usable text and selection through UI Automation,
Ansible compares bounded before/after snapshots to the exact expected insertion.
Those snapshots stay in memory; no field contents are logged or saved. Password
fields are not read. Unsupported, ambiguous or truncated snapshots do not prove
failure and never trigger another insertion.

A verified insertion observed the expected text change in the same field; that
success is not shown as a notice. Unverified, partial, and rejected delivery
still produce warnings or errors and are not automatically retried. Check the
destination before using **Copy** to recover text manually; repeating an
uncertain send can duplicate speech.

## Transcribe without automatic insertion

On **Home**, use **Dictate** to start microphone capture, then
**Finish** to recognize the remaining audio. **Cancel** stops capture and waits for
the current native step; partial text can remain visible for copying. A blocking
native call cannot be forcibly interrupted.

On **Models**, select **Transcribe WAV file** for a recording. **Copy** copies the visible
transcript. These page controls do not automatically insert text into another app.

WAV input is limited to five minutes, 100 MiB on disk, and 64 MiB of decoded float
audio. Unsupported or malformed WAV formats produce an error. App operations
have a five-minute timeout that includes model preparation; native cleanup can
continue after cancellation.

The app preloads the selected microphone-capable model and reuses its native session.
After ten idle minutes it unloads them. Microphone capture begins while the model
loads, including a first Auto qualification, with a bounded backlog so early
speech is kept. A full backlog stops capture with an error rather
than dropping samples. Preloading failures appear as warnings.

## Recognition settings

Change settings when recognition is idle. Preferences return after restart.
Unreadable settings and failed saves produce warnings. If startup cannot read the
settings file, changes apply only to the current session; resolve the read error
and restart before saving preferences.

### Microphone boost

**Microphone boost (dB)** ranges from 0 to 24; 0 is off. Try 6 or 12 for quiet
speech. The next microphone dictation uses it, including audio buffered during
model loading. The waveform shows the boosted signal. WAV transcription does
not use boost.

Boost raises speech and background noise. It does not recover missing audio or
remove noise, and loud samples can clip. Reduce it if speech distorts.

### Language and vocabulary

Leave **Language hint** blank for English-only Moonshine and Kroko. Other models have
model-dependent language support; individual languages are not qualified by this
app. Unsupported native options produce an error.

Open **Dictionary** in the sidebar. **Add new** adds one name, word, or phrase.
**Import** accepts one entry per line. Search filters the list. Each row offers
Edit and Remove. **Undo remove** restores the latest deletion. Existing saved
words stay in the list. Changes save immediately.

Blank lines are removed, whitespace is normalized, and duplicate entries are
merged. Limits are 100 unique entries, 100 characters per entry, and 4 KiB of
UTF-8 text. The page warns if a change could not be saved.

Kroko uses these entries as live English hotwords. Kroko rejects `/` within an
entry because its decoder treats that character as a separator. Saved slash-bearing
entries remain available for other models. With Kroko selected, Dictionary shows
a compatibility warning and recognition stops before opening the microphone.
Edit those entries or choose another model. Qwen3-ASR and both VibeVoice models
accept this context; offline VibeVoice uses it
only for WAV transcription. Moonshine and Nemotron do not use it. The selected
model's support is shown on Dictionary. Each operation uses the
saved list at startup.

Entries are recognition hints, not guaranteed spellings or replacement rules.
The app does not rewrite completed transcripts or text already inserted.

### Recognition processor

**Auto (recommended)** is the default. Settings without a processor field load
Auto. An explicit saved CPU or GPU choice is kept until you change it. Auto
runs CPU, then GPU, sequentially in an isolated `Ansible.DeviceProbe` process
against the bundled public speech sample. It does not use microphone, meeting,
or other user audio, and it does not save that audio. Compared times exclude
loading and warmup and use the median of three runs. Each device has a five-minute deadline that includes load,
warmup, recognition, and teardown. Cancelling stops the worker, then joins and
drains it. Auto measures the mode required for the next work: offline WAV, live
dictation, or meeting history. The first preparation for a model and mode takes
longer. Results are cached for model, native, and sample hashes, hardware and
driver, mode and history, device, threads, and headroom. Malformed cache content
is ignored; Auto qualifies the devices again.
When both devices work
and GPU is within five percent of CPU, Auto selects GPU. Settings shows the last
comparison, including the selected processor and device timings, after a
qualification runs.

A GPU worker crash or timeout disqualifies GPU; a successful CPU result is used.
Shared validation, integrity, memory, protocol, or ownership errors abort
calibration and do not write a device result to the cache. If neither device
works, the app shows an error. Auto does not replay consumed live audio. A
failed live operation reports the error, and the next attempt may use CPU.
CPU fallback requires a successful CPU qualification. A failed cache update
is reported but does not prevent using the qualified device or a qualified
fallback in the current session.
Qualification cannot catch a later in-process native driver crash.

**CPU** and **GPU (Vulkan)** remain available as manual choices. Vulkan uses
device 0 and needs a Vulkan build, a compatible driver, and supported model
operations. Manual GPU failures are shown without an automatic CPU retry.

Auto selects CPU for Nemotron dictation because its GPU streaming path has a
reported native crash that can terminate the app. See [known native limits](native-validation.md#known-limits).
The host-memory guard runs on both backends; it does not guarantee sufficient
GPU memory. The offline 7B model has about 9.86 GB of weights and needs additional
memory to load and decode. Keep the guard enabled.

### Transcript cleanup

Open **Settings > Transcript cleanup**. Choose **Copilot CLI** or **OpenAI API**.
Each provider has its own model and reasoning effort. Automatic cleanup stays
off until you enable it and select **Save cleanup settings**. That save is the
opt-in to send meeting text to the cloud. Enabling OpenAI automatic cleanup
requires a saved API key and an OpenAI API model ID. No OpenAI model is guessed.
Enabling it authorizes sending each new completed meeting transcript to the
selected provider. No extra confirmation appears per meeting.

The selected provider is used alone. There is no fallback to the other provider
or another account. Audio is never uploaded. Only finalized local text is sent,
and only after the original transcript is saved.

**Copilot CLI** uses the installed GitHub Copilot CLI and its signed-in account.
Run `copilot login` if needed. It does not use an OpenAI API key. The default
model is `gpt-6-luna` with `high` reasoning. Cleanup starts a subprocess with
tools, hooks, memory, and custom instructions disabled. That isolation applies
only to the subprocess. It does not change your global Copilot CLI config.
Obsolete `--no-remote` is not passed. `--no-remote-export` is replaced by
`--sync-session off`. If the installed CLI rejects a flag, the app reports that
the CLI is incompatible and keeps the original transcript. CLI errors are
classified. Raw stderr is not shown.

**OpenAI API** posts transcript text to the OpenAI Responses API with `store`
false. Enter a valid OpenAI API model ID from your OpenAI account. Copilot
aliases are not mapped. Save the key with **Save API key**. The key is stored
in Windows Credential Locker. It is not written to `settings.json` or logs.
OpenAI API billing is separate from Copilot. Remove the saved key with
**Remove saved key**.

The meeting snapshots the cleanup provider at Start. Automatic cleanup runs
only after a successful **End meeting** when that snapshot has automatic cleanup
on. Failed, discarded, or empty meetings are not sent. Reopening a saved meeting
does not send text. **Exit** cancels pending cleanup, waits for it to
stop, and does not start a new cloud cleanup during shutdown. **Clean up wording**
asks you to confirm the currently saved Settings provider and model. It does not
edit those values in the meeting window. **Restore original** remains. Failed,
cancelled, or partial cleanup keeps the original wording. Cleanup success and
failure are separate from recognition errors. Review model output. It can be wrong.

## Meeting transcripts

Automatic Teams detection is enabled by default while Ansible is open. After
stable microphone activity, a prompt in the main window offers **Open notetaker**
with Teams selected. Click **Start** in that window to capture audio. Detection
reads Windows audio-session metadata, never microphone samples. Microphone use
can include a Teams test call or another audio workflow; it is not proof of a
scheduled meeting. Muted calls without an active capture session may be missed.
Dismissal suppresses the same process until its activity has ended for ten
seconds. Disable **Suggest notetaking when Teams uses the microphone** in
Settings. Other apps still start
manually; detection does not fetch calendar events or automatically end recording.

Open **Meetings > Start meeting**, then start. Start snapshots the recognition
processor, language hint, and vocabulary for that meeting. After Start, the
window shows **Preparing**. Wait until it shows **Transcribing** before speaking.
A cold Auto run benchmarks and loads the meeting model before capture opens, so
the 30-second recognition backlog does not fill during qualification. That first
preparation can take longer than later meetings that reuse a cached device
choice. The app then records the Windows default microphone plus everything
playing on every active speaker and headphone device. This includes
notifications and other apps, not only the meeting. Devices that appear during
the meeting join within about two seconds. Devices that another app holds in
exclusive mode are skipped. Use headphones: echo cancellation is not
implemented.

The optional **Teams app for speaker names** choice is used only for automatic
speaker naming. It does not limit what audio is recorded. The lookup is
read-only Windows accessibility. It does not change Teams settings. Teams process audio
can be silent to Windows application loopback even when you hear the call,
which is why recording uses output devices instead.

While recording, the window shows a microphone line and a **Speakers and
headphones** line with the device count. Each line uses one of
**Audio detected**, **No audio packets**, **No recent audio packets**, or
**Quiet**. Detected means RMS energy at or above 0.001 (-60 dBFS) in the last
five seconds. That is not speech detection and does not mean words were
recognized.

After 15 seconds with no playback signal, an inline hint appears. Silence can
be normal when others are quiet. If no output device can be recorded, only the
microphone is transcribed until one becomes available. **More > Open audio
diagnostics folder** opens the local metadata logs described under [Local data
and privacy](#local-data-and-privacy), if any have been saved. It does not create
the folder.

| What you see | Reading |
| --- | --- |
| Microphone **Audio detected**, speakers **No audio packets** | No WASAPI packets from any output device |
| Microphone **Audio detected**, speakers **Quiet** | Packets arrived below the energy threshold |
| Speakers **Audio detected**, little or no transcript | Capture has energy. Recognition or mixing may still be failing |
| **No output device available** | Every output device was missing or in exclusive use |

Default setup installs Nemotron ASR and Nemotron 3 Diarization. The Meetings
window does not offer a model picker. If you have explicitly selected a
meeting-capable streaming model with **Use** (Nemotron ASR, Kroko English, or
VibeVoice ASR Streaming 1.5B), Start uses that ASR. Explicit Nemotron or Kroko
requires the installed companion; a missing companion stops Start with an error
rather than switching models. An automatic dictation default is not a meeting
override. When no such selection is saved, an unconfigured meeting uses that
Nemotron pair when both are installed, then installed Kroko with the same
companion, then VibeVoice ASR Streaming 1.5B, including when the companion is
absent. Ordinary dictation, replay, and WAV stay plain ASR. The companion is
never a dictation choice.

Meetings use the recognition processor (Auto, CPU, or GPU), language hint, and
vocabulary snapshotted at Start. Custom vocabulary applies only when the meeting
ASR supports it. Later setting changes do not affect an active meeting.
Dictation and model changes are unavailable while a meeting owns the recognition
resources. Auto qualification for a paired meeting admits both weights together
and requires speaker activity on the public sample. It also compares the
selected device's measurement with the sample's duration: a candidate slower
than 0.50x real time cannot keep up with a live meeting, so an unconfigured
meeting moves on to the next candidate (paired Kroko, then VibeVoice) and the
Meetings window shows a **Speaker model changed** notice naming the skipped
model, its measured pace, and the model in use. A manual processor choice
skips that check. The caption shows the measured pace of the chosen model.

Text appears throughout the meeting as native chunks finish. Paired Nemotron or
Kroko meetings publish cumulative timed anonymous speaker turns (`Speaker 0`
through `Speaker 7`). The diarizer works in four-second chunks, so the newest
words can show **Unknown speaker** for up to about five seconds before their
label arrives. Overlapping speech and words with no speaker activity near them
stay **Unknown speaker**. Kroko words that run through a pause are judged by
the speech where they begin, so the last word before a pause stays with its
speaker when someone else speaks next.
VibeVoice meetings keep that model's speaker labels. Rename a speaker by
selecting its heading. When Teams is selected, the app reads visible
participant tiles through Windows accessibility. It looks for explicit speaking
markers on named tiles or their visible descendants. Current Teams labels such
as **video is on**, **video is off**, **Myself video**, mute state, and
**Has control of production tools** are tile descriptions, not names. Names that
contain commas stay intact. Unmuted, video on, and roster presence do not prove
speaking. Mapping still requires two consistent, nonoverlapping final turns with
an exclusive microphone/playback source and full observation coverage.
Ambiguous evidence retains model labels. Manual names take precedence. Other
meeting apps retain editable model labels. Names are not inferred from
transcript text. Popups, minimized or hidden tiles, unsupported layouts, and
Teams windows that omit speaking metadata can prevent automatic names. Live
mapping on a real call is not yet established. No voiceprints or calendar-name
guesses are used.

Recognition chunks are not speaker turns. The window joins adjacent chunks from
the same speaker into one text block. Each block has the speaker heading, rename
control, and combined time range. A speaker change starts a new block. If the
same speaker returns after someone else, that later speech is a new block.
Saved meetings keep each original chunk's wording and timing. Cleanup still
aligns those saved turns. One **Original wording** expander covers the grouped
block. If any joined chunk is untimed or **Unknown speaker**, the block is not
shown as fully ASR-aligned.

Install **Nemotron ASR** weights to enable optional timing refinement on CPU
for meetings that still publish untimed VibeVoice turns.
It compares recognized token anchors with finalized, unchanged VibeVoice turns in a rolling
30-second audio buffer held only in memory. Confident matches show **ASR-aligned**;
missing or repeated phrases retain **approximate chunk time**. English digits
can match spoken quantities without changing wording.
Long turns can use unique endpoint phrases with at least 80% word agreement;
previously aligned spans cannot be reused by another turn. These are ASR token
anchors, not precise acoustic word boundaries. Missing weights, insufficient
memory, or other optional naming and refinement failures leave editable speaker
labels in place and write a trace warning; they do not add diagnostic status
cards. Chunk timestamps use native input sample positions; audio during pauses
is excluded. Update latency and long-session speaker continuity remain
unqualified.

**Pause** stops capture and transcript updates. **Resume** continues the same
meeting and processes retained pending speech. Speech during the pause is not
captured. **End meeting** stops capture and processes pending chunks before
releasing resources; the window stays open for review. Closing an active meeting
offers finishing or explicitly discarding pending speech. Hiding the main window
does not end the meeting. **Exit** finishes an active meeting before shutting
down. If the meeting is still preparing, closing that meeting window cancels
preparation and waits for startup and any opened capture to stop before closing.
**Exit** also cancels preparation and waits for that cleanup. If shutdown fails, the app
waits for owned shortcut and delivery work to stop before allowing another
recording; the shutdown error remains visible.

Transcripts and title/speaker edits save automatically. Live updates save in the
background, keeping only the newest pending snapshot when a write is already
running. There is no intentional save delay. Pause, End meeting and Close wait
for the required save to finish. A crash can lose updates still awaiting disk;
slow storage can delay those actions. Save failures remain visible and closing
can be retried. Failed wording cleanup keeps the completed original for retry.
The Meetings list supports
reopening saved text; each transcript window offers Copy, text export, and Delete.
If a meeting fails before any speech is transcribed, its window restores the
**Teams app for speaker names** choice and offers **Try again** after resources are released.
Resolve the displayed error before retrying. The failed attempt stays saved.
Capture is limited to two hours. A full 30-second recognition queue or capture
failure stops the meeting with an error and keeps text already produced. Native
steps must finish before resources can be released. After a crash, previous
unfinished meetings are marked Interrupted; unfinished speech cannot be recovered
because audio is never written to disk.

Meetings saved before automatic speaker naming remain readable. Unreadable
transcripts appear separately in **Meetings**; dictation, healthy transcripts,
and new meeting capture remain available. Choose **Export and remove** to save
the original JSON file to a location outside Ansible's meeting folder. Ansible
removes its copy only after the export succeeds. Cancelling or a failed export
keeps the app's copy. The export preserves the original file, including any
format errors; it does not repair the transcript or contain audio. A failure
accessing the meeting folder itself can still prevent capture.

After a successful **End meeting**, automatic wording cleanup runs only when the
Start snapshot had it enabled. See [Transcript cleanup](#transcript-cleanup).
The original transcript is saved locally first. Then finalized text is sent to
the selected provider. Failed, discarded, or empty meetings are not sent.
**Clean up wording** confirms the currently saved Settings provider and model.
It does not edit those values in the meeting dialog. Cleanup preserves speaker
labels and timing and supports **Restore original**. Failed, cancelled, or
incomplete cleanup keeps the original. Cleanup status is separate from
recognition failure. Reopening a saved meeting does not send text. **Exit**
cancels pending cleanup, waits for it to finish, and does not start a new cloud
cleanup during shutdown. Copilot or OpenAI can still retain sent text
under their own policies.

## Local data and privacy

**Settings > Screen capture privacy** can exclude the floating dictation overlay
and meeting windows from supported Windows screenshots and screen sharing.
The option is off by default, persists across restarts, and applies to open and
new windows, including during a recording. The main window stays visible.
Windows 10 version 2004 or later is required. Some capture tools do not honor
Windows capture exclusion; this is not protection against every recorder or a
camera. The app reports an app-wide warning if Windows cannot apply or verify
the setting.

Captured audio always stays in memory and is never saved or uploaded.
Successful dictation transcripts persist as text and metadata in
`dictation-history.json`. Meeting transcripts, titles, and speaker names save
locally. Recognition does
not upload audio. Auto device qualification uses only the bundled public sample
in a local process; it does not send or save user audio. Optional transcript
cleanup sends meeting text only after you enable it in **Settings > Transcript
cleanup** and a completed original is saved locally. Choose Copilot CLI or the
OpenAI API. There is no provider fallback. Audio is never uploaded. The OpenAI
API key is stored in Windows Credential Locker, not in `settings.json` or logs.
Model downloads also use the network.
**Copy** uses the ordinary clipboard; Windows clipboard features and destination
apps can retain text you copy or insert. Automatic shortcut paste leaves dictated
text on the clipboard until replaced. It requests
clipboard-history and cloud-sync exclusions, but those requests are not a storage
guarantee for other apps.

Per-user files are in Windows package storage, normally
`%LOCALAPPDATA%\Packages\<package-family-name>\LocalState`:

| Path | Contents |
| --- | --- |
| `settings.json` | Preferences, chosen model folder, custom vocabulary, transcript cleanup provider settings, and whether the first-launch lesson is complete. The OpenAI API key is not stored here |
| `dictation-history.json` | Latest successful dictation transcripts, metadata, and insertion status; no audio |
| `device-benchmarks.json` | Cached Auto device timings for model, runtime, hardware, and mode; no user audio |
| `usage.json` | Session IDs, completion timestamps, model IDs, session type, speech-word counts, and durations |
| `meetings\<id>.json` | Titles, dates, transcript turns, timing quality, manual/automatic names, and original wording after cleanup; no audio |
| `diagnostics\local-diagnostics.jsonl` | Opt-in operational metadata beside `settings.json`. Rotates to `local-diagnostics.jsonl.previous` at 1 MiB. No audio or transcript text |
| `models\` | Default location for downloaded model weights |

If `dictation-history.json` is corrupt or unreadable, Ansible leaves the file
unchanged, shows a warning, and keeps new rows in memory until storage can be
recovered.

Enable **Local diagnostics** in Settings to record operational metadata. It is
off by default and independent of **Usage statistics**. Turning it off discards
queued records and stops new collection. A write already in progress may
finish. Existing files are not deleted. Earlier app versions may have written
`startup-error.log` or `meeting-capture.jsonl` without this setting. Those files
and their previous rotations are not removed or counted toward the new log's limit.

Logs stay under `diagnostics\`. The current file is
`local-diagnostics.jsonl`. When it is at least 1 MiB, the next append rotates it
to `local-diagnostics.jsonl.previous`. Only those two files are kept, so total
size can exceed 2 MiB by bounded record overhead. Rotation is size-based, not a time
retention promise. The JSONL never includes audio, transcript text, participant
names, window titles, device names, model file paths, or raw exception
messages. It includes timestamps, a random capture ID (new at capture
start and resume), active, skipped, and
lost output-device counts, kind/stage names, HRESULT, packet and sample counts, WASAPI silent-sample
counts, synthetic missing mixed samples, input and mixed RMS maxima, last-packet
and last-signal ages, and about every five
seconds the downstream captured, consumed, and processed seconds plus a
recognition-update count. Kind, stage, and pipeline phase
serialize as readable names, not numbers. Each source may include a bounded
`LastPacketTiming` sample with timestamp sample, expected sample, packet sample
count, current and previous device position, and native flags. Those values are
numeric metadata only. A logging write failure does not stop capture. The
meeting window shows that diagnostics error instead. Dropped events are counted
in later records.

Session operation records include a run ID, package version, operation ID,
known catalog model ID, activity, outcome,
and monotonic durations in milliseconds. Microphone and stream readiness are
measured from operation admission, not the shortcut press. The first-partial
and finish-to-result durations are null when unavailable. The terminal duration
includes resource cleanup. These records do not yet cover every app workflow.
They include the requested backend and a numeric error code when available.
Native selection records include the selected backend, selection source,
session reuse, and durations when those facts are available.
Meeting lifecycle records link start, model-family selection, pause, resume,
availability of optional naming and timing refinement, and terminal outcome to
one operation ID. Pipeline records carry that ID when capture restarts during
the same enabled diagnostics interval. Opt-in terminal records may include
Application stages `CleanupCopilot` and `CleanupOpenAI` with outcome, duration,
and a numeric HRESULT when available. They never include transcript text, API
keys, or raw CLI stderr.

Shortcut delivery records describe the insertion outcome, method, paste chord,
typed reason, time after recognition, and time deferred for modifier release.
Shortcut records include admission rejections and link admitted attempts to
the operation. They do not
include destination or clipboard contents. Microphone capture summaries include
packet and sample counts, maximum callback gap, backlog high-water mark, and
stop and release status. Model download summaries distinguish transfer,
verification, and installation. They include byte counts and HTTP status when
available, but not download addresses. Permission status, hotkey registration,
startup, and shutdown outcomes are recorded after diagnostics are enabled.
Settings save, usage and history load and save, and meeting load, save, and
flush outcomes include durations and numeric error codes.

**Insights** uses completed sessions. Failed and cancelled sessions are excluded.
Unknown speech-word counts remain unknown and are excluded from word totals and
pace. Missing or unreadable usage stays unknown rather than showing invented
zeros. Dictation pace includes microphone pauses; file transcription is
excluded from pace. The activity clock groups completed dictations by local
hour over the selected 7 or 30 days. Files are not included. Model
distribution includes dictation and WAV sessions. The app does not compute
time saved. Completed sessions are queued for local writes, at most 32 pending,
so insertion is not delayed. If a write fails or that queue overflows, Insights
shows the error; a later successful write does not recover a lost entry.
Shutdown drains and joins remaining writes. Disable **Usage statistics** in
Settings to stop collection without deleting prior records. The app has no
external usage telemetry.

Changing model folders does not move or delete weights. Package updates preserve
local data; uninstalling removes package-local data, including models in the
default folder. Models in a chosen folder outside package storage remain there.
Use **Speech models > Model folder > Choose folder** to reuse development weights
from `.runtime\models`.
