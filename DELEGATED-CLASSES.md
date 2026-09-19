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
| Which Zoom account opens it | One of that coordinator's own Zoom accounts, which their copy of the app keeps on the server |
| The meeting link | That Zoom account's link for the group — nobody types it twice |
| Which LMS account finishes it | That coordinator's, kept encrypted on the server |
| What it opens with | Auto, the Zoom app, or the browser — per class, or set for every class shown |

## Both accounts are already theirs

Every copy of the app sends what that PC has to the signed-in person's dashboard account:

* their **LMS sign-in**, with the password AES-GCM encrypted on the server;
* their **Zoom accounts** — which group each one hosts and the link its classes open. No Zoom
  sign-in is sent: that stays in the Zoom app's saved accounts or a browser profile on their PC.

So a coordinator sets their own app up once, and the admin picks from what they actually have
instead of typing a link or an account name a second time.

## Turning a coordinator on

Either **Dashboard → Run classes**, or the tick box on the app's own **Coordinators & groups** page
(`Run their classes`). Both write the same thing on the server. Each coordinator is listed with
their groups, the LMS sign-in their classes go up under, and which of their Zoom accounts opens
them; where they have more than one, the admin chooses. **Stop running theirs** takes every class of
theirs off the admin's PC again at the next pass.

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

## When something is still owed

The LMS lists when a class is, not how to join it, so the link comes from that coordinator's Zoom
account for the group. A class whose group has no Zoom account of theirs is the only one that still
asks: it is shown as needing a link rather than failing at its time, both on the Dashboard
(`4 still need a Zoom link`) and in the app's status line. Filling it in on one class and pressing
**Whole group** writes it to every class of that group that has not happened yet, and a link put on
by hand is never overwritten when the timetable is read again.

The admin's PC then has to have that Zoom account signed in. It is found by the name the coordinator
knows it by, or - when it was added here under another name - by the e-mail it signs in to Zoom with.
Failing both, the class says so by name (`this PC has no Zoom account called "CAI5_AIS4_S7" and none
signed in as mona@zoom.example.com`) instead of failing quietly.

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
| `GET /api/v1/admin/users/{id}/zoom-accounts` | that coordinator's Zoom accounts and their links |
| `POST /api/v1/admin/users/{id}/lms-accounts/{aid}/secret` | the sign-in itself, audited |
| `GET /api/v1/admin/run-plan?from=&to=&coordinator=` | the classes to run; `coordinator` may repeat |
| `POST /api/v1/admin/run-plan/import` | what a coordinator's LMS listed |
| `PATCH /api/v1/admin/run-plan/{id}` | its link, Zoom account, what it opens with, or skipping it |

Each person's own app keeps its accounts with `GET`/`PUT /api/v1/me/zoom-accounts` (the whole set:
an account removed on their PC stops being offered here) and the existing `/api/v1/me/lms-accounts`.

Migrations `0009_delegated_runs` (`run_delegations`, `class_plans`) and `0010_zoom_accounts`
(`zoom_accounts`, and the delegation column naming one); nothing existing is touched. Re-importing a timetable never duplicates a class and never overwrites what a person put on
it — the LMS knows the timetable, and this side knows how a class opens.

## What was checked, and what needs a real machine

Run by the tests: the server's whole contract (33 cases), the app's side of it — whose account each
class uses, two coordinators side by side, finding their Zoom account here by name or by the e-mail
it signs in with, a missing link or account, turning somebody off, reading each timetable with its
own sign-in (15 cases) — the group-to-account file (8 cases), sending this PC's Zoom accounts up
(7 cases), the Schedules page's coordinator column, filter and engine choice (7 cases), and the
Dashboard page (13 cases).

Run as a dry run against a real server: a throwaway PostgreSQL migrated from nothing to `0010`, the
backend under uvicorn on `127.0.0.1`, and the whole journey over real HTTP — both coordinators'
accounts saved by their own sign-ins, the admin ticking them, a sign-in refused before and handed
over after, both timetables imported, every class arriving with its link and its account already
filled in, two 19:00 classes with different pairs, the filter, skipping, a hand-written link
surviving a re-read, turning somebody off closing their sign-in again, and a coordinator refused all
of it. Nothing touched Zoom or the LMS.

Not run by the tests, because they need a real desktop with Zoom and the LMS signed in: the LMS
session list actually being read for a second coordinator, two meetings genuinely live at once on one
PC, and the attendance of both going up under the right names. Those are still a smoke test on the
real machine.
