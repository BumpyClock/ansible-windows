# Performance evaluation, 2026-10-25

The strongest opportunities are repeated device-identity hashing, full meeting transcript redraws, synchronous transcript persistence, and loading entire meeting histories. These are source-confirmed repeated work. Their effect on packaged-app latency still needs measurement. No production code was changed for this review.

Review baseline is `9e23ed01242a72376ef4213ca2a40564097b6f16`. The initial working tree was clean. Review covered startup, dictation preparation and delivery, native streaming and qualification, meeting capture and attribution, persistence, UI observers and rendering, model management, and build configuration. Generated files, downloaded dependencies, and personal app data were excluded.

**Evidence and limits.**

- Core tests passed, 958 passed, zero failures and zero skips, using `dotnet test .\Ansible.Tests\Ansible.Tests.csproj --filter 'Category!=NativeIntegration'`. The local TRX is `.runtime/review-results/performance-review.trx`.
- The native DLL, model weights, and public WAV were absent. No downloads, microphone capture, app installation, or native qualification were performed. No Ansible process was found running.
- Managed diagnostic measurements used .NET SDK 10.0.402, Release, .NET 10 JIT, workstation GC, Windows x64, and synthetic data. These are not NativeAOT or WinUI measurements. The measured host has 16 logical processors. Local hardware details are recorded in `.runtime/performance-review/hardware.json`.
- Two background CPU samples before the probe were 87.86% and 65.91%. Other applications were left running. Timing variance is substantial. There is no sampled CPU or I/O profile, so the dominant wall-time limiter is unresolved. These are cost observations, not proven end-to-end bottlenecks or speedup estimates.
- Each case received setup and warmup outside timing, followed by five measured calls with rotated case order. Files were warm in the OS cache. Timing includes output checks for attribution, history and identity. Allocation counts are process-wide managed allocations, not retained memory, native memory, or working set. CPU-time counters in the raw data have coarse resolution.
- The probe asserts unchanged speech, expected turn counts, successful save/load round trips, complete history listings without failures, and stable identity hashes. Every check passed. It never loads its synthetic DLL fixtures as executable code.

Reproduce from the repository root with a fresh output directory. The harness is separate from the solution and deliberately uses the existing test friend-assembly name to reach production diagnostics without changing their visibility.

```powershell
dotnet run --project docs/reviews/performance-probe/ReviewProbe.csproj -c Release -- .runtime/performance-review-repeat
```

[Probe source](performance-probe/Program.cs) and [all measured samples](performance-probe/measurements.json) are retained with this review. The original command used the same source from `.runtime/performance-review/ReviewProbe.csproj` with the corresponding relative project reference.

The documented command was then rerun successfully against a fresh fixture directory. Its [five additional samples per case](performance-probe/reproduction-measurements.json) gave a 493.218 ms median for the 300-document list, a 112.674 ms median for the larger identity fixture, and the same 4,043,480-byte median for two-hour attribution. The five-minute save median changed from 9.961 to 50.888 ms, reinforcing that storage timing here is noisy. The table below reports the first run, not a merged or selected subset.

**Measured managed costs.**

| Production operation | Synthetic workload | Median ms | Range ms, five calls | Median allocated bytes |
| --- | --- | ---: | ---: | ---: |
| Speaker attribution | 5 min, 750 words, 75 activities | 0.424 | 0.327 to 0.938 | 165,744 |
| Speaker attribution | 30 min, 4,500 words, 450 activities | 1.795 | 1.237 to 28.767 | 1,013,144 |
| Speaker attribution | 120 min, 18,000 words, 1,800 activities | 16.510 | 5.616 to 44.095 | 4,043,480 |
| Full save with disk flush | 5 min, 75 turns, 21,104 bytes | 9.961 | 6.778 to 53.335 | 2,840 |
| Full save with disk flush | 30 min, 450 turns, 125,754 bytes | 8.858 | 7.902 to 58.054 | 3,744 |
| Full save with disk flush | 120 min, 1,800 turns, 503,754 bytes | 13.259 | 11.181 to 47.056 | 11,432 |
| List saved meetings | 10 documents, 0.64 MB on disk | 15.914 | 13.046 to 26.072 | 1,247,784 |
| List saved meetings | 100 documents, 6.36 MB on disk | 163.484 | 148.201 to 296.619 | 12,468,896 |
| List saved meetings | 300 documents, 19.08 MB on disk | 497.125 | 448.055 to 763.386 | 37,419,880 |
| Auto cache-key construction | 4 MiB main DLL, 2 MiB sibling, sample | 18.850 | 16.560 to 22.796 | 540,096 |
| Auto cache-key construction | 32 MiB main DLL, 16 MiB sibling, sample | 111.503 | 106.916 to 120.740 | 539,120 |

Attribution fixtures contain 150 words per minute and alternating four-second speaker intervals. History fixtures contain 180 ten-second turns and 4,500 words per document. They do not represent real recognition accuracy or speaker overlap. Identity fixtures contain deterministic random bytes and a 450,000-byte sample. Their sizes are illustrative, not measurements of the missing native package.

