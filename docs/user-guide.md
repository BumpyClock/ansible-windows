# Use Ansible

See the [README](../README.md#build-and-run) to build and launch the app.

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

- **Paste, then restore clipboard** is the default. Ansible backs up the clipboard,
  offers the speech text, sends Ctrl+V, and restores the backup after the target
  reads it. It requests exclusion from Windows clipboard history and cloud sync.
  Clipboard or restoration failures are reported. If another app copies content
  during the paste, that new content is kept.
- **Type characters** sends Unicode keystrokes. **Gap between characters (ms)**
  ranges from 0 to 200, with a default of 20. A zero gap sends a burst that can
  garble text in slow targets such as Notepad. Use paced typing for apps that
  block Ctrl+V.

Paste is rejected if the clipboard contains formats that cannot be backed up or
the backup exceeds 256 MiB. Large clipboard content can delay insertion. If the
target does not read the offered text within three seconds, Ansible restores the
clipboard and reports failure. A target that processes Ctrl+V later can paste the
restored content instead. Check the field; use **Type characters** for that app.

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

## Local data and privacy

The app keeps captured audio and transcript content in memory. Recognition does
not upload them. Model download requests are the app's network activity.
**Copy** uses the ordinary clipboard; Windows clipboard features and destination
apps can retain text you copy or insert. Automatic shortcut paste requests
clipboard-history and cloud-sync exclusions, but those requests are not a storage
guarantee for other apps.

Per-user files are in Windows package storage, normally
`%LOCALAPPDATA%\Packages\<package-family-name>\LocalState`:

| Path | Contents |
| --- | --- |
| `settings.json` | Preferences, chosen model folder, and custom vocabulary |
| `usage.json` | Session IDs, completion timestamps, model IDs, session type, speech-word counts, and durations |
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
