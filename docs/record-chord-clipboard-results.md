# Clipboard acceptance results

## Current qualification

Claude desktop accepted at least 16 of the tested synthetic PNG files.
Combined files/text and combined contact-sheet/text publications omitted text.
Separate bulk-file and text pastes preserved both in two eight-image checks.
This does not qualify paced image-by-image delivery, larger files, claude.ai,
or any other target. The final draft remains unsent.

## Initial access attempt on 2026-11-04

No target passed or failed an insertion test. No attachment cap or fallback is established. Desktop results do not establish website or cloud results.

The machine reported Windows 11 Enterprise build 26300. Claude desktop package version 3.6556.2.0 exposed a window, but its observed accessibility tree contained only an empty document. The window capture was blank after activation, so no empty composer or existing draft could be verified. Further captures included an unrelated window. No input was sent to Claude.

The desktop window inventory did not expose a ChatGPT desktop or GitHub Copilot app chat window. Copilot CLI processes were present, but those are not evidence of a desktop chat composer. No desktop ChatGPT or Copilot paste was attempted.

For chatgpt.com, the T3 preview opened a tab, but two snapshot attempts each returned `Preview automation snapshot timed out after 15000ms`. The test tab was closed. Browser fallback selection returned `No browser is available`, and browser discovery returned an empty list. Therefore claude.ai, chatgpt.com, the Copilot coding agent task box, and the Codex cloud task box remain blocked for this session. Authentication state and browser version were not established. No website or cloud composer was operated.

The published NativeAOT harness ran `publish --case text --target claude-desktop` successfully. Run ID `20261104T191618194Z-cf8cd2` contains a text-only fixture, so PNG size and attachment counts do not apply. Publication replaced the clipboard and sent no keys. Its persistent clipboard data is not evidence of insertion. No synthetic draft or screenshot was saved because no safe target was visible. The existing manual matrix remains pending; separate blocked observations were appended to the ignored CSV. All text preservation, attachment counts, order, label readability, pacing, and processing times remain unmeasured.

## Claude desktop retest on 2026-11-04

Claude desktop 3.6556.2.0, Windows 11 Enterprise build 26300. These are desktop chat results, not claude.ai website results. All tests used the new-chat composer and synthetic fixtures. Nothing was sent. The final draft contains the 16 synthetic attachments and no text. It remains unsent.

| Case and run ID | Visual observation | Result |
| --- | --- | --- |
| Text, `20261104T191728774Z-51037b` | The three fixture lines appeared in the composer and matched `text.txt`. | Pass. |
| Files plus text, one frame, `20261104T191751634Z-a1e577` | One 283,648-byte PNG appeared. Its preview showed the readable label `SYNTHETIC FRAME 01`. The composer contained no fixture text. | Partial. |
| Files plus text, eight frames, `20261104T191841214Z-b79c0f` | Eight thumbnails appeared in numbered order. Total PNG bytes were 2,280,560. Fixture text was absent. | Partial. |
| Separate text, `20261104T191912284Z-2fdfaa` | The exact fixture text appeared after the eight images were attached. Images remained visible. | Pass for this two-paste sequence. |
| Files only, eight frames, `20261104T192113044Z-18f39b` | Eight thumbnails appeared in order with the preceding text still present. Total PNG bytes were 2,280,560. This repeated the file-and-text combination in reverse paste order. | Pass for this two-paste sequence. |
| Composite plus text, eight frames, `20261104T192304261Z-26da45` | One 1,163,210-byte contact sheet appeared. Its expanded preview showed readable labels 01 through 08 in order. Fixture text was absent. | Partial. |
| Files plus text, 16 frames, `20261104T192358902Z-0cc738` | Sixteen numbered thumbnails appeared in order across the horizontal strip. Total PNG bytes were 4,556,430. Fixture text was absent. | Partial. |

All individual frames were 1280 by 720. The 16-frame files ranged from 282,287 to 287,441 bytes each. The contact sheet was 1920 by 1080. Frame labels in the individual thumbnails were small. Frame 01 was verified in the expanded preview. The contact sheet labels were verified in its expanded preview. Time to usable attachment was not measured with a stopwatch. Thumbnails appeared on the next inspection after each paste, so no timing bound is claimed. No attachment error was observed.

These observations establish at least 16 file attachments for these fixtures in this desktop composer. They do not establish a maximum or larger-file support. Separate file and text pastes worked twice with eight images, in opposite orders. The repeated sequence is promising, but its production reliability and paced image-by-image delivery remain untested. Bitmap alone, noisy larger files, and higher counts remain pending. No all-target conclusion is supported.
