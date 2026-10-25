# Code quality review, 2026-10-25

Implementation and verification of these findings are recorded in [Review fixes](2026-10-25-fixes.md). This review describes the original baseline.

Review of commit `9e23ed01242a72376ef4213ca2a40564097b6f16`. Review only. No application, test, build, or deployment changes were made.

The strongest findings are a misleading insertion outcome, a production callback pipeline whose only consumers are tests, and an operation owner that mixes model replacement with recognition. The source does not support a claim that large parts of the application are dead. Most native and capture abstractions protect real ownership or platform boundaries.

Priorities use P2 for actionable correctness or substantial maintenance work and P3 for small deletions. Structural findings describe the current tree. This was a full-tree review, so no claim is made that a particular commit introduced them.

**Q1. P2. Failed key release is presented as successful insertion.**

Users can see an **Inserted** history tag even when Windows never queued the paste trigger. The evidence is the `CleanupFailed` mapping at [HomeHistoryProjection.cs:16](../../Ansible/HomeHistoryProjection.cs#L16), the early cleanup-failure return at [WindowsTextTarget.Input.cs:49](../../Ansible/WindowsTextTarget.Input.cs#L49), and the producer at [WindowsTextTarget.cs:172](../../Ansible/WindowsTextTarget.cs#L172).

`SendPasteChord` returns `CleanupFailed` before checking whether all keydowns were sent. A send that queues only Ctrl, followed by failed key release, therefore produces this outcome without queuing V. The history projection labels it "Inserted" and explains that the clipboard could not be cleaned up. Both current producers report failed injected-key release. Neither reports clipboard restoration. The current paste path deliberately leaves dictated text on the clipboard.

[FinalTextDelivery.cs:60](../../Ansible.Core/FinalTextDelivery.cs#L60) also classifies `CleanupFailed` as `Sent`. The immediate warning still reaches users through `delivery.Error`, so this finding concerns inaccurate evidence in history and the outcome contract, not automatic replay. [HomeProjectionTests.cs:102](../../Ansible.Tests/HomeProjectionTests.cs#L102) and [FinalTextDeliveryTests.cs:137](../../Ansible.Tests/FinalTextDeliveryTests.cs#L137) preserve the inaccurate interpretation.

Smallest correction. Give failed key release a conservative label and accurate explanation. Do not infer that the full text was inserted. If the product needs exact partial-input evidence, represent delivery evidence separately from key-release failure. Keep automatic retry exclusive to `Deferred`. Do not restore the old clipboard cleanup path.

Behavioral verification. Feed `SendPasteChord` a sender that accepts only the first modifier and then rejects releases. Carry its actual outcome through `FinalTextDelivery` and the history projection. Assert that the history never says "Inserted", that the warning names key release, and that input is not replayed. Keep the existing cancellation and partial-release tests. This path was established by source tracing, not a live Windows input experiment.

**Q2. P2. Delete the second dictation preview channel that only tests consume.**

Maintainers currently carry two preview publication paths, but the app consumes only `StateChanged`. [DictationSession.cs:94](../../Ansible.Core/DictationSession.cs#L94) accepts an optional `IProgress<TranscriptUpdate>`. It travels through `Begin` at line 282, assignment at line 308, the `Operation` property at line 1151, and the extra conditional report and lock at lines 1052-1056.

Both application callers use the parameterless method. They are [DictationPage.xaml.cs:396](../../Ansible/DictationPage.xaml.cs#L396) and [MainWindow.xaml.cs:758](../../Ansible/MainWindow.xaml.cs#L758). The only callers supplying the callback are [DictationSessionTests.cs:1424](../../Ansible.Tests/DictationSessionTests.cs#L1424) and [DictationSessionTests.cs:1450](../../Ansible.Tests/DictationSessionTests.cs#L1450). The test name `LateOrCancelledTranscriptUpdatesCannotReachLiveDelivery` describes a path that current shortcut delivery does not use. Shortcut delivery awaits the final operation result.

Delete the optional argument, forwarding parameter, stored callback, and secondary report path together. Preserve the engine's progress contract and the session's `StateChanged` preview updates. Those have production consumers. Rewrite the useful parts of the two tests against state publication and final insertion. Delete assertions whose only purpose is to keep the second callback alive.

Behavioral verification. Existing recording, cancellation, stale-update, final-only insertion, and history-completion tests should still pass. Add no replacement callback abstraction. The desired result is one fewer channel and one fewer operation field.

**Q3. P2. Split model replacement ownership from recognition ownership.**

The next engineer changing model installation must reason about microphone shutdown and transcript completion because [DictationSession.cs:279](../../Ansible.Core/DictationSession.cs#L279) routes connection, model maintenance, WAV transcription, replay, and dictation through one optional-payload dispatcher. The file has 1,191 lines, measured from the reviewed tree. Its size is a symptom of mixed ownership rather than a reason by itself to split files.

Evidence is concentrated in four places. `Begin` assigns phase, transcript retention, nullable model, optional path, startup usage loading, maintenance delegate, and recognition options according to activity. `ExecuteAsync` at line 336 switches on that activity again and mixes connection notices with recognition outcomes. `ReleaseOperationAsync` at line 751 understands capture, producer, inference, candidate engine, and previous engine. The `Operation` type at line 1129 contains all those unrelated resources and optional payloads. Null-forgiving accesses such as `operation.Path!`, `operation.Maintenance!`, and `operation.Model!` rely on the dispatcher having constructed a valid combination.

Separate the model connection or replacement transaction from the recording or transcription operation. Keep one session admission gate and one owner for shutdown. Give each operation its actual required input and its own retained resources. A small closed set of concrete operation payloads is sufficient. Do not build a generic command framework or spread the existing field bag across partial files.

The useful deletion is removal of activity-specific model, path, maintenance, and resource branches from the wrong operation. Settings persistence and the bounded usage writer can become cohesive helpers afterward if that further reduces what the session must coordinate. They are not prerequisites for the ownership correction.

Behavioral verification. Preserve rollback after failed model replacement, cancellation during startup, early microphone buffering, failed capture release, retryable close, shutdown joining all owned tasks, and final completion only after cleanup. Keep the ownership failure tests. Do not "simplify" by disposing failed resources and forgetting their handles. This is a supported maintenance finding, not a demonstrated current resource leak.

**Q4. P2. Stop calculating analytics that no production consumer reads.**

Insights maintainers inherit a wider analytics contract than the product uses. [UsageSummary.cs:5](../../Ansible.Core/UsageSummary.cs#L5) exposes `TotalWords`, `FileWords`, `LongestStreak`, and `UnknownWordSessions`. `ModelUsage` at line 3 also exposes per-model `Words`. Searches of the application, core, probe, tools, and XAML found no production reader for those outputs. The application projection at [HomeInsightsProjection.cs:35](../../Ansible/HomeInsightsProjection.cs#L35) consumes dictation counts, pace, recording time, current streak, activity, model session counts, and overall session count.

The unused outputs are not just extra record properties. The calculator sums file words at [UsageSummary.cs:27](../../Ansible.Core/UsageSummary.cs#L27), sorts all active dates and walks them for longest streak at line 40, sums words within each model group at line 49, and counts unknown-word sessions at line 57. [UsageTests.cs:31](../../Ansible.Tests/UsageTests.cs#L31), line 36, and line 117 assert several of these otherwise unread values.

Delete these outputs and their exclusive calculations. Keep the persisted usage entries, their validation, nullable word counts, and known-word filtering used for pace. `FileSessions` still contributes to the production `Sessions` total, so do not delete file-session counting blindly. Keep file sessions represented in model shares and activity unless the product explicitly changes that behavior.

Behavioral verification. Compare the existing Home Insights projection for empty usage, unknown words, mixed file and microphone sessions, midnight boundaries, and multiple models. Remove tests that only demand the deleted aggregates. Retain tests that prove unknown speech does not become a known zero. No wall-clock performance claim is made for this deletion.

**Q5. P3. Remove confirmed unused members and an unconsumed interface.**

These deletions reduce maintenance surface without inventing a replacement layer. They are intentionally grouped below the structural findings.

| Location | Evidence | Concrete deletion |
| --- | --- | --- |
| [DictationSession.cs:1092](../../Ansible.Core/DictationSession.cs#L1092) | `UpdateFacts` has no callers in production or tests. | Delete the private method. |
| [MeetingTextCleanup.cs:17](../../Ansible.Core/MeetingTextCleanup.cs#L17) | `IMeetingTextCleanup` occurs only in its declaration and the concrete class's implementation list. The app constructs `CopilotMeetingTextCleanup` directly at [MeetingWindow.xaml.cs:485](../../Ansible/MeetingWindow.xaml.cs#L485). | Delete the interface and implementation annotation. Preserve the concrete service and its process-runner test seam. |
| [MeetingRecognitionContracts.cs:7](../../Ansible.Core/MeetingRecognitionContracts.cs#L7) | `MeetingSession` never reads `IMeetingRecognitionEngine.DeviceSelectionSummary`. The property still has real consumers through `IRecognitionEngine` and the concrete native engine. | Remove only the meeting-interface member. Preserve dictation's device summary and native benchmark diagnostics. |
| [PageCanvas.xaml.cs:31](../../Ansible/PageCanvas.xaml.cs#L31) | `ContentWidth` appears only at its declaration across source and XAML. `ContentPadding` and `ViewportHeight` have callers. | Delete `ContentWidth` only. |
| [DesignTokens.xaml:89](../../Ansible/DesignTokens.xaml#L89), line 239, line 322 | `AppSelectedBrush` has theme definitions but no source, XAML, or resource dependency reader. `AppSelectedColor` at lines 20 and 171 is referenced only by those brushes. | Delete the three unused brush entries and their two exclusive color definitions. |
| [DesignTokens.xaml:453](../../Ansible/DesignTokens.xaml#L453) | `SettingsSegmentMinWidth` appears only at its definition. | Delete the unused dimension. |
| [DesignTokens.xaml:913](../../Ansible/DesignTokens.xaml#L913) | `HomeLegendFutureChipStyle` has no references. | Delete the unused keyed style. Preserve its shared base style. |

Verification for managed deletions is compilation plus the existing relevant tests. Resource deletions need the design-token check and a packaged UI build when implementation occurs. Do not treat framework resource keys with no local references as dead. WinUI can resolve those through control templates.

**Architecture observations that did not become deletion findings.**

- `NativeOperation`, `NativeModule`, native handles, capture factories, and permission decorators enforce real cancellation, teardown, loader, or consent invariants. Their small size and test seams are not sufficient grounds for deletion.
- Linked app source in the test project exercises platform-independent projections and the real input-policy methods. It is not a parallel production implementation.
- `TeamsSpeakerObserver.LastRead` is read only by a native qualification test, but it supplies bounded traversal diagnostics rather than implementing a hypothetical product flow. Deleting it solely for low caller count would reduce qualification evidence. It is retained as justified diagnostic surface.
- `NativeTranscript`, `MeetingTranscript`, and `StreamingTranscript` have different output contracts. There is shared speaker-label parsing worth revisiting, but this review did not establish a sufficiently strong behavior-preserving merger to prescribe one.
- The 908-line `MainWindow.xaml.cs` owns several lifetimes. Shortcut delivery and meeting detection are plausible future concrete owners. No separate finding is added merely for file size. Any extraction must remove its corresponding fields and shutdown joins from the window, not move methods while retaining shared mutable state.
- `NativeAudioEngine` already separates device selection and alignment into partial files. Partial files do not create ownership boundaries, but the shared native model and semaphore explain much of this cohesion. Successful streaming teardown can retain a reusable session. It must not be mistaken for unconditional reload.
- Fixed interop-layout fields, generated JSON contexts, WinUI event handlers, XAML classes, vtable callbacks, and framework resource overrides must survive ordinary "zero direct caller" searches.

**Inventory and coverage.**

The inventory below uses tracked first-party files with extensions `.cs`, `.xaml`, `.ps1`, `.cjs`, `.csproj`, `.props`, `.targets`, `.json`, `.patch`, `.appxmanifest`, and `.manifest`. Line counts come from PowerShell `Get-Content` on the reviewed working tree. They describe audit scope, not executable size or complexity.

| Area | Files | Lines | Coverage |
| --- | ---: | ---: | --- |
| `Ansible` | 51 | 9,830 | Shell and lifecycle, shortcut insertion, meetings, projections, settings, model UI, XAML/resource references, app composition and packaging. |
| `Ansible.Core` | 68 | 9,342 | Session and model ownership, recognition contracts, native/session boundaries, model admission, capture, persistence, meeting attribution/alignment, device selection, usage and permissions. |
| `Ansible.Probe` | 3 | 331 | CLI callers, streaming measurement contract, project configuration. |
| `Ansible.DeviceProbe` | 2 | 19 | Worker entrypoint and project configuration. The worker implementation is in Core. |
| `Ansible.Tests` | 55 | 11,899 | Test inventory, test-only production consumers, linked production sources, outcome assertions, safety regression coverage, native-test gating. |
| `tools` | 11 | 1,475 | Setup, catalog validation, native build and patches, publish checks, speaker probes, asset generation and resource checks. |
| Total | 190 | 32,896 | Repository-wide inventory and reference search, with deeper control-flow review of the owners named above. |

The detailed reading concentrated on lifecycle and resource owners, their callers, and tests that could keep unused production paths alive. This is not a claim that every line received equally deep analysis. Generated `bin` and `obj`, `.runtime`, vendored upstream sources, generated binary/image assets, and external dependency internals were excluded. Project instructions and README were read. Existing documentation informed scope but was not audited for every stale sentence.

The review used `rg` reference searches, declaration/reference scans across production and tests, targeted source reads, and an XML resource dependency walk. Candidate resource deletions were checked again with repository searches. Declaration/reference counts are lexical evidence, not compiler reachability proof. The checks explicitly accounted for XAML references and native callbacks before reporting deletions.

`ast-grep` and `sg` were unavailable on PATH, and no existing installation was found in the checked npm, pnpm, or Scoop locations. No global tool was installed for this read-only review. `mcporter list --no-oauth` completed. Two servers were healthy, six required authentication, and Chrome refused the configured relay policy. Connected services were not needed for local source evidence.

The parent reviewer ran the required non-native suite and reported **958 passed, 0 failed**. This reviewer did not rerun it or alter tests. No native model, microphone, UI, or MSIX validation was performed here. The parent reported that the native DLL, model weights, and public sample were absent. Passing managed tests therefore does not establish native or packaged behavior.

**Suggested implementation sequence.**

1. Correct Q1's outcome interpretation and the tests that preserve it. Keep final-only insertion and no automatic replay.
2. Delete Q2's test-only callback pipeline and Q5's confirmed unused surface. Preserve useful tests through existing production boundaries.
3. Trim Q4's analytics contract to actual readers.
4. Refactor Q3 in ownership-sized steps. Require existing cancellation, rollback, retained-resource, and close tests after each step. Publish the NativeAOT package before declaring an implemented UI or interop change complete.

The review agent did not create a checkpoint commit; the parent owns the final artifact checkpoint.