**1. High priority. Avoid reconstructing the Auto device identity on every warm operation.**

Impact for dictation users is a source-supported opportunity to reduce capture-to-recognizer readiness even when device qualification and native sessions are already cached.

`NativeAudioEngine.DeviceSelection.cs:62-64` calls `DeviceBenchmark.Key` before checking `_deviceSelections`. `DeviceBenchmark.cs:107-151` hashes the native library and sample, enumerates and hashes every sibling DLL, and reads CPU/GPU registry identity on every call. The native library is hashed twice because it also matches `*.dll`. In the packaged app these siblings include the other runtime DLLs copied to the app root.

The 32 MiB fixture reads 80 MiB of DLL contents plus the sample per key computation. Its 111.503 ms median is evidence for that synthetic workload only. Hashing, file access, registry work, and background scheduling were not profiled separately.

Recommended change is one verified runtime/hardware identity per connected engine or package generation, combined with the existing per-model, mode, thread, vocabulary and diarization-profile fields. Remove the duplicate library hash. Preserve integrity verification, file-change invalidation, and driver-sensitive cache invalidation. Do not reuse a persistent key solely because a path matches.

Validation should compare warm shortcut starts with a populated device cache, count hash/file-read invocations, and verify that changed DLLs, models, vocabulary decoder mode, or hardware identity invalidate the appropriate selection. Record actual packaged runtime size before estimating savings.

**2. High priority. Update and virtualize meeting transcript rows.**

Impact for meeting users is an opportunity to keep scrolling, selection, and incoming transcript updates responsive as the meeting grows.

`MeetingWindow.xaml:38-39` places all transcript rows in a `StackPanel` inside a `ScrollViewer`. `MeetingWindow.xaml.cs:180-226` avoids redraw only when collection references match. Otherwise it clears all children, constructs controls for every turn, attaches new speaker-button handlers, and forces `UpdateLayout` when following the bottom. `MeetingSession.ApplyUpdate` supplies a fresh turns array, so incoming transcript updates take the full rebuild path. Audio clock updates alone generally hit the reference guard; they do not rebuild every row.

Recommended change is a data-bound, virtualized transcript collection with stable turn identity and updates limited to changed turns. Late speaker corrections must still update affected earlier turns. Keep the viewport constrained and preserve keyboard focus, text selection, expansion state, and follow-bottom behavior. Microsoft's [WinUI collection performance guidance](https://learn.microsoft.com/windows/apps/develop/performance/optimize-gridview-and-listview#ui-virtualization) supports viewport-based virtualization and reducing per-item elements.

No UI frame time was measured. Validate a packaged build at 100, 1,000 and 2,000 turns while appending and correcting turns. Measure UI-thread update time, realized control count, allocations, scrolling, and selection behavior. A virtualized control inside another unconstrained scroller can defeat the intended improvement.

**3. High priority. Remove full-document persistence from the recognition callback.**

Impact for meeting users is an opportunity to prevent storage delays from consuming the audio backlog budget.

`MeetingSession.cs:412-425` receives native progress synchronously and blocks on `SaveAsync().GetAwaiter().GetResult()`. `MeetingStore.cs:184-199` serializes the entire cumulative document, flushes it to disk, and atomically replaces the previous file. That happens for each update. Native diarization and ASR updates can both publish during the same audio push (`NativeDiarizationSession.cs:137,181`). This is synchronous backpressure from the storage path onto inference.

The measured half-megabyte document cost 13.259 ms median per save, with an 11.181 to 47.056 ms range. This small synthetic document does not prove that storage currently causes backlog failures. It does establish a real blocking cost and a dependency that becomes sensitive to filesystem stalls.

Recommended change is one bounded persistence worker owned by the meeting, accepting the latest document revision while another save is active. Join and flush the required revision at pause, finish and close, and surface save errors. Keep atomic replacement and durable final saves. Coalescing intermediate revisions changes crash-recovery granularity, so specify the maximum permitted unsaved interval before implementation. Do not create one detached task per update or silently discard persistence failures.

Validation should inject a slow store while recognition continues, prove bounded pending state, verify save ordering, and check that close joins the final save. Also verify crash-recovery behavior against the chosen durability interval.

**4. High priority for growing histories. Load summaries instead of entire transcripts for lists and startup.**

Impact for established users is an opportunity to keep startup and window activation independent of total transcript history.

`MeetingStore.ListAsync` (`MeetingStore.cs:34-55`) loads and validates every complete document. Startup awaits `MarkInterruptedSessionsAsync`, which uses that same scan, before `_session.InitializeAsync` (`MainWindow.xaml.cs:142-150`). The window's unfiltered `Activated` handler also refreshes meeting history (`MainWindow.xaml.cs:95-103`). `MeetingsPage.xaml.cs:61-79` rebuilds list rows and retains complete documents in each row's `Tag`, although the list displays only title, date and status.

