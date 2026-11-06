# Record chord clipboard acceptance spike

This standalone harness lets the tester check real chat composers before Ansible chooses a bundle delivery format. It uses clearly labeled synthetic images. It never records the screen, opens the microphone, submits a message, or changes Ansible settings. These are spike tools, not a record chord implementation.

Acceptance observations are recorded in [the results](record-chord-clipboard-results.md). Publishing valid Windows clipboard bytes does not prove that a chat input accepts attachments, preserves text, or finishes reading before the next paste. The proposed eight-image cap and fallback ladder remain undecided.

## Build and self-check

From the repository root in PowerShell:

```powershell
.\tools\record-spikes\clipboard\Build-ClipboardSpike.ps1
.\.runtime\record-spikes\clipboard-app\ClipboardSpike.exe self-check
```

The self-check replaces the clipboard with synthetic test data. It sends no keyboard input. It reads back exact text, image bytes, `CF_HDROP` filenames through the Windows Shell, and the three privacy exclusion formats. It leaves the last contact sheet and text on the clipboard. It does not restore earlier clipboard content.

All generated files and records default to ignored `.runtime/record-spikes/clipboard`. Each invocation creates a unique subdirectory. `files.csv` records PNG sizes and dimensions. `text.txt` contains the exact expected text. `publication.csv` records requested formats and queued input. `manual-results.csv` is created once and preserves the tester's entries on subsequent runs.

## Test each target by hand

Use a disposable, unsent chat draft. Record the exact browser or desktop app version, Windows build, date, run ID, and target. The initial matrix covers all five destinations:

| Target | Text | One image and text | Many images and text | Limits and fallback |
| --- | --- | --- | --- | --- |
| claude.ai | Pending | Pending | Pending | Pending |
| chatgpt.com | Pass | Image only | Eight images, no combined text | One separate text paste worked; limits and repeat pending |
| GitHub Copilot app chat | Pass | Image only | Eight images, no combined text | One separate text paste worked; limits and repeat pending |
| Copilot coding agent task box | Pending | Pending | Pending | Pending |
| Codex cloud task box | Pending | Pending | Pending | Pending |

First test publication only. Switch to the target yourself and press Ctrl+V once. Replace `claude.ai` with the actual destination label.

```powershell
$spike = '.\.runtime\record-spikes\clipboard-app\ClipboardSpike.exe'
& $spike publish --case text --target claude.ai
& $spike publish --case files --count 1 --target claude.ai
& $spike publish --case files-text --count 1 --target claude.ai
& $spike publish --case bitmap --target claude.ai
& $spike publish --case files --count 8 --target claude.ai
& $spike publish --case files-text --count 8 --target claude.ai
& $spike publish --case composite --count 8 --target claude.ai
```

Run one command, inspect that paste, and remove the test draft before running the next command. `files` publishes `CF_HDROP` alone. `files-text` publishes `CF_HDROP` and `CF_UNICODETEXT` together. `bitmap` publishes one image as registered `PNG` plus `CF_DIBV5`, with text in the same publication. `composite` publishes one numbered contact sheet with text in the same formats. This tests whether a target drops text when image or file formats coexist.

For the paced sequence, invoke `paste` deliberately. Focus the target before the countdown ends. This sends one image-only paste per fixture, followed by a text-only paste. It never presses Enter. Keep focus on the same window, release all keys and buttons, and do not use the clipboard during the sequence.

```powershell
& $spike paste --case sequential --count 8 --countdown 8 --pace 1500 --target claude.ai
```

`paste` also supports the other cases for repeatable input timing. It checks that the foreground window and clipboard sequence still match before each Ctrl+V. It stops after a focus or clipboard change. This is a manual spike and does not inspect whether the field is editable. It is not the product's destination-inspection or insert-once delivery implementation. A timer only paces publications. It is never evidence that an image was consumed. Sequential publication can replace data before a slow target reads it, which is precisely why this path needs target-specific measured success.

