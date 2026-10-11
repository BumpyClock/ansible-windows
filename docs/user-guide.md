# Use Ansible

See the [README](../README.md#build-and-run) to build and launch the app.

## Microphone access

Ansible checks microphone permission at launch, before recording can start. If
Windows requires consent, the app requests it then. Already granted access does
not prompt again. If Windows blocks access, the main window shows **Open
microphone settings** and **Check again**. Enable the applicable microphone
access settings, then return to Ansible. Policy restrictions may require your
administrator. File transcription and model management remain available.

This check does not open a microphone or capture audio. Recording checks access
again to catch permission changes, without opening another consent prompt.
Older Windows versions without the capability-request API show Settings guidance
when access cannot be confirmed. Camera, screen-recording, and calendar access
are not requested because these features do not use them.

## Choose a model

Open **Speech models**. Select **Download** on a model card, or open **Model folder**
and use **Choose folder** to select a folder containing catalog weights. Select
**Use** on an installed model, or select it from **Speech model** on the Dictation page.

Downloads start only when requested. **Pause** keeps partial files for **Resume**.
The app verifies the length and SHA-256 before installing weights. If recognition
is active when verification finishes, use **Install verified model** after it
ends. You can dictate with an existing model during a download.

**Model folder > Verify installed files** checks the chosen folder without downloading.
Use the model card's remove icon to remove weights or discard a partial download;
both require confirmation. Installation, removal, and folder changes are available
only while recognition is idle. Removing the last model leaves the app disconnected
until another model is installed.

The app remembers the selected model by ID. If it is missing from the chosen
folder, the app warns and uses the first available model for that session. The
saved choice is retained until you explicitly select another model.

### Model behavior

These capabilities apply to the pinned audio.cpp backend, not every upstream
implementation. Download sizes, language notes, and licenses appear on model cards
and in the [catalog](../tools/audio-models.json).

| Model ID | Input | Recognition text | Shortcut insertion |
| --- | --- | --- | --- |
| `moonshine-tiny` | Microphone and WAV | Final only; English only | Yes |
| `qwen3-asr-0.6b-q8` | Microphone and WAV | Buffered | Yes |
| `nemotron-asr-0.6b-q8` | Microphone and WAV | Can produce live previews | Yes, after final recognition |
| `vibevoice-streaming-1.5b-q4` | Microphone and WAV | Can produce live previews with annotations | Yes, after final recognition |
| `vibevoice-asr-7b-q8` | Microphone and WAV | Buffered until Finish; final only | Yes, after final recognition |

Live previews can appear in the main transcript, but a short recording may end
before any preview arrives. VibeVoice output can contain speaker labels. Shortcut
insertion uses speech metadata or parses the streaming model's speaker format to
remove labels. Unrecognized output is not inserted. The offline 7B model keeps
microphone audio in memory until Finish, then runs offline recognition; it has no
live preview and still requires substantial free memory. Installed weights do not
prove that a model can load or run on your hardware.

## Dictate into another app

1. Focus an editable field in the destination app.
2. Hold F9 while speaking, then release it to finish capture.
3. Wait for recognition and check the inserted text.

The default activation mode is **Hold to talk**. In **Settings > Activation mode**,
choose **Press to start and stop** to finish with a second shortcut press instead.
Click the shortcut field to record a different key or key combination; Esc
cancels the change. **Reset** restores the default shortcut.

Shortcut dictation inserts authoritative final speech once, after successful
recognition. Cancelling or failing recognition inserts no text. Held Ctrl, Alt,
Shift, or Win keys delay insertion until released. Model loading and recognition
can continue after capture stops.

The waveform pill shows microphone energy while capture is active. It hides when
capture stops, including when the hold-to-talk key is released. It does not show
transcript text. Reduced-motion mode uses a level meter.

### Text insertion

**Settings > Text insertion** offers two methods:

- **Paste (replaces clipboard)** is the default. Ansible puts the speech text on
  the clipboard immediately and sends Ctrl+V. The text remains available for
  asynchronous readers, including hosted editors. The previous clipboard content
  is replaced, not backed up or restored. It requests exclusion from Windows
  clipboard history, clipboard monitors, and cloud sync. Publication failures
  are reported and prevent the paste shortcut from being sent.
- **Type characters** sends Unicode keystrokes. **Gap between characters (ms)**
  ranges from 0 to 200, with a default of 20. A zero gap sends a burst that can
  garble text in slow targets such as Notepad. Use paced typing for apps that
  block Ctrl+V.

Use **Type characters** to preserve your clipboard or for apps that block Ctrl+V,
including terminals that require a different paste shortcut. Ansible does not
restrict clipboard readers to the destination window's process, and does not
use clipboard reads or a timeout as proof of insertion. Another app or a later
dictation can replace the clipboard before a delayed paste reads it. Check the
destination before starting another dictation.

Ansible checks the original window and focused native handle before insertion.
When available, it also checks the field's UI Automation identity. Some editors
do not expose that identity, so a move between fields in the same window can go
undetected. Known password and read-only fields are rejected. Elevated apps and
the secure desktop are unsupported.

If focus changes or insertion fails, the app does not retry a potentially partial
send. Check the destination, then use **Copy** on the Dictation page to recover
the text manually. Windows cannot confirm that an app accepted typed characters.

## Transcribe without automatic insertion

On **Dictation**, use the round record button to start microphone capture, then
**Finish** to recognize the remaining audio. **Cancel** stops capture and waits for
the current native step; partial text can remain visible for copying. A blocking
native call cannot be forcibly interrupted.

Select **Transcribe WAV file…** for a recording. **Copy** copies the visible
transcript. These page controls do not automatically insert text into another app.

WAV input is limited to five minutes, 100 MiB on disk, and 64 MiB of decoded float
audio. Unsupported or malformed WAV formats produce an error. App operations
have a five-minute timeout that includes model preparation; native cleanup can
continue after cancellation.

The app preloads the selected microphone-capable model and reuses its native session.
After ten idle minutes it unloads them. Microphone capture begins while the model
loads, with a bounded backlog. A full backlog stops capture with an error rather
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

Leave **Language hint** blank for English-only Moonshine. Other models have
model-dependent language support; individual languages are not qualified by this
app. Unsupported native options produce an error.

Under **Custom vocabulary**, enter one name, word, or phrase per line and select
**Save dictionary**. Save an empty list to clear it. Blank lines are removed,
whitespace is normalized, and duplicate entries are merged. Limits are 100 unique
entries, 100 characters per entry, and 4 KiB of UTF-8 text.

Qwen3-ASR and both VibeVoice models accept this context; offline VibeVoice uses it
only for WAV transcription. Moonshine and Nemotron do not use it. The selected
model's support is shown beside the vocabulary field. Each operation uses the
saved list at startup.

Entries are recognition hints, not guaranteed spellings or replacement rules.
The app does not rewrite completed transcripts or text already inserted.

### Recognition processor

CPU is the default. **GPU (Vulkan)** selects device 0 and requires a Vulkan build,
a compatible driver, and supported model operations. Failures are shown without
an automatic CPU retry. Select CPU explicitly to retry.

Use CPU for Nemotron dictation: its GPU streaming path has a reported native
crash that can terminate the app. See [known native limits](native-validation.md#known-limits).
The host-memory guard runs on both backends; it does not guarantee sufficient
GPU memory. The offline 7B model has about 9.86 GB of weights and needs additional
memory to load and decode. Keep the guard enabled.

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

Open **Meetings > Start meeting**. Select the meeting application, or explicitly
choose **All system audio**, then start. The app captures the Windows default
microphone and playback together. Application capture includes its child-process
tree; a browser selection can include audio from other tabs. Windows build 20348
or later is required for application capture. Use headphones: echo cancellation
is not implemented.

Install **VibeVoice ASR Streaming 1.5B Q4_K** in **Speech models** first. Meetings
use this model independently of the dictation selection, with the configured
CPU/Vulkan backend, language hint, and custom vocabulary. Dictation and model
changes are unavailable while a meeting owns the recognition resources.

Text appears throughout the meeting as native chunks finish. Speaker labels come
from VibeVoice; rename a speaker by selecting its heading. When Teams is selected,
the app reads visible speaking markers through Windows accessibility. Names require
two consistent, nonoverlapping final turns with an exclusive microphone/playback
source and full observation coverage. Ambiguous evidence retains model labels;
manual names take precedence. Other meeting apps retain editable model labels.
Hidden windows, unsupported layouts, and unsupported marker languages can prevent
automatic names. No voiceprints or calendar-name guesses are used.

Install **Nemotron ASR** weights to enable optional timing refinement on CPU.
It compares recognized token anchors with finalized, unchanged VibeVoice turns in a rolling
30-second audio buffer held only in memory. Confident matches show **ASR-aligned**;
missing or repeated phrases retain **approximate chunk time**. English digits
can match spoken quantities without changing wording.
Long turns can use unique endpoint phrases with at least 80% word agreement;
previously aligned spans cannot be reused by another turn. These are ASR token
anchors, not precise acoustic word boundaries. Missing weights or insufficient
memory disable this optional pass without replacing the speech model. Chunk
timestamps use native input sample positions; audio during pauses is excluded.
Update latency and long-session speaker continuity remain unqualified.

**Pause** stops capture and transcript updates. **Resume** continues the same
meeting and processes retained pending speech. Speech during the pause is not
captured. **End meeting** stops capture and processes pending chunks before
releasing resources; the window stays open for review. Closing an active meeting
offers finishing or explicitly discarding pending speech. Closing the main app
finishes its active meeting before shutting down.

Transcripts and title/speaker edits save automatically. The Meetings list supports
reopening saved text; each transcript window offers Copy, text export, and Delete.
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

After **End meeting**, **Clean up wording** can send transcript text to GitHub
Copilot CLI. Install and authenticate Copilot CLI first if prompted. Choose the
model and reasoning effort in the dialog; defaults are `gpt-6-luna` and `high`.
Cleanup preserves speaker labels and timing, keeps original wording, and supports
**Restore original**. Model output can be wrong; review the result. Failed or
cancelled requests keep the existing transcript. CLI requests run with tools,
hooks, memory, and custom instructions disabled, in temporary app-owned state
directories removed after the process exits. Copilot's service can still retain
requests under its own policies; local session isolation does not change those policies.

## Local data and privacy

**Settings > Screen capture privacy** can exclude the floating dictation overlay
and meeting windows from supported Windows screenshots and screen sharing.
The option is off by default, persists across restarts, and applies to open and
new windows, including during a recording. The main window stays visible.
Windows 10 version 2004 or later is required. Some capture tools do not honor
Windows capture exclusion; this is not protection against every recorder or a
camera. The app reports a warning if Windows cannot apply or verify the setting.

Captured audio always stays in memory. Dictation transcripts stay in memory;
meeting transcripts, titles, and speaker names save locally. Recognition does
not upload audio. Optional Copilot cleanup uploads transcript text after the
explicit **Send text and clean up** action. Model downloads also use the network.
**Copy** uses the ordinary clipboard; Windows clipboard features and destination
apps can retain text you copy or insert. Automatic shortcut paste leaves dictated
text on the clipboard until replaced. It requests
clipboard-history and cloud-sync exclusions, but those requests are not a storage
guarantee for other apps.

Per-user files are in Windows package storage, normally
`%LOCALAPPDATA%\Packages\<package-family-name>\LocalState`:

| Path | Contents |
| --- | --- |
| `settings.json` | Preferences, chosen model folder, and custom vocabulary |
| `usage.json` | Session IDs, completion timestamps, model IDs, session type, speech-word counts, and durations |
| `meetings\<id>.json` | Titles, dates, transcript turns, timing quality, manual/automatic names, and original wording after cleanup; no audio |
| `models\` | Default location for downloaded model weights |
| `startup-error.log` | Startup failure diagnostics, when a startup error occurs |

**Insights** uses completed sessions. Failed and cancelled sessions are excluded.
Unknown speech-word counts remain unknown and are excluded from word totals and
pace. Dictation pace includes microphone pauses; file transcription is excluded
from pace. Disable **Usage statistics** in Settings to stop collection without
deleting prior records. The app has no external usage telemetry.

Changing model folders does not move or delete weights. Package updates preserve
local data; uninstalling removes package-local data, including models in the
default folder. Models in a chosen folder outside package storage remain there.
Use **Speech models > Model folder > Choose folder** to reuse development weights
from `.runtime\models`.
