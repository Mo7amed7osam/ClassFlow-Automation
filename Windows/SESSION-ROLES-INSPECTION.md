# Session Role Management — UI Automation inspection report

Read-only survey of what the existing Windows automation already gives this module, what is
still unverified on Windows, and how the module should be built. **No module code written yet.**

Scope rule for the whole module: it must not modify the Auto Admit engine, WaitingRoomTester,
Attendance Collector, Attendance Matching, Scheduler, meeting lifecycle, or the admission flow.
It integrates only through `MeetingLifecycleEvents` (the same optional event bus the attendance
bridge already uses) and reuses existing classes without editing them.

## 1. Available today (verified in this repository)

| Capability | Where it lives | Notes |
| --- | --- | --- |
| Find the Zoom process and its windows | `ZoomProcessDiscovery`, `ZoomWindowManager` | `FindMainZoomMeetingWindow()`, `FindParticipantsWindow()`, `ClassifyZoomWindow()`, `IsActiveMeetingPresent()` |
| Read the UIA tree on the interactive desktop | `ZoomTreeInspector`, `FlaUiElementExtractor`, `DesktopThread.RunOnInteractiveDesktop` | Captures Name, AutomationId, ControlType, bounds and pattern support (`Invoke`, `ExpandCollapse`, …) |
| Match menu-like elements | `MeetingElementMatcher`, `ProfileButtonMatcher` | Already recognises `MenuItem` control types and SplitButton + InvokePattern invocation |
| Click without stealing focus or moving the user's cursor | `CursorPreservingHover`, `SyntheticHoverActivator`, `HoverThenSingleClickExecutor`, `SingleClickExecutor`, `BackgroundZoomInteraction`, `ForegroundWindowPreserver` | This is the input path the module must reuse — no fixed coordinates, no OCR clicking |
| Keep a long participants list usable | `ParticipantsPanelScrollRecovery` | Needed to reach a participant that is scrolled out of view |
| Read the joined participants list | `RuntimeAttendanceSources` (Desktop/Web), `WebAttendanceParticipantSource` | The module reuses these classes read-only as the participant monitor; it never edits them and never writes attendance |
| Name matching and alias memory patterns | `NameNormalizer`, `RuleBasedNameMatcher`, `JsonAliasMemory`, `OpenAiNameMatcher`, `OpenRouterNameMatcher` | Reused as libraries. Roles get their OWN storage files so attendance matching is untouched |
| Live diagnostics | Inspector CLI: `inspect`, `find "<term>"`, `uia-hwnd-inspect --hwnd`, `processes`, `meeting-watch` | Read-only commands already shipped |

## 2. Available on macOS only — indicative, not proof for Windows

The captured accessibility dumps in this repo (`zoom-accessibility.txt`, `zoom-cg-off-space.txt`)
are **macOS AX trees**, not Windows UIA. They show the shape we hope Windows mirrors:

- `AXOutline description="Participants list"` containing rows,
- group rows `"Waiting room (1)"` and `"Joined (1)"`,
- per participant `AXMenuButton description="More options for <name>"` with `AXPress`,
- `AXButton description="More options to manage all participants"`.

A per-row More menu exists on macOS. That is the element the co-host flow depends on, so the
Windows equivalent is the single most important thing to confirm.

## 2b. Confirmed on Windows (live capture, 1 Sep 2026, meeting running, panel closed)

A live `inspect` run against a real meeting settled several unknowns:

| Element | Result |
| --- | --- |
| Meeting window | `Zoom Meeting`, class `ConfMultiTabContentWndClass`, **PID 24764** |
| Zoom processes present | **5** (Workplace 21436, meeting 24764, notes 23148, sharing frames 25296/4832) |
| Participants toolbar button | `[Button] "Participants, open panel, 1 participants, Alt+U"` — **InvokePattern: yes** |
| Toolbar participant count | Exposed inside that button's own name |
| Panel-level more menu | `[MenuItem] "More options for participants"` — InvokePattern: yes |
| Host tools | `[Button] "Host tools, open panel"` — InvokePattern: yes |
| Role wording in video tile | `[Pane] "eyouth coordinator(Host, me), Computer audio muted, …"` |
| Tree depth | Truncated at depth 15 — the probe therefore runs at depth 25 |

