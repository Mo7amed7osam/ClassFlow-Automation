# Standalone attendance snapshots

`src/ZoomAutoAdmit.Attendance` is a Windows-only library wired into the production
`WindowsRuntimeBootstrapper` (both UI and meeting-start CLI). It collects raw
participant presence; it does not calculate absence, match students, grade, or
generate reports. Existing admission decisions, WaitingRoomTester, switching and
scheduling rules are unchanged. Orchestration/runtime adapters now publish lifecycle
events; engines only publish admission notifications at their existing success sites.

## Ownership and integration boundary

Create one `AttendanceCollector` per session. Supply an `IAttendanceParticipantSource`
and `JsonAttendanceSnapshotStore`. The collector owns only its timer and capture
serialization. It never owns/disposes Zoom windows, Playwright pages, browser
contexts, profiles, or admission engines.

`MeetingLifecycleEvents` is owned by the bootstrapper. The orchestrator publishes
Active after preparation and before starting Auto Admit, and Ending before stopping
the runtime. Active startup failure, monitor-task exit and bootstrapper disposal
also clean up attendance. `AttendanceLifecycleBridge` serializes queued admissions
per session, drains them before the final capture, and unsubscribes on disposal.

Admission notifications carry the session ID via an AsyncLocal scope inherited by
the existing monitor tasks. They do not parse global console logs, infer identity
from display names, or block the clicking loop on attendance reads. Notification
hooks preserve existing engine success semantics; in particular the legacy Desktop
background single-admit path reports success after click, without a separate join
verification. This integration does not strengthen or weaken that existing logic.

Direct library callers can still use explicit hooks:

```csharp
await using var collector = new AttendanceCollector(session.SessionId, source,
    new JsonAttendanceSnapshotStore());

// From the host's lifecycle boundary when the meeting becomes Active:
await collector.OnMeetingStateChangedAsync(session.SessionId, MeetingState.Active, token);

// After a confirmed admission, from the host's session-specific event boundary:
await collector.OnAdmissionAsync(session.SessionId, token);

// Optional manual action:
await collector.CaptureManualAsync(token);

// Before disposing the meeting page/window, so the final roster is still readable:
await collector.OnMeetingStateChangedAsync(session.SessionId, MeetingState.Ended, token);
```

`ObserveAsync(session, token)` alternatively polls the existing `MeetingSession.State`
once a second. Both Active and Monitoring start collection (Active can be transient).
Observer cancellation stops collection. Observation is best-effort: transitions
shorter than one poll may be missed, and an Ended state observed after Zoom closes
cannot yield a fresh end roster. Prefer explicit hooks for precise boundaries.

Production startup now creates the bridge automatically. `bootstrapper.Attendance
.CaptureManualAsync(sessionId)` exposes manual capture without adding a new UI
screen. Existing standalone engine debug commands outside the orchestrator do not
have a session scope and do not start attendance. No matching or absence services
are introduced.

`RuntimeAttendanceSources` binds the existing primary Web page and the one Desktop
Zoom process to exposed, semantically named Joined/In-meeting lists. It rejects
missing or ambiguous lists rather than opening panels or clicking controls. The
bootstrapper accepts `attendanceSources` and `attendanceStore` overrides for exact
UIA IDs/DOM scopes, alternate layouts and tests. Defaults are deliberately conservative;
actual Zoom layouts must expose such a list or need an explicit binding. The lifecycle
is wired even when a participant read fails: the collector logs failure and continues.

The existing runtime must report end (EndAsync, monitor exit or application shutdown).
This change adds no new Zoom meeting-end detector. If Zoom has already closed the
page/window, final capture logs failure instead of inventing current attendance.

## Capture behavior

- One start snapshot; repeated Active/Monitoring notifications are idempotent.
- Periodic captures every 15 minutes while running (TimeProvider is injectable).
- One capture for each forwarded admission event and each manual request.
- One final capture attempt on Stop/Ended/Failed/disposal; then the timer stops.
- Calls are serialized per collector; different collectors share no lock/state.
- Calls before start/after stop do not read participants. Foreign session events
  are rejected. A stopped collector is not restarted; create one for a new session.
- Read/storage failures log `Snapshot failed` and do not save invented empty rosters
  or terminate Auto Admit. Subsequent interval/event/manual requests can retry.
