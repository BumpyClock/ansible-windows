# Capture and encode overhead spike

The production performance verdict is **inconclusive**. This run measured the
current prototype on one busy machine, with an uncontrolled desktop and an
untuned CPU readback/scaling path. It does not select a production encoder,
bitrate, or resolution. The observed latency and memory values below are real
measurements of that configuration.

## Observed comparison

The corrected run completed five alternating trials per condition, after one
diagnostic warmup per condition. All ten measured trials passed. There were
zero rejected samples and zero operation errors in this corrected run.

| Metric | Capture off, median (range) | Capture on, median (range) |
| --- | --- | --- |
| End-of-input to final recognition | 238.3 ms (233.3 to 247.1) | 272.6 ms (254.4 to 317.2) |
| Source duration to final recognition | 248.0 ms (240.4 to 256.0) | 277.4 ms (268.1 to 322.5) |
| First nonempty preview | 1357.7 ms (1344.8 to 1369.3) | 1369.2 ms (1360.0 to 1378.4) |
| Sampled peak private commit | 6671.0 MiB (6667.2 to 6671.7) | 6791.4 MiB (6785.0 to 6793.7) |
| Sampled peak working set | 1075.5 MiB (1071.4 to 1076.5) | 1173.1 MiB (1172.2 to 1177.9) |
| Process CPU during ASR | 25671.9 ms (24500.0 to 26843.8) | 34500.0 ms (32062.5 to 37625.0) |
| Whole-system active CPU | 38.2% (35.0 to 45.1) | 44.0% (36.2 to 55.0) |

The observed median final-tail difference was 34.4 ms, and the observed
source-duration-to-final difference was 29.4 ms. The sampled private-commit
difference was 120.4 MiB; the sampled working-set difference was 97.6 MiB.
Private commit is not resident RAM. Peaks are maxima of samples taken during
recognition, not process-lifetime peaks or true instantaneous allocation peaks.

The sampler waited 100 ms between queries. Actual intervals across measured
trials ranged from 109.5 to 155.9 ms, with a 124.2 ms median, because queries
and scheduling also take time. Raw sample timestamps are retained.

All trials returned the complete 225151-sample input, 28 spoken words, the public
sample's `Mother Nature` anchor, and identical final speech. The common speech
SHA-256 was `97FC8C2B0DEA7A3DD180403F668EABE27930479CC70FA9FAF992F54C764EE357`.
No microphone was opened and no captured audio was saved.

## What was timed

`tools/record-spikes/overhead` is a standalone Release NativeAOT executable.
It links the existing `Ansible.Probe/StreamingProbe.cs` input pacing and metric
implementation. Both conditions use the same prepared Nemotron session, CPU
backend, four inference threads, and unchanged 512 MB memory headroom. Input
packets are available at their absolute 40 ms packet-end deadlines through the
existing bounded eight-packet channel. Loading and warmups are excluded.

The speech sample lasts 14.0719375 seconds. Its real-time input cadence is the
dominant lower bound on full-stream duration. This is not a throughput contest
or pure inference timing. End-of-input is when the producer accepted the final
packet, not when native recognition consumed it; the final tail includes queued
processing. Source-duration-to-final also includes input backpressure.

Capture starts before recognition on a dedicated owned worker. Both conditions
wait five seconds before the measured ASR region. Capture runs keep a two-second
tail after ASR and then signal their stop event. These fixed waits are excluded
from the recognition values and are not product user latency. The stop event
remains alive until the worker joins. The MP4 is then decoded independently.

A valid capture trial requires a real source image before ASR, successful
encoder submissions spanning ASR on the QPC clock, and a decoded video timeline
covering the entire ASR interval. The same native helper used by the WinUI
package records this evidence. Fresh WGC updates are not required on a quiet
screen. Held images remain explicitly separate from source updates.

Measured captures had 49 to 328 WGC updates, 459 to 601 successful encoder input
submissions, and 273 to 547 held inputs. Every output was 1920 by 1200 pixels
and silent H.264, with a 20.6 to 20.8333-second decoded duration. The diagnostic
capture warmup had only four WGC updates and still passed encoded-work and
decoded-duration coverage. No claim of 30 unique screen frames per second is
made. The output media type requests 30 fps and can contain repeated frames.

## Interpretation and limits

The unreported diagnostic warmups retain process CPU, capture-worker CPU, and
system CPU counters. The measured data then uses the same instrumentation on
both sides. Full-stream time is constrained by the known input pacing. Capture
worker CPU during an entire take ranged from 0.797 to 2.500 seconds, while
whole-process CPU also includes ASR and encoder work on other threads. Worker
CPU is not an isolated codec cost or a sampled native-stack profile.