Two consequences follow immediately:

1. **Attendance's process assumption was wrong.** It required exactly one process named `Zoom`;
   five exist, and the meeting lives in a different one from the main Workplace window. The
   Desktop source now binds to whichever process owns the meeting/participants window.
2. **The Joined list only exists while the panel is open**, and the toolbar button that opens it
   is invokable through UIA. Attendance now invokes that button once when the list is missing,
   waits, and re-reads — no coordinates, no keystrokes, no cursor movement.

## 2c. Everything the co-host flow needs — CONFIRMED on Windows

A second live probe, with the participants panel open (floating window, not docked) and a
co-host already granted, captured every remaining element. All of it is plain UIA:

| Need | Windows element | Pattern |
| --- | --- | --- |
| Participants button | `[Button] "Participants, open panel, 1 participants, Alt+U"` → reads `"Participants, close panel, 2 participants…"` when open | Invoke |
| Participants list | `"Participant list, use arrow key to navigate…"` (one per meeting window) | — |
| Section headers | `[ListItem] "Waiting room (1), Expanded"`, `[ListItem] "Joined (1), Expanded"` | SelectionItem |
| Participant row | `[ListItem] "Mohab Mohamed __Coordinator,(Guest), Computer audio muted,Video off…"` | SelectionItem |
| Per-row More menu | `[SplitButton] "More options for <participant name>"` | **Invoke** |
| Panel-level menu | `[SplitButton] "More options to manage all participants"` | Invoke |
| Make co-host action | `[MenuItem] "Make co-host"` under `Zoom Meeting > Menu > Pane > Pane` | **Invoke**, Value |
| Assignment result | row label becomes `…,(Co-host, guest)…`, and `[Text] "(Co-host, guest)"` appears in the row | — |
| Assignment announcement | `[Unknown] "Co-host permission granted to <name>"` (offscreen accessibility alert) | — |
| Opposite action (for safety checks) | `[MenuItem] "Put in waiting room"` — never invoked by this module | — |

So the whole flow — open panel → find row → invoke that row's More → invoke "Make co-host" →
verify by re-reading the row label and the announcement — is achievable with UIA `Invoke` only.
No OCR, no coordinates, no cursor movement, and the panel may be docked or floating.

### What this also fixed in attendance

The same capture showed why attendance never recorded anything: the desktop reader was looking
for a list named `Joined (N)`, but Windows exposes **one** list named `Participant list, use
arrow key to navigate…` with `Waiting room (N)` / `Joined (N)` rows as *section headers* inside
it. The reader now walks the rows in order, keeps only the rows under the Joined header (waiting
room is never attendance), and trims each label at its role/status tail, so
`Mohab Mohamed __Coordinator,(Guest), Computer audio muted…` is stored as
`Mohab Mohamed __Coordinator`.

## 3. Not verified on Windows — must be measured in a live meeting

All six items are now confirmed in §2c. Only one open question remains:

- whether this Zoom build ever shows a modal **confirmation dialog** for co-host. The probe saw
  the permission granted with no dialog, only the accessibility announcement. The implementation
  must therefore treat a confirmation dialog as optional: handle it when present, never wait for it.

Two findings raise the risk that some of these are *not* exposed on Windows:

- The existing waiting-room row admission deliberately uses OCR geometry plus hover
  (`WaitingRoomParticipantRowDetector` is pure OCR) instead of UIA row buttons. That choice
  suggests Zoom for Windows does not reliably expose per-row buttons through UIA.
- The attendance Desktop source expects a `ControlType.List` named `Joined (N)`. On this PC it
  has never produced a snapshot (no `%LOCALAPPDATA%\ZoomAutoAdmit\Attendance` folder exists at
  all), so that expectation is unconfirmed in practice.

## 4. How to measure it (read-only, one live meeting)

With a meeting running and the participants panel open:

