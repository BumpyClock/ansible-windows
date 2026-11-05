# Clipboard acceptance results

## Current qualification

| Observed surface | Accepted file count | Combined files/text | Separate file and text pastes |
| --- | --- | --- | --- |
| Claude desktop | At least 16 | Text omitted | Preserved both; two eight-image checks, including reverse order |
| chatgpt.com in Chrome | At least 8 | Text omitted | Preserved both; one check |
| GitHub Copilot desktop | At least 8 | Text omitted | Preserved both; one check |
| T3 Code desktop | At least 8 | Not tested | Corrected bundle text followed by eight PNGs preserved both |

These counts are lower bounds at the tested fixture sizes. They are not
attachment maxima. Paced image-by-image delivery and larger files remain
unqualified. No test message was sent.

On 2026-11-04 the user separately confirmed that web paste behavior matches
the desktop tests. This is user-reported manual verification. No additional
per-surface fixture sizes, processing times, or cloud task attachment limits
were supplied. Keep that provenance separate from the observed runs below.

## Bundle verification regression on 2026-11-04

The installed app pasted text without pictures in T3 Code and Claude desktop.
Pictures are gated on exact text insertion, so attachment acceptance alone had
not qualified the app's automatic two-paste path.

A production UIA readback probe reproduced an empty Chromium composer as one
newline and a collapsed final caret beyond `DocumentRange`. T3 also converted
`@0:03` to a file mention and changed blank-paragraph readback. The verifier
continues to require stable field identity, bounded complete content, selection,
and an exact insertion. The reader now handles Chromium's sole empty paragraph
and terminal caret. Generated text uses `at 0:03` and single line separators.

The corrected 110-character synthetic bundle passed exact same-field comparison
in both T3 Code and Claude desktop. Claude's production bounded observer also
returned `Verified`. Each subsequent single `CF_HDROP` paste visibly attached
eight numbered PNGs in order while preserving that text. Claude's horizontal
strip was inspected through frame 08. No message was sent. Both drafts remain
unsent with synthetic text and eight images.

T3 files run `20261105T032926150Z-6ec3ac` and Claude files run
`20261105T033128992Z-cfbbe8` used 1280 by 720 fixtures from the harness. The
readback probe is ignored under `.runtime/record-spikes/target-readback`.
These checks exercise actual accessibility and clipboard paths with separate
paste input. They do not yet qualify a physical record-chord take through the
installed update, larger files, paced bitmaps, contact sheets, or a maximum.

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

## Chrome and GitHub Copilot app tests on 2026-11-04

Windows 11 Enterprise build 26300. Chrome 156.0.8078.76 used the existing signed-in profile and a new chatgpt.com Chat-mode draft. GitHub Copilot desktop app 1.1.34 used a fresh Chat session on This computer. These are distinct surfaces. Neither message was sent.

| Surface and case | Observation | Result |
| --- | --- | --- |
| chatgpt.com text, `20261104T193052004Z-ea551a` | All three lines matched `text.txt`. | Pass. |
| chatgpt.com files plus text, one, `20261104T193126982Z-6b1b93` | One 283,648-byte 1280 by 720 PNG appeared. Expanded preview showed `SYNTHETIC FRAME 01`. Text was absent. | Partial. |
| chatgpt.com files plus text, eight, `20261104T193212948Z-4b02c0` | Eight 1280 by 720 thumbnails appeared in order 01 to 08. PNGs totaled 2,280,560 bytes. Text was absent. | Partial. |
| chatgpt.com separate text, `20261104T193227572Z-b0df6c` | The three fixture lines appeared after those eight images. The images remained. | Pass for this one two-paste check. |
| chatgpt.com composite plus text, `20261104T193355146Z-468ed2` | All three text lines appeared. No image appeared on subsequent inspection. The 1920 by 1080 contact sheet was 1,163,210 bytes. | Partial. |
| GitHub Copilot app text, `20261104T193443501Z-c858c6` | All three lines matched the fixture. | Pass. |
| GitHub Copilot app files plus text, one, `20261104T193459497Z-ca3712` | One 283,648-byte 1280 by 720 PNG thumbnail appeared. Text was absent. The small thumbnail showed frame 01. | Partial. |
| GitHub Copilot app files plus text, eight, `20261104T193523010Z-840c35` | Eight 1280 by 720 thumbnails appeared in numbered order. PNGs totaled 2,280,560 bytes. Text was absent. | Partial. |
| GitHub Copilot app separate text, `20261104T193531086Z-044145` | The three fixture lines appeared after those eight images. The images remained. | Pass for this one two-paste check. |

Individual frame labels 01 through 08 were visible in the ChatGPT thumbnails. Copilot thumbnails were small; their sequence was visible, but full-size label readability was not checked. No attachment error was observed. Processing time was not measured. Eight is a tested lower bound for these file fixtures, not a maximum. The separate-paste candidate was not repeated. Copilot composite, paced bitmaps, larger files, and higher counts were not tested.

The new Chrome ChatGPT draft remains unsent with the composite fixture text and no image. The new GitHub Copilot draft remains unsent with eight synthetic images and fixture text. The earlier Claude desktop draft was preserved. Chrome's new claude.ai tab redirected to a login URL, so that website was not tested and the tab was closed without interacting with login. Consumer ChatGPT desktop was not identified. The installed `ChatGPT (Beta)` package was `OpenAI.CodexBeta` 26.727.4816.0, not evidence of a consumer chat app.

The T3 preview profile displayed an unauthenticated ChatGPT page and said login was required to upload files. Its test tab remained open when Computer Use stopped. Existing Chrome was signed in and supplied the results above. While opening `github.com/copilot/agents` in another new Chrome tab, Computer Use returned: `Computer Use has been stopped for this turn because it could not determine the current browser URL on Windows with enough confidence to enforce policy. Stop your work and send a final message noting why Computer Use ended.` The new agents tab remains open. Copilot coding agent and Codex cloud task composers were not tested. No action was taken to bypass the stop.