At 300 synthetic documents, one warm list call took 497.125 ms median and allocated 37.4 MB. This is store-call elapsed time, not a measured UI freeze. Async I/O does not necessarily occupy the UI thread for the entire interval. Loading, validation, continuation work, and row construction still grow with history.

Recommended change is a lightweight metadata listing with full documents loaded on open. Refresh on relevant changes or navigation, not every window activation transition. Keep interrupted-session recovery as a separately joined startup operation so it need not gate independent dictation initialization. Start with avoiding unnecessary refreshes; introduce a rebuildable metadata index only if history scale warrants it. Preserve corrupt-file visibility and recovery rather than hiding read failures.

Validation should compare empty, 100 and 300-document startup and activation workloads, measure time until shortcut readiness, and verify recovery, external file changes, renamed meetings, deletions, and corrupt files.

**5. Medium priority. Bound cumulative speaker-attribution work as meetings grow.**

Impact for long meetings is an opportunity to reduce managed allocation and repeated CPU work without weakening speaker attribution.

`NativeDiarizationSession.ReadTokens` (`NativeDiarizationSession.cs:184-207`) rereads the entire native ASR token history into new managed objects. `Publish` then calls `NemotronSpeakerAttribution.Create` over all tokens and activities. That method rebuilds all words and turns and sorts activity events into a new coverage index (`NemotronSpeakerAttribution.cs:53-120,200-216`). Later layers validate and copy the turns again. The coverage index already avoids scanning every activity for every word; retain that useful optimization.

The two-hour synthetic attribution call allocated 4,043,480 bytes. The timing range was too noisy for a firm latency estimate. Allocation and work scale with cumulative history even when only a tail changed. Actual update cadence and native-token-copy costs were not measured.

Recommended first step is avoiding duplicate publications and caching the activity timeline when activities are unchanged. Then consider an explicit stable-prefix/revisable-tail contract across ASR, attribution, persistence and UI. Do not assume native tokens or speaker assignments become immutable after an arbitrary timeout. The native final replaces streamed activity, so final reconciliation must remain correct.

`MeetingSpeakerMatcher.Refine` also sorts and rebuilds evidence when there are no observations. The two-hour no-observation fixture allocated 68,904 bytes and took 1.178 ms median. An empty-evidence path could be simplified, but must still clear obsolete automatic names. This is secondary to full transcript rebuilding.

Validation should replay incremental synthetic updates with revised old token timings, late speaker evidence, overlap, silence and final replacement. Match the full recomputation result exactly and measure allocations per update through a long session.

**Qualification gap. Short native benchmarks omit most meeting update work.**

`DeviceBenchmarkWorker.cs:96-108` calls `StreamCore` with `progress: null`. `NativeDiarizationSession.Publish` uses a conditional progress call, which skips per-event managed attribution when that callback is absent. Final attribution still runs. The benchmark also excludes `MeetingSession`, persistence, speaker-name matching and WinUI rendering. It warms up once and measures one public-sample run (`DeviceBenchmarkWorker.cs:114-124`). The five-percent CPU/GPU preference therefore rests on a single measured trial, and the meeting real-time admission test does not exercise a long complete meeting pipeline.

Keep isolated device qualification, memory admission and the known unsafe Nemotron Vulkan exclusion. Add separate end-to-end qualification with short and long histories, storage, UI updates, finalization, and repeated trials. Record cold readiness, warm first preview, input-end-to-final, maximum backlog, p95 update time, native/managed memory, and actual transcript correctness. Do not infer a two-hour guarantee from a 14-second sample or the 0.50x admission threshold.

**What already earns its cost.**

Source inspection confirms background model preparation, reuse of successfully reset streaming sessions (`NativeAudioEngine.cs:560-580`), identity-checked model hash caching, bounded audio queues, pooled inference conversion buffers, and background dictation usage persistence. Preserve these. Do not optimize paced Unicode typing into bursts, remove memory guards, weaken native ownership, or parallelize CPU/GPU qualification against shared memory. No evidence from this review justifies those changes.

Usage persistence still reloads and rewrites its whole metadata file per completed entry, and the app constructs its main pages eagerly. These are lower-priority scaling hypotheses. The current usage writer already stays off final-text delivery, and the visible home projection caches unchanged usage. Profile those paths before changing them.

**Suggested implementation sequence.**

1. Fix the incorrect insertion-success reporting identified in the companion quality review. This is a correctness issue, independent of performance.
2. Remove repeated Auto identity hashing with explicit invalidation and measure actual warm shortcut latency.
3. Update and virtualize meeting rows, then separate bounded persistence from recognition with a documented durability interval.
4. Remove unnecessary history refreshes and separate metadata listing from full document reads.
5. Measure long meetings end to end before introducing incremental attribution or tuning native thread counts.

The current evidence supports these targets. It does not establish an overall app speedup, a preferred inference model/backend, or a production latency guarantee.