- Names are preserved verbatim, including duplicate names and UI suffixes. Names
  are observations, not stable identities. Only Joined-list names are collected;
  Waiting Room occupants and menu/button text are not attendance.

## Desktop reader

`DesktopAttendanceParticipantSource` takes the actual meeting HWND/process ID and
inspected **Joined list AutomationId** and **participant name AutomationId**.
It uses a dedicated STA/UIA3 connection and the existing `FlaUiElementExtractor`.
It validates window ownership and list uniqueness, reads only that subtree, and
never clicks, scrolls, invokes, hovers, or opens the Participants panel.

These IDs must be obtained from the current Zoom UIA layout. No IDs, meeting HWNDs,
or screen coordinates are guessed/hard-coded. A detached Participants window can
be supplied instead if it owns the Joined list. Missing IDs/panel, ambiguous scope,
or a traversal error fail the capture. Native UIA calls cannot be interrupted while
inside COM; cancellation is checked between traversal calls, so shutdown can wait
for a stalled provider. No repeated abandoned worker threads are created.

## Web reader

`WebAttendanceParticipantSource` takes the **existing primary IPage** (or existing
`ZoomBrowserSession`) and a `Func<IPage, ILocator>` resolving only the Joined list.
The host supplies the exact list/iframe scope for the current Zoom layout. Row/name
selectors can be supplied when constructing with IPage; default name selector
families follow the existing Web DOM reader but do not normalize or deduplicate.

The resolver is called for every capture to tolerate frame/DOM replacement on the
same page. It must derive locators from that supplied page, never open or choose
another page. Missing, hidden, or multiple Joined lists fail the capture. A detached
frame fails only that snapshot; the next capture resolves again on the same page.
DOM evaluation only reads row/name data. There is no OCR or pointer interaction.

## Completeness and live validation

Zoom may virtualize participant lists or hide them when collapsed. Both concrete
readers mark snapshots `isComplete: false` with a note: they record exposed rows,
not a guaranteed full roster. Zero exposed names are treated as unavailable, not
proof of zero attendees. A trusted custom source may provide a complete empty
roster. No last-known roster is relabeled as a current meeting-end snapshot.

Real Zoom validation is still needed to select the correct Joined-list UIA/DOM scope
and assess exposure/virtualization in each layout. Tests use fake participant sources,
UIA data trees and mocked Playwright pages; no login or live meeting is launched.

## Storage and logs

Default: `%LOCALAPPDATA%\ZoomAutoAdmit\Attendance\<sessionId>\<timestamp>-<uniqueId>.json`.
Each immutable JSON file has `sessionId`, UTC `timestamp`, `source` (Desktop/Web),
`participants: [{"name": "raw name"}]`, `trigger`, `reason`, `isComplete`, and `note`.
Integrated snapshots also include `meeting`: account ID, account display name,
meeting URL without query/passcode/fragment, scheduled start and selected engine.
Files are written to unique temporary files, flushed and moved without overwrite.
No credentials, meeting URL passcodes, account storage mutations or external services are used.
Participant names are local personal data: storage inherits the Windows user's
directory permissions, with no automatic retention/deletion or extra encryption.

### Failed captures

A read that fails (participants panel closed, an ambiguous or unexposed Joined list, a
storage error) is written next to the snapshots as
`<timestamp>-<uniqueId>.issue`: `sessionId`, `timestamp`, `trigger`, `reason`. It is a
record that a capture was attempted and why it failed — never attendance, and never an
empty participant list. The Attendance page shows these for the selected session so a
silent gap is visible instead of looking like an empty meeting.

### Whole-session matching

The Attendance page merges every snapshot of the selected session by default (the union of
names, keeping the highest count seen for a repeated name), because one snapshot only shows
who was on screen at that moment. Unticking "Match the whole session" falls back to the
single selected snapshot. Merging never invents a name and never marks anyone absent.

Logs use existing ConsoleLogger routing and include a session ID:
`[ATTENDANCE] Collector started`, `Snapshot captured`, `Participants count`,
`Collector stopped`. Snapshot logs include `Reason: MeetingStart / AdmitEvent /
Scheduled / Manual / MeetingEnd`. Logging does not include participant names.

Run tests with:

```powershell
dotnet test Windows/tests/ZoomAutoAdmit.Attendance.Tests/ZoomAutoAdmit.Attendance.Tests.csproj
```
