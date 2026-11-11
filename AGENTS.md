# Project context

## Product direction

This repository builds a Windows dictation and transcription app, not Ansible automation.
The intended experience is a WisprFlow-style workflow with a native Windows interface.
Use WinUI 3 and .NET NativeAOT for fast startup and efficient managed execution.
Use [audio.cpp](https://github.com/0xShug0/audio.cpp) as the local native inference backend.
Support multiple speech-recognition models through that backend rather than coupling the app to one model.

The current application is an integration proof of concept, not a complete production dictation product.
Treat the product direction as the goal, not a claim that every workflow already exists.

## Rapid prototype development

This project is in rapid prototype development, with no default backward-compatibility requirement.
Existing code is exploratory work, not evidence of intentional architecture or a contract to preserve.
The code map below describes the current layout, not required module boundaries.

- Breaking changes and substantial refactors are welcome when they improve the product or simplify the design.
- Replace weak designs directly. Do not add compatibility shims, deprecated wrappers, or parallel legacy paths to protect prototype code.
- Change APIs, model IDs, data formats, and module boundaries as needed. Require compatibility only when the user explicitly requests it.
- Update affected callers, tests, and documentation together. Remove obsolete code rather than maintaining both implementations.
- Keep the product goals and correctness requirements below. Prototype freedom does not justify unsafe resource handling or fabricated recognition results.

## Current implementation

- The app targets .NET 10 and uses single-project MSIX with self-contained .NET NativeAOT and Windows App SDK deployment.
- Build on Windows x64. Both x64 and ARM64 target CPU/Vulkan. ARM64 cross-compiles baseline ARMv8-A/NEON through clang-cl and VS ARM64 tools, without OpenMP. Vulkan shaders use x64 host tools. ARM64 binaries and NativeAOT packages compile but are not hardware-qualified. Keep architectures in separate runtime directories. x86 and NPU are unsupported. Qualify each model/device combination separately.
- Recognition runs in-process through the audio.cpp C ABI, using `audiocpp.dll`.
- The native build pins audio.cpp v0.9.0. It applies tracked patches, including the Nemotron cumulative streaming timestamp patch, and compiles `nemotron_3_diar`. Upstream support does not imply support in this compiled backend.
- Catalog entries carry an `asr` or `diar` task. Omitted task means ASR. Only family `nemotron_3_diar` may be `diar`.
- Diarization companions cannot be selected for dictation. Dictation, replay, and WAV remain plain ASR. Unconfigured dictation prefers installed `nemotron-asr-0.6b-q8` and keeps a saved model ID. Default setup installs that ASR and `nemotron-3-diarization-q8`.
- There is no meeting model picker. Start snapshots processor, language hint, and vocabulary. An explicit saved model ID is honored only when it matches the current selected meeting-capable streaming ASR (Nemotron, Kroko, or VibeVoice streaming). An automatic dictation default is not a meeting override. Explicit Nemotron or Kroko requires installed `nemotron-3-diarization-q8`; a missing companion fails Start instead of switching models. Unconfigured meetings use installed Nemotron ASR with that companion, then installed Kroko with the same companion, then VibeVoice streaming, including when the companion is absent. Auto qualification rejects a meeting candidate above 0.50x real time on the selected device (`MeetingRealTimeException`); unconfigured meetings then continue down that order and record `SelectionNotice`, while an explicit choice fails Start. The diarizer streams in four-second chunks.
- The UI includes Dictation, Settings, Insights, Speech models, and a floating waveform pill. The welcome lesson is a modal `ContentDialog` over the current page, opened on first launch and from Settings > Dictation > Shortcut > Practice; it is not a shell page. Its footer is the stock `ContentDialog` button row set per step: Next (Done on the last step) as the accent default button, Back (hidden on the first step), and Skip for now (hidden on the last step). Enter activates Next or Done. It follows the window theme. Pages sit on an inset content layer (WinUI layer fill over the Mica base) 4px below the 48px title bar and 4px from the right and bottom edges. `PageCanvas` owns each page's only vertical scroller; views do not add nested scrollers. Its header and content share one column that starts at the page padding and is at most `PageReadingMaxWidth` (1000) wide, or `PageContentMaxWidth` (1440) when the page is `Wide`. `Wide` can change while the page shows. Only Home sets it, from code: Home is wide with the two-column layout once History holds a saved dictation and the page content is at least 1040 DIP wide. With no saved dictations Home stays at the reading width and stacks at every width. Settings is one scrolling page of six sections in this order, each a heading over one card of rows: Dictation, Screen and ink, Recognition, Meetings, Transcript cleanup, and Privacy and data. It has no tabs and no sub-group headings. `EmptyState` is the one empty state (glyph on a 48 DIP round backplate, Subtitle title, optional detail and footer), placed 64 DIP under the content above it on pages. History, Meetings, Dictionary, the meeting window transcript, and the Home rhythm card without usage statistics use it. Home's Words dictated card holds the Most-used models list. The radial Daily rhythm graph is drawn for every period, and an empty period says when the last dictation was or "No dictations in this period". When Home is stacked, History and its empty state come first, then the two insights cards side by side, which fill their row so both end on one line. The History heading uses BodyStrong like section headings on other pages. The History empty state reads "New dictations appear here." and, when usage statistics count earlier dictations, adds "N earlier dictations are no longer saved." ("Your earlier dictation is no longer saved." for one). Day headings on Home history and Meetings, and the date in the meeting window, read weekday, day, abbreviated month ("Thu 29 Oct"), with the year only outside the current year. The meeting window title bar shows the 16 DIP app mark before the title. A transcript turn that starts at exactly zero reads "0:00" with no "About" prefix. Meetings rows show a detail line and a More options menu (Open, Delete), untitled meetings read "Meeting at <time>", and the header More options menu holds "Delete empty meetings…", which removes finished meetings with no speech after one confirmation. A finished meeting with no speech shows a secondary title and no badge, and its detail line states the outcome once (`MeetingText.Outcome`). A failed or interrupted meeting that holds saved text keeps its badge, and only the Failed badge with saved text uses the critical icon. The meeting window keeps its title on one line with a rename button and a More options menu. Content that must stay on screen while the page scrolls uses `PageCanvas.Pin` (`StickyPin.cs`), which drives a composition Translation from the scroller; Home pins the History header while History has rows and, in the wide layout, the insights rail. Once less than a third of the page title's height is left in the viewport (`PageCanvas.IsTitleScrolledOut`), the title bar shows the page name. Once the header actions scroll out too (`IsActionScrolledOut`), it adds, directly after the name, a copy of the page's declared `PageCanvas.PrimaryAction` (a `PageAction`) as a standard button, not an accent button, with the same label, enabled state, visibility, and click handler; the copy carries the action's icon when the page declares `PageCanvas.PrimaryActionIcon`. Home and Meetings declare one. On Speech models, "Transcribe WAV file…" and "Model folder…" are items in a header More options menu, and "Model folder…" opens the folder flyout (path, Choose folder, Verify installed files). On Dictionary, "Import…" is in a header More options menu and Add is the page's only accent button. The title bar brand is a 16 DIP icon with the app name in caption type, in the compact layout too. The compact sidebar rail is 48 DIP. The pill shows the current level through a fixed bar envelope and, by default, sits under the destination caret read once at shortcut press; bottom centre is the fallback. The shared Badge component sizes to content up to 240 effective pixels, trims long labels, and uses the WinUI control corner radius with a 28px minimum height.
- Automatic Teams detection reads active Windows capture-session metadata and offers a notetaker prompt; only an explicit Start captures meeting audio.
- Meeting audio is the default microphone plus loopback of every active output device, retained as synchronized, separate microphone and playback channels. Microphone speech is labeled `You`; playback ASR and diarization preserve remote speaker identities. Teams mute does not mute this independent microphone capture. Teams process loopback proved silent, so a selected Teams process supplies speaker naming, verified call-window titles, and automatic stop after an observed call ends, not audio routing. Endpoint preparation runs off the capture thread. Meeting capture uses one `IMeetingCapture` contract with `MeetingAudioPacket` frames. Device access uses the internal `IMeetingAudioDevices` boundary so tests run the real worker against fake devices. Playback-pair qualification alone does not qualify the concurrent microphone stream.
- Launch checks microphone permission before session initialization; capture factories recheck access without prompting and never open devices just to request consent.
- Microphone capture uses Windows `waveIn` with 16 kHz mono PCM16 and bounded audio queues.
- The session preloads the selected streaming model and a reusable native session, unloads them after 10 idle minutes, and opens the microphone before the model is ready so early speech is buffered, not lost.
- Successful dictation transcripts persist locally as text and metadata in `dictation-history.json` beside `settings.json`, newest first, at most 100 entries within a 500,000-character budget. Reload after restart. Group by Today, Yesterday, and older dates. Search covers all retained entries. Row Remove permanently deletes the saved entry. Insertion status persists. An entry larger than the budget stays current-result only. WAV results stay outside dictation history. History writes use one background coalescing writer; shutdown drains and joins it. Corrupt or unreadable history is left untouched, a warning is shown, and new rows stay in memory until storage can be recovered. Prior session-only transcripts cannot be recovered. Captured audio is never saved or uploaded. Meeting transcripts save locally. Optional Copilot CLI or OpenAI wording cleanup sends transcript text only. Settings saved opt-in is snapshotted at meeting Start. Successful End after the original is saved can trigger automatic cleanup. Manual cleanup remains explicit. Local Insights persist session metadata, not transcript text or recordings. Usage writes use a 32-entry bounded queue so disk work never delays final insertion; shutdown drains and joins that writer. Failed or overflowed writes set `UsageError`, which Insights shows.
- Every main-window notification is an `AppNotice` in the window's `AppNotices` list, rendered by `NoticeHost` as `NoticeBar`s in a band at the bottom of the content layer. A `NoticeBar` is a flat docked bar (card stroke, control corner radius, no shadow). In the main window the band lines up with the current page's column (`PageCanvas.Column`, `NoticeHost.AlignWith`), with the same left and right insets and width cap. `MainWindow` sets the band height as the current page's `PageCanvas.BottomInset`, so the page's scroll viewport ends above the band, a notice never covers page content, and content at the top of the page does not move. `MainWindow` relays the session notice (key `session`) and posts microphone permission, Teams suggestion, tray, close, and its own UI failures. UI code posts errors to `AppNotices` under a stable key per source, never through the session; a new error replaces that source's previous notice. Pages post keyed notices and transient confirmations; a page condition that explains the page (Dictionary compatibility, a model row error) may use `NoticeBar` inline. The meeting window owns its own `AppNotices` and `NoticeHost` with one notice slot. Session notices display only undismissed warnings and errors, scoped to Dictation, Settings, Models, Dictionary, Meetings, or app-wide. Capture-privacy failures are app-wide. Routine success and cancel notices are not shown. Meeting optional naming and timing-refinement failures are trace warnings. The meeting window keeps editable speaker labels and does not show `NamingStatus`, `RefinementStatus`, or `DeviceSelectionSummary` cards.
- Shortcut insertion sends authoritative final speech once after successful recognition. Default paste publishes text immediately, replaces the clipboard, and leaves it available for delayed readers; never use a render callback or timeout as proof of insertion or restore old clipboard content behind a queued paste. Refresh UIA identity before input; never downgrade a previously identified field. Verify only exact, bounded, same-field text changes; keep snapshots in memory. Only pre-input deferral permits automatic retry. Release owned keydowns after cancellation without replaying the paste. Request history, monitor, and cloud-sync exclusions and report publication failures before sending input. Paced Unicode typing preserves the clipboard; burst typing garbles slow targets such as Windows 11 Notepad. Manual Copy uses the ordinary clipboard.

Start with `README.md`. Use `docs/user-guide.md` for app workflows, `docs/development.md` for builds and tests, and `docs/native-validation.md` for model probes and native limits.
Use the current source to determine implemented UI behavior.

## Code map

- `Ansible\` contains the WinUI app, XAML views, floating window, and Windows microphone integration.
- `Ansible.Core\DictationSession.cs` owns the shared operation lifecycle, including startup, recognition, preferences, cancellation and shutdown.
- `Ansible.Core\ModelSelectionPolicy.cs` owns dictation and meeting model eligibility, the default model preference, meeting candidate order, and the diarization companion requirement. It is pure and table-tested.
- `Ansible.Core\ShortcutDictation.cs` owns one shortcut dictation attempt. It covers admission, push-to-talk release tails, destination inspection, insert-once delivery, warnings, diagnostics, and the shutdown join. `Ansible\WindowsShortcutTarget.cs` adapts `WindowsTextTarget` to its destination contract.
- `Ansible.Core\MeetingSuggestions.cs` owns meeting metadata polling, suggestion episodes, stale-open checks, and shutdown/restart. `MainWindow` supplies availability and active selections, renders offers, and opens the notetaker. Only explicit Start captures meeting audio.
- `Ansible.Core\RecognitionContracts.cs` defines testable engine, audio and storage boundaries. `SessionState.cs` defines immutable UI snapshots and terminal outcomes.
- `Ansible.Core\` contains native interop, verified model admission, shared audio input parsing, speech-content normalization, and per-user usage storage.
- `Ansible.Core\Windows\` owns Windows capture bindings and retained-resource teardown. `WaveInCaptureFactory.cs` composes the native capture adapter.
- `Ansible\AppNotices.cs` defines the notice model and window-owned list. `NoticeBar.xaml` is the one notification control, and its `NoticeBarPanel` lays a notice out by content, stacking the message and actions when they do not fit on one line; `AppNotices.Arrange` filters by page area, orders by severity, and caps the collapsed stack; `NoticeHost.xaml` renders, stacks, and times out notices for a window. `ShellPage.cs` is the main window's page registry (page, sidebar entry, notice area, title). `ShellTitleBar.xaml` owns the title bar, its collapsed page name, and the primary action copy. `StickyPin.cs` keeps a pinned element on screen and fades its stuck indicator. `PageCanvas.xaml` owns page headers, the page width cap (`Wide`), the single page scroller, and the bottom inset that ends the scroll viewport above the notice band. `EmptyState.xaml` is the shared empty state. `MainPage.xaml` is Settings, one scrolling page of six sections.
- `Ansible\WelcomeDialog.xaml` owns the three-step shortcut lesson and its practice field target. `WelcomeScene.xaml` draws keycaps that follow real key state and a pill that follows the real session; it never loops or shows sample text.
- `Ansible\ShortcutField.xaml` renders a shortcut as keycaps and owns the capture box that records a new one. `Ansible\ShortcutKeycaps.cs` draws a shortcut as keycaps for the Home header.
- `Ansible\ModelCardText.cs` builds the plain-language text on a model row from the catalog entry and download snapshot; exact technical details stay under Show details.
- `Ansible\AppPaths.cs` supplies installation resources and per-user data paths. `UiSessionObserver.cs` dispatches versioned snapshots to the UI.
- `Ansible.Core\NativeAudioEngine.cs` owns model sessions and recognition. `NativeAudioApi.cs` defines the C ABI bindings.
- `Ansible.Probe\` exercises actual native WAV and streaming recognition independently of the UI.
- `Ansible.Tests\` contains xUnit tests for core behavior.
- `tools\` contains model setup, native build, and NativeAOT publish scripts.
- `tools\audio-models.json` owns model IDs, tasks, capabilities, pinned downloads, checksums, and licenses.
- `.runtime\` holds downloaded models, upstream source, native tools, and generated binaries. Keep it out of Git.

## Development commands

Run commands from the repository root in PowerShell.
Native publishing requires Windows x64, the .NET 10 SDK, and Visual Studio's Desktop development with C++ workload.

Run the core tests when changing core behavior:

```powershell
dotnet test .\Ansible.Tests\Ansible.Tests.csproj --filter 'Category!=NativeIntegration'
```

Prepare the runtime when the native DLL or models are missing:

```powershell
.\tools\Setup-AudioBackend.ps1
.\tools\Build-AudioNative.ps1
```

Default setup installs `nemotron-asr-0.6b-q8` and `nemotron-3-diarization-q8`.
Use `-Models <model-id>` for another catalog entry, including `moonshine-tiny`.
Do not install every model for routine checks; `-Models all` downloads large weights.
Unconfigured dictation prefers installed `nemotron-asr-0.6b-q8`. It does not override a saved model ID.

Build and verify an unsigned NativeAOT MSIX after changes to the WinUI app, native interop, or deployment:

```powershell
.\tools\Publish-Ansible.ps1
```

The default publish requires a Vulkan runtime record. Pass `-Backend cpu` for an intentional CPU-only package.
Add `-Bundle` for a verified MSIX bundle. Select `-Architecture x64,arm64` for both architectures or `-Architecture arm64` for ARM64 alone. x64 does not require ARM64 or x86.
Each selected architecture needs a matching native runtime with the requested backend. See `docs/development.md` for toolchains and qualification limits.
Run `tools\Test-PackagingConfiguration.ps1` and `tools\Test-NativeBuildRecipe.ps1` after architecture or packaging changes.
Use the packaged Visual Studio launch profile for development. Package signing, certificate trust, and installation are separate operations.

Use `docs/native-validation.md` when changing model integration or streaming behavior. Native integration tests require the native runtime, public sample, and specific weights; see `docs/development.md` before running them.
Paired meeting speaker checks use `.\tools\Test-SpeakerModels.ps1 -Model nemotron|kroko|both`.
Run model probes sequentially and wait for each process to exit.
Documentation-only edits do not require model downloads or a native rebuild.

## Engineering constraints

- Keep NativeAOT compatibility. Use source-generated JSON and `LibraryImport`; avoid reflection-dependent serialization and runtime code generation.
- Ensure NativeAOT publishing includes required WinUI `.pri` and `.xbf` resources. A successful managed build does not prove publish correctness.
- Keep the native C ABI integration as the default architecture. Do not introduce an HTTP server or Python runtime for recognition.
- Keep capture and inference off the UI thread. Dispatch UI updates and make session ownership explicit.
- Intercept the final window close before shutdown. Join every owned operation; unresolved native resources must remain owned and block new work.
- Support cancellation, release native handles, and bound audio queues. Surface failures instead of dropping samples or silently changing models.
- Respect each model's offline, streaming, language, preview, and task capabilities. Streaming input does not guarantee incremental recognition. Diarization companions are not dictation models.
- Never simulate transcripts or usage statistics. Distinguish installed weights, successful loading, successful recognition, and measured live performance.
- When adding models, update the pinned catalog, compiled native families, capability handling, and relevant tests together.
- Pin upstream revisions and verify download lengths and SHA-256 checksums. Include upstream notices; model licenses are separate from audio.cpp's license.
- Bound inference by available memory. Do not close unrelated applications or disable memory safeguards to force model loading.
- Keep speech local. Do not add audio uploads or external telemetry without an explicit product decision. Dictation history persists only successful dictation text and metadata in `dictation-history.json` under the 100-entry and 500,000-character limits. Do not save captured audio or WAV results there.
- Count authoritative speech content, not presentation labels. Unknown speech counts remain unknown rather than becoming zero.
- Measure startup, recognition latency, and memory before making performance claims. NativeAOT does not remove native model costs.

## Version control

Create local checkpoint commits at coherent, verified milestones during substantial work.
Commit task-related changes only; preserve unrelated user edits. Do not push unless requested.
