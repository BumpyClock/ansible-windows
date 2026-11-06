# Capture overhead probe

This standalone NativeAOT experiment compares a warm Nemotron CPU session with
the current screen encoder off and on. It does not drive the desktop, open a
microphone, upload data, or change application settings.

```powershell
.\tools\record-spikes\overhead\Build-OverheadSpike.ps1
.\.runtime\record-spikes\overhead-app\OverheadSpike.exe --self-check
.\tools\record-spikes\overhead\Run-OverheadSpike.ps1
```

Build once before measuring. Do not run other model probes or builds during the
measurement. Leave unrelated applications running. The runner records hardware,
free memory, source revision, and a two-second process CPU sample. It then runs
the native executable. The main output is under a new ignored
`.runtime/record-spikes/overhead/<UTC run>/` directory. Environment evidence is
in `.runtime/record-spikes/overhead-environment-<UTC>.txt`.

The claim under test is the change in final recognition latency and sampled
memory for the same warmed native session and public speech sample while this
encoder is active. The probe does not choose a production encoder or establish
whole-app dictation performance.

After measurement finishes, inspect all output dimensions and durations with
Windows video metadata. This tool compiles after the measured runs, so it does
not add build load to them.

```powershell
.\tools\record-spikes\overhead\Inspect-OverheadVideos.ps1 -RunDirectory <printed-run-folder>
```

Two diagnostic warmups, one per condition, precede ten measured trials in
alternating baseline/capture order. All trials use CPU, four inference threads,
the existing 512 MB memory headroom, and the same prepared Nemotron session.
`StreamingProbe.cs` is linked from the existing real-time input probe. Its
40 ms packet-end pacing, bounded eight-packet channel, and latency definitions
are unchanged. The native final must report the complete sample count, 28 spoken
words, and the public sample's `Mother Nature` anchor. Exact final speech must
match across every trial.

For a capture trial the helper starts on a dedicated owned worker. A manual
reset event ends its bounded 60-second recording. Both sides wait five seconds
before starting ASR. Capture runs keep a two-second tail after ASR finalization,
then stop and join the worker. Those fixed waits are excluded from recognition
metrics. They are not product user latency. The first actual source frame must
precede ASR. Successful encoder-input submissions must span the ASR interval
on the real QPC clock, and the decoded MP4 timeline must cover that interval.
Quiet input can reuse a held real image; it must not shorten the take. WGC
updates and held encoder inputs are separate counts. A delay or a running task
alone is never accepted as readiness evidence. The completed MP4 must decode
H.264 frames and contain zero audio streams. Invalid coverage or an error fails
the harness instead of becoming a successful performance sample.

The memory sampler waits 100 ms between queries during the ASR region. Query
time and scheduling add to the actual intervals recorded in each CSV. Peaks are
observed sample maxima, not true allocation peaks or process-lifetime
`PeakWorkingSet`. Process
CPU and whole-system active CPU are measured across that region. Capture worker
CPU covers its full recording lifetime and excludes encoder work on other
threads. It is diagnostic evidence, not an isolated encoder cost. Diagnostic
warmups are retained in raw CSV but excluded from reported medians and ranges.

`trials.csv` records actual ASR, memory, CPU, frame, output-size, and clock
evidence. Per-trial memory CSV files preserve the samples. `identity.txt` records
runtime/AOT state, selected model/backend/settings, and SHA-256 hashes.
`summary.txt` reports five-run medians and ranges per side. `outcome.txt` records
success or a concrete error. Captured MP4s stay local and ignored. Their input
screen contents vary with the existing desktop. Frame activity is not controlled.
The requested 30 fps media type and duplicate decoded frames do not establish
30 unique captured frames per second. CPU staging/scaling is the current
feasibility implementation, not a tuned final pipeline.
