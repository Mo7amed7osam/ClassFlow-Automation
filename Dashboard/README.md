# Dashboard (V3: the admin and the coordinators)

An internal web page for the recordings, the agents and class attendance, used by one admin and
the coordinators.
The existing FastAPI backend serves it under `/dashboard/`. It reads the existing PostgreSQL
database through `/api/v1/dashboard/*`, `/api/v1/auth/*` and `/api/v1/admin/*`. There is no second
server and no second database. The jobs API, the agent WebSocket, device registration and the n8n
recording API are unchanged.

## Roles

| | Admin (one account) | Coordinator |
|---|---|---|
| Sees | Every group, recording, agent and job | Only the groups the admin gave them, and those groups' recordings |
| Recordings | Edit, attach to the LMS, move to any group | Edit and attach within their groups; move a recording only between their own groups |
| Agents page | Yes | No (the backend answers 403) |
| Users page | Approve/reject registrations, create coordinators, assign groups, disable/enable, set a new password | No |
| Run classes page | Turn a coordinator on so the admin's PC opens and finishes their classes under their own Zoom and LMS accounts; fill in each group's Zoom link; choose what a class opens with, or skip it | No |
| Groups page | All groups, their coordinators; add, label, archive | "My groups" |
| Attendance, Students | Every group's sessions and rosters | Their groups' sessions and rosters, with the same tools |

A coordinator asking for anything outside their groups gets the same `404` as for something that
does not exist. The rule lives in one place on the backend (`access.py`), and every dashboard
query goes through it. A change to someone's groups, or disabling them, counts from their next
request.

| Page | Shows |
|---|---|
| Overview | Admin: agents online, pending / on-LMS / no-link counts, jobs. Coordinator: their groups, pending, on LMS, without a link. Both: the 10 latest recordings they may see. |
| Recordings | Group, date, start time, file name, source (Drive / Zoom / Missing), LMS status, updated, and Actions (Edit, Attach LMS). Filters: group, session date, LMS status, source. Sort: last updated (default), first seen, session date. 25 per page. A row opens its details. |
| Groups / My groups | Each group: recordings, last recording date, pending, on LMS, without a link, last updated (the admin also sees coordinators, archived groups, and the actions) |
| Users (admin) | Registrations waiting for approval, then every account with its status, groups, last sign-in and actions |
| Agents (admin) | Each device: online/offline, idle/busy, last heartbeat, assigned jobs, successes and failures in the last 24 h, recent jobs |
| Account | Your name, role, groups, when the session ends; change your password |
| Attendance | Class meetings whose attendance was taken (by the Windows agent, or created by hand): group, date, Live/Ended/Finalized, present / needs review / absent. Filters: group, date, status. |
| Attendance › a session | Present / needs review / absent / unmatched counts, then three tabs: **Students** (status, Zoom name, confidence and why, joined, left, minutes in the meeting), **Review** (only the doubtful ones, with Confirm / Not them, and the Zoom names nobody has with the best student to give each to), **Zoom names** (every name seen; mark one "Not a student"). Buttons: Match again, Match with AI (when the server has it), Add names (paste), Export CSV, Finalize / Reopen. Refreshes every 15 s while live. |
| Students | A group's roster: add, edit, remove (past attendance stays), import (paste from Excel/LMS or a CSV, previewed first), and each student's remembered Zoom names (forget one). |

**Groups** come from the recordings: the first time n8n syncs a recording for a new group, the
group is registered by itself. The admin can also add one ahead of time, give it a label, or archive
it (its coordinators stop seeing it; its recordings are kept). A group's name never changes.

**Recording operations** (Recordings page):
* **Edit.** A dialog with group, date, start time, file name, type and link. It sends only what
  changed. A changed link resets the LMS status to Pending, and the dialog warns about it. For a
  coordinator the group is a list of their own groups.
* **Attach LMS.** A confirmation, then `{"replaceExisting": false, "dryRun": false}` unless "Dry run"
  or "Replace an existing link" is ticked. It shows the job ID and status.
* **Details.** A drawer with every field, the jobs made from the recording and its history (who
  changed what). The address becomes `?recording=<id>`.

LMS status badges: Pending (yellow), Attached (green), Failed (red), and Processing (blue) while an
attach job is queued or running. The link status says only which link is stored and where the
recording stands on the LMS; it does not check that the link opens.