Record actual attachments, order, readable frame numbers, preserved text, rejected formats, upload errors, and time until attachments become usable. Compare text with the generated `text.txt`; do not infer text preservation from a visible attachment. Record failure and partial success separately. A queued Ctrl+V is always unverified in the harness log.

Find the count limit by repeating `files-text` with increasing `--count`, up to the harness ceiling of 32. Find size sensitivity with `--width 1920 --noise`; seeded random pixels produce much larger PNGs than flat fixtures. Record actual bytes from `files.csv`. The width range is 320 through 1920. The harness uses 16:9 images. Its safety ceilings are test limits, not measured target limits. If the target accepts every tested count or size, record a lower bound rather than inventing a maximum. Account tier, browser, and app version may affect the observed result.

The eight starting frames are a provisional experiment size. The lowest successful attachment count across the five targets must set the eventual product cap. One successful target cannot qualify another. If combined text and images fail, test one composite image with text. If that also drops text, record it. Do not choose a sequential production fallback until that target passes repeated paced checks.

## Clipboard contract

Every text, image, and file publication includes DWORD zero payloads for `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`, and `CanUploadToCloudClipboard`, matching Ansible's text publication. All data is rendered before input. Ownership of successful `SetClipboardData` handles transfers to Windows. Unpublished memory is freed. The clipboard remains available after the process exits. No delayed rendering, clipboard restoration, UI Automation attachment claims, HTTP service, or Python dependency is used.

The bitmap path uses bottom-up 32-bit BGRA `CF_DIBV5` with explicit channel masks and sRGB, alongside a registered `PNG` format. `CF_HDROP` uses a `DROPFILES` header with a double-NUL-terminated UTF-16 filename list. These formats follow Microsoft's [clipboard format guidance](https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats), [Shell clipboard format guidance](https://learn.microsoft.com/windows/win32/shell/clipboard), and [SetClipboardData ownership contract](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setclipboarddata).

## Results from this session

On 2026-11-04, the build script published and verified a native x64 PE executable with no managed CLR header. The NativeAOT executable passed exact clipboard readback for text, files, files plus text, bitmap plus text, and composite plus text. Every case included the three exclusion formats. Shell filename enumeration also passed. Separate no-input publications passed for eight PNG files plus text and one bitmap plus text. No paste input was sent.

Windows GDI decoded the generated three 1280 by 720 PNG fixtures and their 1920 by 1080 contact sheet. The contact sheet was visually inspected and showed readable synthetic frame labels. The generated manual result matrix has 40 rows, all pending. No target limit or fallback was established.

The local native self-check log is `.runtime/record-spikes/clipboard-self-check.log`. The tested native executable SHA-256 was `9E3005B0FC681419984F1CEDE158788EC2D33983E856C177426749CCBBA18B68`. Target hand results remain pending.

## Target results on 2026-11-04

The initial attempt encountered UI access blockers. The later Claude desktop
retest observed text-only insertion and at least 16 file attachments for the
tested fixture sizes. Combined file-plus-text and composite-plus-text pastes
omitted text. Separate bulk-file and text pastes preserved both in two
eight-image checks. See the [results](record-chord-clipboard-results.md) for
the observations, remaining target blockers, and limits. Paced image-by-image
delivery remains unqualified.

Chrome ChatGPT and GitHub Copilot desktop each showed eight synthetic file thumbnails in order and omitted text from the combined file publication. One separate text paste preserved both in each surface. ChatGPT accepted the composite text but showed no composite image. These single checks do not qualify a fallback or set a maximum. See the [results](record-chord-clipboard-results.md) for run IDs and remaining gaps.

The user subsequently confirmed that web paste behavior matches the desktop
tests. The report records this as user-reported manual verification. Browser
automation stopped on URL-confidence enforcement; no action bypassed that stop.
Cloud task-box attachment limits remain unmeasured.

The subsequent [bundle regression check](record-chord-clipboard-results.md#bundle-verification-regression-on-2026-11-04)
observed corrected bundle text followed by eight PNGs in T3 Code and Claude
desktop. It also tested actual same-field text readback. This does not yet
qualify the updated app's physical record-chord workflow.