The desktop's activity varied, and whole-system load ranged from 35% to 55%
during the measured trials. Existing Teams, Figma, browser, and other processes
were left running. No build or second inference process ran concurrently.
Alternating conditions reduces drift; it does not remove these confounders.
The native pixel readback/scaling implementation is still an untuned feasibility
path. Therefore these observations do not establish a general causal slowdown,
prove no user-visible regression, or choose this encoder over GPU conversion.

This does not measure microphone-device startup, actual capture queue overflow,
hotkey handling, WinUI rendering, ink composition, clipboard delivery, cold model
loading, five-minute recordings, Vulkan ASR, or other models and machines. It
measures warm recognition while the current encoder is already active.

## Rejected first attempt and correctness fix

The first attempt is preserved at
`.runtime/record-spikes/overhead/20261104T194707025Z`. Five baselines and four
capture trials passed its original gate; the fifth capture was rejected.
There was one invalid coverage result, and the run exited nonzero. Its
incomplete comparison is inconclusive and is not mixed into the table above.

The rejected capture's last WGC timestamp preceded recognition. Its MP4 was
only 3.3 seconds although the worker ran through the five-second lead-in,
recognition, and two-second tail. The cause of the absent WGC updates was not
established. Inspection found that the old helper encoded only on new updates
and ended its final sample after 1/30 second. It omitted a quiet tail. The
benchmark also incorrectly required new WGC updates throughout ASR.

The corrected helper submits a held real image on its bounded real-time cadence
and extends the final sample through actual stop. The new gate checks successful
submission clocks and independently decoded duration. It does not merely accept
a running task or ignore missing work. A shared-encoder static-input fixture
proved a four-second quiet tail with one labeled synthetic image, zero WGC
updates, 99 held inputs, and a readable correctly oriented image near the end.
The corrected actual NativeAOT MSIX also passed. See the
[encoder validation](record-chord-encoder.md#quiet-input-regression-and-corrected-package).

## Reproduce and evidence

Build and invocation are in the
[overhead harness guide](../tools/record-spikes/overhead/README.md). Builds must
finish before measuring. Native inference remains sequential. The new harness
is NativeAOT but was not itself packaged as an MSIX; the corrected WinUI helper
and interop were verified separately through the published package.

The successful run is
`.runtime/record-spikes/overhead/20261104T200817692Z`. It contains `trials.csv`,
per-trial memory samples, `summary.txt`, `identity.txt`, `outcome.txt`,
`video-metadata.csv`, and local silent MP4s. The run log is
`.runtime/record-spikes/overhead-run-held-v2.log`. Environment and pre-run CPU
evidence are in
`.runtime/record-spikes/overhead-environment-20261104T200813335Z.txt`.
These artifacts are ignored and must be preserved separately when sharing.

The run used Windows 11 Enterprise 26300, .NET 10.0.14 NativeAOT, an AMD Ryzen AI
7 PRO 350 with eight cores and sixteen logical processors. The native runtime
enumerated an AMD Radeon 860M, while ASR explicitly used CPU. The capture
adapter and hardware encoder identities were not logged. Pre-run free physical
memory was 21578508 KiB;
commit safeguards were unchanged. The base repository revision was
`88d41c5705dff5667f22d9e45f567b01a008c2bd` plus the spike corrections, whose
actual executable/helper hashes are recorded below.

| Input or artifact | SHA-256 |
| --- | --- |
| Native ASR DLL | `220B0ED18769C6CEF3929F4B6A4FC6468189E911CAE92F7895E24987C02DC022` |
| Public WAV | `A8096A0C2AC136BCAF70143DC80BA576ADE8DFBF7F9F30ECDEC29B920AB3E3CB` |
| Nemotron ASR Q8 | `C58B62C1BDD6C5D7B14B08126C5BD16C1289FEE6E0D6AC0761DBD3AA6D714935` |
| Corrected capture helper | `3B82F2A06C74FEEF683CEE16EB2902F6B8357954CDDE85189EF9417A4528DEB7` |
| Overhead NativeAOT executable | `2AA95206C2AE9CCEC4DCC26B235238841C04D508D69984F94760133F86C06C9D` |

The standalone self-check passed summary arithmetic and the 88-byte ABI layout.
The full core suite passed 1467 tests with no failures or skips after the native
helper/interop correction. `Publish-Ansible.ps1` passed and its extracted app
ran under a fresh isolated package identity. All owned work exited and joined.