```powershell
$P = "Windows\src\ZoomAutoAdmit.Inspector\ZoomAutoAdmit.Inspector.csproj"
dotnet run --project $P -- inspect > roles-tree.txt          # whole tree, read-only
dotnet run --project $P -- find "Participants"
dotnet run --project $P -- find "More"
dotnet run --project $P -- find "Joined"
```

Then open one participant's **More** menu by hand and immediately run:

```powershell
dotnet run --project $P -- find "Co-host"
dotnet run --project $P -- processes        # gives the popup window handles
dotnet run --project $P -- uia-hwnd-inspect --hwnd 0x<popup handle>
```

The answers to §3 fall straight out of those four outputs.

### `roles-probe` (added — read-only)

A dedicated command now bundles the six checks into one report. It only reads the tree the
existing inspector already produces: no click, hover, cursor move or state change.

```powershell
dotnet run --project Windows\src\ZoomAutoAdmit.Inspector\ZoomAutoAdmit.Inspector.csproj -- roles-probe
```

It prints and saves `%LOCALAPPDATA%\ZoomAutoAdmit\Logs\roles-probe-<timestamp>.txt` with, for each
candidate element, its control type, name, AutomationId, class, enabled/offscreen state, supported
patterns, bounds and tree path — then a FOUND/MISSING verdict per required element. Run it twice:
once with the participants panel open, and once more with a participant's **More** menu opened by
hand, so the menu items and any confirmation dialog are captured.

## 5. Recommended implementation approach

**Module boundary.** New project `ZoomAutoAdmit.SessionRoles`, referenced only by the app host and
the UI. It subscribes to `MeetingLifecycleEvents.Lifecycle` (Active / Ending) and
`AdmissionVerified`, exactly like `AttendanceLifecycleBridge`. No existing class is edited; the
orchestrator, engines and schedulers stay untouched. If the module throws, the meeting is
unaffected (observers already swallow their own failures).

**Storage** — independent, `%LOCALAPPDATA%\ZoomAutoAdmit\Roles\session-roles.json`:
session types, instructor and co-host candidates per type, approved aliases, and assignment
history. Roles never write into the attendance or roster stores.

**Participant monitoring.** Reuse `RuntimeAttendanceSources.Create(context)` read-only on its own
polling loop. No new detector, no shared state with the attendance collector.

**Matching order** (first hit wins; AI is last and never decides alone):
1. exact configured name (normalised via `NameNormalizer`),
2. approved alias from the roles store,
3. previous successful assignment for the same person and session type,
4. AI confidence — only to *propose*. Below the threshold, or when several candidates tie, the
   module records a suggestion for a human to approve instead of assigning.

**Learning.** On a successful assignment, store `{personId, alias, sessionType, role, confidence,
approved}` in the roles store, so the next session matches without an AI call.

**Instructor detection.** Configured instructor → history for that session type → screen sharing
as a *signal only* → AI fallback returning `{probableInstructor, confidence, reason}`.

**Assignment flow (UI Automation only).** Panel open → locate row → open row More menu → invoke
"Make Co-host" (prefer `InvokePattern`; fall back to the existing hover-then-single-click executor
with the element's own bounds, never fixed coordinates) → confirm dialog if present → verify by
re-reading the row. No OCR clicking, no image coordinates.

**Safety rules to keep:** co-host is granted only to a person already on that session type's
candidate list; the AI can rank candidates but cannot introduce someone; every attempt is
idempotent (skip if the row already reads co-host) and logged as
`[ROLE] Session type loaded`, `[ROLE] Instructor detected`, `[COHOST] Participant matched`,
`[COHOST] Assignment started`, `[COHOST] Assignment successful`, `[COHOST] Assignment failed`.

**Session type detection (decided).** The type is derived from the schedule name — the imported
names look like `CAI5_AIS4_S7 • 27 • <topic>`. The roles store keeps, per session type, a list of
keyword rules matched against that name (case- and diacritic-insensitive, longest rule wins), plus
an explicit fallback type. A name that matches no rule leaves the session untyped: the module then
assigns nothing and logs `[ROLE] Session type not recognised`, rather than guessing a profile.
