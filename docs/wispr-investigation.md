# Installed Wispr Flow investigation

Measured: Static inspection on 2026-10-09 covered installed Wispr Flow
`app-1.6.1102`, its packaged `.webpack/main/index.js`, and the native helper.
Measured: No personal calendars, transcript databases, credentials, or application
logs were read, and the helper was not executed for this investigation.

Measured: The inspected `resources/app.asar` SHA-256 is
`5A35C867858FD0C14C643BF3530B0BA928869FF01A1383A437EB255DB4969A74`.
Measured: Native analysis used `resources/Release/Wispr Flow Helper.exe`,
20,228,632 bytes, SHA-256
`c58c61b106c83f7b77efc921b0e849bb7210154898504215f17306c1a6d6abe0`,
with PE image base `0x140000000`; native addresses below refer to that binary.
Measured: JavaScript locations below are decoded-string character offsets inside
`.webpack/main/index.js`, not absolute ASAR byte offsets; the inspection script
uses .NET `Substring`.

## Calendar integration

Measured: The connector catalog exposes **Google Calendar** and **Outlook
Calendar** separately at offset `2629160`; it does not obtain calendar events
from Teams accessibility elements.

Measured: `AriaWebClient.syncCalendar` requests `GET /api/v1/calendar/sync` with
optional `since`, `cursor`, and `launch` parameters at offset `3551577`.
Measured: `CalendarSyncManager` joins concurrent synchronization, fetches all
cursor pages, stores the server time for incremental requests, and handles a
server mirror revision that invalidates the local cache at offsets
`3884296`–`3888000`.

Measured: The client upserts cached event identifiers, titles, UTC start/end
times, conference URLs, participants, response status, organizer status, and
connector keys at offset `3888100`; cancelled events evict reminders.
Measured: Synchronization requires sign-in, an allowed calendar policy, and an
active online client outside test mode.
Inferred: OAuth/calendar-provider access occurs behind the connector/backend
boundary; this client code does not establish whether the server uses Microsoft
Graph subscriptions, polling, or another provider mechanism.

Measured: Reminder sweeps run at seconds `2,17,32,47` of the minute, with the
selected notification timing determining which sweep is used, at offset
`3882403`.
Measured: This is a reminder schedule, not evidence of a 15-second calendar
network synchronization interval.

## Detecting a joined meeting

Measured: Native bridge requests include `GetMicrophoneHolders`,
`SetMicrophoneHoldersMonitor`, `GetNativeCallSnapshot`, `GetMeetingTabScan`, and
`GetConferenceEndState` at offsets `4460598`–`4462300`.
Measured: Windows holder transport declares `session_plus_registry` detection
and `audio_session`/`consent_store` sources at offset `2481846`.

Measured: The holder classifier at offset `5730347` prioritizes
`process_api` and `audio_session` as trusted sources; other sources can produce
an untrusted fallback.
Measured: Except for `process_api`, a snapshot with `deviceInUse=false` does not
identify an active platform; the client excludes its own executable.
Measured: Native Teams identifiers include `ms-teams.exe`, `teams.exe`, and
several Teams package identifiers at offset `2754100`.

Measured: A change to a recognized microphone-holder family reaches the
new-meeting notice path at offset `5508058`, with `trusted_holder` or
`guessed_holder` evidence; calendar correlation happens afterward.
Measured: The inspected JavaScript path does not universally require a Teams
UIA “joined” control before creating this notice.
Inferred: Opening Teams alone is insufficient for the trusted path, while
microphone use is evidence of an active audio workflow rather than proof of a
particular scheduled meeting.

Measured: Calendar correlation first honors an explicit pending event/join
intent, then refreshes the foreground URL, then matches conference codes against
active cached events at offsets `5436200`–`5439600`.
Measured: Code matching has no hard time bound; duplicate matches favor an
event spanning now, then the nearest start/end boundary.
Measured: The native-app time-only fallback in this function is **Zoom**, with
one minute before/after the event; ambiguous candidates remain unmatched.
Measured: This inspected function has no equivalent native Teams time-only
fallback.
Measured: Browser correlation can retry three times, 400 ms apart, before an
unmatched notice is shown at offset `5477400`.

Measured: Polling defaults to five seconds without a tracked recording and
three seconds while tracking, or 30 seconds when the pushed native monitor is
healthy and enabled; the tracked monitor requests a 500 ms cadence at offsets
`5491338` and `5508473`.
Measured: Observation sequence/generation checks reject superseded pushed
snapshots at offset `5922000`.

## Grace periods and automatic recording

Measured: Default preferences enable `autoDetectMeetingsEnabled` and disable
`notetakerAutoStartEnabled` at offsets `2887033` and `2887938`.
Measured: Actual auto-start also checks its feature flag, trusted/platform
evidence, onboarding, permissions, recording state, and cloud-sync access at
offset `5475234`; guessed holders and explicit user-intent joins are excluded
from this auto-start eligibility branch.
Inferred: A detection notice and permission to record automatically are separate
product decisions.

