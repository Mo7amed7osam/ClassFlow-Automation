# One PC, several coordinators' classes

The admin's PC opens and finishes classes for the coordinators the admin chooses, and every class
goes up under its own coordinator's name: their Zoom account opens the meeting, their LMS account
presses Run Session, writes the attendance and puts the recording link on it.

Two classes at the same time are the ordinary case, not the awkward one. Nothing about a class is
read from a global setting any more, so a 19:00 class of one coordinator and a 19:00 class of
another never wait for each other and never go up under the wrong person.

## What a class carries

| | Where it comes from |
|---|---|
| When it is | That coordinator's own LMS session list, read signed in as them |
| Which group | The same list |
| Which Zoom account opens it | Chosen by the admin, per coordinator (their Zoom account, on the admin's PC) |
| The meeting link | Filled in once per group — the LMS does not publish it |
| Which LMS account finishes it | That coordinator's, kept encrypted on the server |
| What it opens with | Auto, the Zoom app, or the browser — per class, or set for every class shown |

## Turning a coordinator on

**Dashboard → Run classes** (admin only). Each coordinator is listed with their groups and the LMS
sign-in their classes would go up under. **Run their classes** turns them on; **Stop running theirs**
takes every class of theirs off the admin's PC again at the next pass.

The admin's PC then, every five minutes:

1. asks the server who is turned on;
2. reads each of their LMS sign-ins and keeps it like any other account on that PC — in Windows
   Credential Manager, with its own browser profile, and never as the account the app falls back to;
3. records which groups belong to whom, so every later step can ask "whose class is this?";
4. reads each coordinator's LMS timetable (every six hours, or at once from **Read coordinators'
   timetables** on the Schedules page) and sends it to the server as their plan;
5. turns each planned class into a schedule on that PC, on its exact date, which opens by itself
   fifteen minutes before its time like any other.

Reading the timetables happens for up to three coordinators at once, each in their own browser
profile, so one person's read never waits behind another's.

## The one thing a person still fills in

The LMS lists when a class is, not how to join it. A class with no Zoom link is shown as needing one
rather than failing at its time, both on the Dashboard (`4 still need a Zoom link`) and in the app's
status line. Filling it in on one class and pressing **Whole group** writes it to every class of that
group that has not happened yet, and the next timetable read carries it on to new classes of the same
group. The same is true of the Zoom account that opens the meeting: set once per coordinator, it is
inherited by each of their classes.

If the named Zoom account is not on the admin's PC, the class says so by name
(`this PC has no Zoom account called "CAI5_AIS4_S7"`) instead of failing quietly.

## Two at once

* **Zoom.** The existing allocation policy already gives the first live class the Zoom desktop app
  and every later one the web engine, each with its own browser profile (up to eight per account).
  Nothing was added for this; delegated classes simply go through the same path.
* **The LMS.** Each account has its own browser profile, and the profile lock is taken per profile
  name. Two coordinators' LMS steps therefore run side by side instead of queueing, and neither
  signs the other out. A class's dashboard profile is now its group's, not whichever account was
  "in use".
* **Attendance.** Snapshots are taken per live meeting as before. A group with no roster on the PC
  has its students read from the LMS **with that group's own account**, so a coordinator's roster is
  fetched by the account that can actually see it.

## Whose class is this?

`%LOCALAPPDATA%\ZoomAutoAdmit\Lms\class-accounts.json` maps each group to its coordinator and their
LMS account. It holds no password — only which account, whose it is, and which groups. Everything
that touches the LMS for a class asks it first and falls back to the account in use for a group
nobody claimed, which is exactly what a PC running only its own classes has always done.

A coordinator turned off is forgotten there at once, and a group they no longer have stops being
theirs on the next pass, so a class never goes up under somebody who lost it.

## The Schedules page

Every class shows its **Coordinator** and what it **Opens with**. **WHOSE** narrows the list to one
person (or to this PC's own classes) and works together with the day filter. **Use for all shown**
sets what every class in the filtered list opens with, which is how "all of this coordinator's
classes use the browser" is said.

Editing a delegated class by hand leaves it theirs: its coordinator survives the edit.

## Reading a coordinator's LMS sign-in

The admin already sets these passwords, so no separate approval by the coordinator is asked for.
What the server does insist on:

* the request comes from the admin, over the dashboard session, with `X-Dashboard-Request: 1`;
* that coordinator has been turned on — a sign-in is never handed out as a side effect of listing
  accounts;
* every read is written to `admin_audit_log` with who read whose, and the answer is never cached and
  never logged.

Turning a coordinator off closes it again immediately.

## The server's part

| | |
|---|---|
| `GET /api/v1/admin/delegations` | every coordinator, their groups and sign-ins, and who is turned on |
| `PUT /api/v1/admin/delegations/{id}` | run theirs (or stop), with the LMS and Zoom account |
| `GET /api/v1/admin/users/{id}/lms-accounts` | that coordinator's sign-ins — never a password |
| `POST /api/v1/admin/users/{id}/lms-accounts/{aid}/secret` | the sign-in itself, audited |
| `GET /api/v1/admin/run-plan?from=&to=&coordinator=` | the classes to run; `coordinator` may repeat |
| `POST /api/v1/admin/run-plan/import` | what a coordinator's LMS listed |
| `PATCH /api/v1/admin/run-plan/{id}` | its link, Zoom account, what it opens with, or skipping it |

Migration `0009_delegated_runs` adds `run_delegations` and `class_plans`; nothing existing is
touched. Re-importing a timetable never duplicates a class and never overwrites what a person put on
it — the LMS knows the timetable, and this side knows how a class opens.

## What was checked, and what needs a real machine

Run by the tests: the server's whole contract (25 cases), the app's side of it — whose account each
class uses, two coordinators side by side, a missing link or Zoom account, turning somebody off,
reading each timetable with its own sign-in (13 cases) — the group-to-account file (8 cases), the
Schedules page's coordinator column, filter and engine choice (7 cases), and the Dashboard page (11
cases).

Not run by the tests, because they need a real desktop with Zoom and the LMS signed in: the LMS
session list actually being read for a second coordinator, two meetings genuinely live at once on one
PC, and the attendance of both going up under the right names. Those are still a smoke test on the
real machine.