**Attendance corrections.** "Change" on a student picks their Zoom name from the session's names (a
name another student holds says so and moves), marks them absent or present without a Zoom name, or
hands them back to automatic matching. A change made by a person is kept when the session is matched
again, and — unless "Remember" is unticked — is remembered for the group's next sessions (a "Not
them" is remembered too, so that name is not offered for that student again). How matching works is
in [Backend/README.md](../Backend/README.md#attendance-rosters-zoom-snapshots-matching).

## Accounts and sign-in

The dashboard never uses the n8n API key, and the browser never holds one.

1. Apply the migrations (`0005_users_and_groups` adds `users`, `groups`, `user_groups` and fills
   `groups` from the existing recordings). Use the backend's virtual environment,
   `.venv\Scripts\python`, not a bare `python` (see [Backend/README.md](../Backend/README.md#run-it-locally)):
   ```powershell
   cd Backend
   .venv\Scripts\python -m central_backend.cli migrate
   ```
2. Create the admin once. The password is asked for twice and never shown:
   ```powershell
   .venv\Scripts\python -m central_backend.cli create-admin --username admin --display-name "Your name"
   ```
   If you used `CENTRAL_ADMIN_USERS` before, move that account into the database instead with
   `create-admin --from-env` (if it held several accounts, name the one to keep with `--username`;
   the others are listed, not imported), then remove the variable. The backend no longer reads it,
   and warns at start-up while it is still set. It also warns while there is no admin.
3. Restart the backend. Existing sign-ins end once, because sessions now belong to a user.
4. Coordinators open `/dashboard/register` ("Request one" on the sign-in page). Their account waits
   until you approve it on **Users**, where you also give them their groups. You can create an
   account there yourself too; it is active at once. `CENTRAL_ALLOW_REGISTRATION=false` turns
   self-registration off.

Accounts are `pending` (registered, waiting), `active`, `rejected` or `disabled`; only `active` ones
can sign in. The sign-in page says which it is, but only after the right password.

**How it is protected:**
* Passwords: at least 12 characters, stored as scrypt hashes. An unknown username takes as long as a
  wrong password.
* A sign-in creates a random session token. The browser keeps it in a cookie its scripts cannot read
  (HttpOnly), which is never sent from another site (SameSite=Strict). Outside development it is also
  Secure and uses the `__Host-` name prefix. The database stores only the token's SHA-256.
* Every request reads the user again, so disabling an account, rejecting it, or setting a new
  password for it signs that person out at once. Changing your own password signs out your other
  browsers.
* After 5 failed attempts for a name from one address, sign-ins are refused for 15 minutes.
  Registrations are limited to 5 per hour per address.
* There can be only one admin: the database enforces it (a unique index), and no route can make one.
  The admin account cannot be disabled or given groups from the page.
* Every POST, PATCH and PUT needs the header `X-Dashboard-Request: 1`, which blocks forged
  cross-site requests.
* The three kinds of credential are separate: the n8n key does not open the dashboard, and a
  dashboard session does not open the n8n API.
* Every change (to recordings, accounts and groups) is written to `admin_audit_log` with the user.
* The page is sent with a strict Content-Security-Policy (`default-src 'self'`, no inline scripts,
  no framing), `Referrer-Policy: no-referrer` and `X-Frame-Options: DENY`. Recording links open in a
  new tab and are not printed in the tables.

## Build and serve

```powershell
cd Dashboard
npm install
npm run build          # type-checks, then writes Dashboard/dist
```

The backend serves `Dashboard/dist` at `http://<backend>/dashboard/` when the folder exists; if
it doesn't, the backend runs without the page. To use another folder, set `CENTRAL_DASHBOARD_DIST`.
Restart the backend after the first build so it notices the folder.

## Develop

Start the backend on `127.0.0.1:8765` in development mode, then run:

```powershell
cd Dashboard
npm run dev            # http://127.0.0.1:5173/dashboard/  (/api is forwarded to the backend)
```

If the backend is elsewhere, set `DASHBOARD_BACKEND_URL`.

Tests run with `npm test` (Vitest + Testing Library), and the type check with `npm run typecheck`.

## Stack and layout

React 19, TypeScript, Vite, TanStack Query (fetching, caching, polling), React Router, Tailwind CSS.

```
src/api/        client.ts (same-origin fetch, session cookie, X-Dashboard-Request), hooks.ts, types.ts,
                attendance.ts (rosters and attendance: types and hooks)
src/components/ Layout (menu by role), RecordingsTable, ui (badges, buttons, cards, time-ago),
                Overlay (Modal, Drawer), Toast, EditRecordingModal, AttachRecordingModal, RecordingDrawer,
                CancelJobModal, UserModals (new coordinator, groups, new password, confirm)
src/pages/      Login, Register, Account, Overview, Recordings, Groups, Users, Agents,
                Attendance, AttendanceSession, Students
src/lib/        format.ts: times in Cairo time; session dates exactly as stored
```

## Backend endpoints used

### Sign-in (`/api/v1/auth/*`)

| Endpoint | |
|---|---|
| `POST /api/v1/auth/register` | `{username, displayName, password}` → `201 {username, status: "pending"}`. `409` username taken, `403` registration closed, `429` too many. |
| `POST /api/v1/auth/login` | `{username, password}` → sets the cookie, `{username, displayName, role, expiresAt}`. `401` wrong name or password; `403 {"details": {"reason": "pending" \| "rejected" \| "disabled"}}`; `429`. |
| `POST /api/v1/auth/logout` | Ends this session. |
| `GET /api/v1/auth/me` | `{id, username, displayName, role, allGroups, groups, expiresAt}`; `groups` is the coordinator's list, `null` for the admin. |
| `POST /api/v1/auth/password` | `{currentPassword, newPassword}`; other sessions end. |

### Data (any signed-in user, filtered by role)

| Endpoint | |
|---|---|
| `GET /api/v1/dashboard/overview` | Recording counts, and `groups`; `agents` and `jobs` are `null` for a coordinator. |
| `GET /api/v1/dashboard/recordings?group=&date=&status=&link=drive\|zoom\|missing&sort=updated\|created\|session&page=&pageSize=` | `{items, total, page, pageSize, sort}`; each item is a recording plus `linkStatus {link, lms, label}` and `lastJob`. |
| `GET /api/v1/dashboard/recordings/{id}` | One recording, the jobs made from it, and its history. `404` outside the viewer's groups. |
| `GET /api/v1/dashboard/groups` | `{groups: [{id, group, displayName, archived, recordings, lastSessionDate, lastUpdatedAt, pending, onLms, missingLink}]}`. `lastSessionDate` is `null` for a group without recordings. |
| `GET /api/v1/dashboard/agents`, `/agents/{deviceId}/jobs` | **Admin only.** Devices and their jobs. Job summaries never include the recording link. |
| `PATCH /api/v1/dashboard/recordings/{id}` | Edit `group, date, startTime, fileName, type, link, lmsStatus`, with the same rules as `PATCH /api/v1/recordings/{id}`. Answers the item plus `changed`. `403` when a coordinator moves it to a group that is not theirs; `404`; `409` clash. |
| `POST /api/v1/dashboard/recordings/{id}/attach` | `{"replaceExisting": false, "dryRun": true}` (**`dryRun` defaults to true**; real JSON booleans). Creates the same `recording.process` job as `POST /api/v1/jobs`, linked to the recording. `202 {recordingId, jobId, status, dryRun, replaceExisting}`; `409 {"error":"Cannot attach","details":{"reason": "noLink" \| "zoomOnly" \| "jobInProgress"}}`. |

| `POST /api/v1/dashboard/recordings/{id}/cancel` | Cancels the recording's queued attach job. `409 {"reason": "noOpenJob" \| "alreadyStarted"}` once an agent has taken it. |
| `/api/v1/dashboard/students*`, `/api/v1/dashboard/attendance/*` | Rosters and attendance: see [Backend/README.md](../Backend/README.md#attendance-rosters-zoom-snapshots-matching). |

When an attach job finishes, the recording's `lmsStatus` becomes `attached` or `failed` (a final
failure, including `agentLost`). A dry run, a retryable failure, or a job whose link changed
meanwhile leaves it as it was.

### The admin's tools (`/api/v1/admin/*`, admin only)

| Endpoint | |
|---|---|
| `GET /api/v1/admin/users?status=&role=` | `{users: [{id, username, displayName, role, status, createdAt, approvedAt, lastLoginAt, groups}], count, counts}`; pending first. |
| `POST /api/v1/admin/users` | `{username, displayName, password, groupIds}` → `201`, an active coordinator. |
| `GET` / `PATCH /api/v1/admin/users/{id}` | `PATCH {displayName?, status?: "active" \| "disabled"}`. Disabling signs them out. `409` for the admin account. |
| `POST /api/v1/admin/users/{id}/approve`, `/reject` | A pending registration (a rejected one can still be approved). |
| `POST /api/v1/admin/users/{id}/password` | `{password}`: a new password for a coordinator; they are signed out. |
| `PUT /api/v1/admin/users/{id}/groups` | `{groupIds}`: the coordinator's groups, as a whole set. Archived groups are refused. |
| `GET /api/v1/admin/groups` | Every group (archived too) with its counts and `coordinators`. |
| `POST /api/v1/admin/groups` | `{name, displayName?}` → `201`. |
| `PATCH /api/v1/admin/groups/{id}` | `{displayName?, archived?}`. |
| `GET /api/v1/admin/delegations` | Every coordinator with their groups, their LMS sign-ins (never a password), whether this PC runs their classes, and how many of those still need a Zoom link. |
| `PUT /api/v1/admin/delegations/{coordinatorId}` | `{enabled, lmsAccountId?, zoomAccount?}`: run their classes, or stop. |
| `GET /api/v1/admin/run-plan?from=&to=&coordinator=` | The classes to run. `coordinator` may be repeated, which is how the page narrows to a few people. |
| `PATCH /api/v1/admin/run-plan/{id}` | `{meetingUrl?, zoomAccount?, preferredEngine?, status?, applyToGroup?}`. `applyToGroup` writes the link to every class of that group that has not happened yet. |

The page never asks for a sign-in: the password stays on the server and goes only to the PC that
runs the classes. See [DELEGATED-CLASSES.md](../DELEGATED-CLASSES.md).
