# Session Role Management

Automatic co-host assignment per session type. The module is isolated: it observes the existing
meeting lifecycle events, reuses the existing participant monitoring and UI automation read-only,
and modifies none of the Auto Admit engine, WaitingRoomTester, Attendance collector, Attendance
matching, Scheduler or admission flow. If it fails, the meeting is unaffected.

## Where things live

| Piece | Project / file |
| --- | --- |
| Models, storage, session-type resolver, matcher | `src/ZoomAutoAdmit.SessionRoles/` |
| Co-host assignment through UIA | `src/ZoomAutoAdmit.SessionRoles/CoHostAssignment.cs` |
| Lifecycle observer | `src/ZoomAutoAdmit.SessionRoles/SessionRoleBridge.cs` |
| Schedule-name lookup (read-only) | `src/ZoomAutoAdmit.Inspector/Runtime/ScheduleNameSource.cs` |
| Editing screen | Session Roles page (`Views/SessionRolesView.xaml`) |
| Storage | `%LOCALAPPDATA%\ZoomAutoAdmit\Roles\session-roles.json` |

## Profiles

One profile per session type (Technical, Soft Skill, English…), each with keywords, instructors and
co-host candidates. A person may carry aliases — the Zoom display names already seen for them.
People are configured once; nobody is picked per meeting.

## Session type detection

Two ways, in this order:

1. **Account / group binding** — a profile may list the account IDs it belongs to (picked from the
   saved accounts, so an ID is never mistyped). That is explicit, so it wins, and it works even
   when the meeting has no schedule name.
2. **Keywords in the schedule name** — imported names look like
   `CAI5_AIS4_S7 • 27 • Intro to Python`; the longest matching keyword wins. If several profiles
   claim the same account, the schedule name decides between them.

A meeting that matches neither stays **untyped**: the module assigns nothing and logs
`[ROLE] Session type not recognised`, rather than guessing a profile.

## What happens during a meeting

1. Lifecycle publishes `Active` → the bridge loads the profile for the detected session type.
2. Every 20 seconds it reads the participants through the existing participant source (no new
   detector, no second UIA reader of its own).
3. Each observed display name is matched: configured name → approved alias → previous successful
   assignment → name rule → **AI confirmation**. The name rule covers the display names Zoom decorates
   (`Mohab Mohamed __Coordinator` for a configured `Mohab Mohamed`): it uses the existing rule
   matcher, accepts only near-certain scores, and never accepts a single first name. Unmatched
   names are left alone and re-checked next pass.
4. A match triggers co-host assignment, then the result is verified from Zoom itself.
5. A successful assignment is remembered (`{sessionType, person, observed name, role, confidence,
   approved}`) so the next session matches without any AI call.
6. Lifecycle publishes `Ending` → the watcher stops.

Any one of the configured people is enough; the module never waits for all of them.

## Assignment flow (UI automation only)

`Participants panel (opened via its own toolbar button if closed)` → `Participant list` →
the row whose cleaned label equals the person → that row's `More options for <name>` split button
(`Invoke`) → the `Make co-host` menu item (`Invoke`) → verification by re-reading the row label for
`(Co-host…)`, or the accessibility announcement `Co-host permission granted to <name>`.

No OCR, no image coordinates, no fixed mouse positions, no keystrokes. The panel may be docked or
floating. If `Make co-host` does not appear, the row menu is closed again and the attempt is
reported as failed — Zoom is left as it was found.

## AI confirmation

When the local rules cannot settle a Zoom display name, the module picks the *suspects* — the
configured people whose names overlap it — and asks the AI one question per suspect: "is this
observed name this person?", using the same validated matcher and stored key as attendance
matching. The AI's role is to confirm a suspect, never to search:

- it may only answer about people already configured for that session type;
- an answer naming anyone else is discarded and logged;
- an answer that requests review, or scores below 85, is logged as a suggestion and **not** assigned;
- a confirmed match is remembered, so the same Zoom name never needs the AI again.

It runs only when an AI key is saved (the AI Matching page). Without a key the module simply
matches less and assigns nothing extra.

## Desktop notifications

Outcomes appear as a small toast at the bottom-right of the screen, outside the app window, on top
of whatever is open, without stealing focus, fading away after a few seconds:

- **Co-host assigned** — who was assigned, from which Zoom name, and how it was matched.
- **No co-host found** — raised once per meeting when every participant has been checked (including
  by the AI) and none of them is on that session type's profile; it lists the names that were checked.
- **Co-host assignment failed** — Zoom did not confirm the change.

They are informational only: nothing in the module depends on a toast being shown.

## Safety rules

- Co-host is granted only to a person already configured for that session type.
- Rows under the Waiting room header are never eligible.
- An already-co-host row is reported as success without touching the menu (idempotent).
- Failures retry on the next pass instead of escalating; nothing else about the meeting changes.

## Logs

`[ROLE] Session type loaded`, `[ROLE] Session type not recognised`, `[ROLE] Instructor detected`,
`[COHOST] Participant matched`, `[COHOST] Assignment started`, `[COHOST] Assignment successful`,
`[COHOST] Assignment failed` — each with `sessionId=`.

## Not implemented yet

- **AI fallback.** Matching stops at the name rule. The seam is `RoleMatcher.Match`
  returning null; an AI proposal must never assign by itself — it may only suggest a person who is
  already on the profile, for a human to approve.
- **Instructor detection beyond configuration and history** (screen-sharing signal, ranking).
- **"Add this participant from the current meeting"** shortcut on the Session Roles page.