Measured: The fallback loss policy uses two missing-holder polls plus five
seconds of grace when a final transcript arrived within the last minute; without
that recent final it uses one poll and zero additional grace at offset
`5485018`.
Measured: Loss processing separately checks a 1,500 ms absence confirmation at
offsets `5500800` and `5648580`.
Measured: The newer fusion policy includes a 30-second backstop, 10-second
device/restart/wake/helper guards, a 1-second corroborated window, and a
3.5-second high-confidence window at offset `5648300`.
Measured: Transcript liveness, UIA/tab evidence, recording identity, recovery,
and feature flags can suppress, delay, or replace these paths.
Inferred: These constants must not be presented as a single unconditional
“Wispr stops after five seconds” rule or as measured live latency.

## Speaker names, audio clocks, and cloud refinement

Measured: Native helper disassembly shows case-insensitive exact Teams UIA
matches for `Active speaker` at address `140449610`, and `Video on`/`Video off`
at `140449520`; address `140400540` removes a comma-delimited `Screen sharing`
badge.
Measured: These are speaker/participant extraction predicates; an exact native
Teams join/in-call predicate was not established by this inspection.

Measured: The helper mixer at `14032C3E0` combines 640-sample PCM16 frames,
clamps the summed samples, and emits 1,280-byte frames; its timeline at
`14032C080` uses a 32,000-sample ring and fills absent positions with silence.
Measured: `140330180` derives positions from QPC time relative to an origin,
with 250 ms holdback and a `0.0016` multiplier for 100 ns units; `14032FFE0`
converts QPC ticks using `10,000,000 / frequency`.
Measured: Packet reader `14032CEC0` uses `IAudioCaptureClient` packet reads and
passes their QPC position, rather than device position, to `14032D800`; it bounds
each packet to 32,000 samples and each drain pass to 100 packets.
Inferred: At 16 kHz these frames represent 40 ms, and the native path aligns
microphone/playback audio before mixing.

Measured: JavaScript `handleTokens` associates final system-stream speech with
the latest accessibility event at or before the token’s reconstructed start
epoch; observations older than 1,500 ms do not supply a name, at offset
`5294300`.
Measured: Interim tokens have no speaker attribution, microphone and system
streams keep distinct speaker namespaces, and a system speaker identity map
receives valid name evidence.
Inferred: Name attribution depends on both the recognition speaker identity and
a sufficiently fresh UI observation; a visible name alone is not diarization.

Measured: The packaged client requests
`/api/v1/meetings/{id}/speaker-observations/upload-url` for NDJSON at offset
`3518813`, and the refinement coordinator contains a speaker-observation upload
to S3 path at offset `6022036`.
Measured: It also contains live-transcript uploads and cloud refined-transcript
retrieval paths at offsets `3520009` and `3514598`.
Inferred: Wispr’s final refinement combines cloud processing with client-side
speaker observations; copying its client UIA logic cannot reproduce an unknown
server pipeline.

Measured: The specific backend recognition/refinement LLM, server diarization
algorithm, real-world accuracy, and complete cloud/audio clock reconciliation
were not established.
Measured: No meeting was joined and no real speech was recorded to validate
these static paths; current rollout flags and personal preferences were not
read.

## Reproducing the static evidence

Measured: The local inspection utility is
`.runtime/wispr-inspection/Inspect-Archive.ps1`; native analysis outputs are
`.runtime/wispr-native-analysis/target-code-headless.log` and
`.runtime/wispr-native-analysis/follow-calls-headless.log`.
Measured: These ignored artifacts contain generated static-analysis output,
not the user’s Wispr application logs, and are not shipped in this repository.

```powershell
.\.runtime\wispr-inspection\Inspect-Archive.ps1 -Offset 5730347 -Count 1100
.\.runtime\wispr-inspection\Inspect-Archive.ps1 -Offset 5436200 -Count 3400
.\.runtime\wispr-inspection\Inspect-Archive.ps1 -Patterns @('calendar/sync') -Limit 1
```

## Recommendation for a later Ansible calendar feature

Inferred: Use delegated Microsoft Graph `calendarView` for an Outlook date
range, then cache the events locally and correlate their conference URLs with
independent call evidence; calendar time alone should not start recording.
Measured: Microsoft documents recurring occurrences, exceptions, and single
events in this endpoint, with `Calendars.ReadBasic` as the least-privileged
delegated permission for the documented call. See
[Microsoft Graph calendarView](https://learn.microsoft.com/en-us/graph/api/user-list-calendarview?view=graph-rest-1.0).
Measured: This investigation does not implement calendar access, initiate OAuth,
or change Ansible’s manual meeting-start scope.
