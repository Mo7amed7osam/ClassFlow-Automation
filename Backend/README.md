# Central Backend (Phase 1)

> **Phase 1 foundation — not production-ready.** It has not been deployed and has no TLS setup of
> its own. It runs as a single instance only. See [ARCHITECTURE.md](ARCHITECTURE.md) for the
> design and the Phase 2 list.

The public-facing part of Zoom Auto Admit:
* n8n submits **jobs** over HTTPS.
* Windows PCs running the app connect **out** to it over a WebSocket as **agents**, and it hands
  each job to one of them.
* The backend never automates a browser. Zoom and the LMS are driven only by the Windows agent,
  which reuses the app's existing recording workflow.

```
n8n ──HTTPS, X-API-Key──▶ backend ◀──WSS, device token── Windows agent (outbound only)
                            │                              └─ RecordingLinkProcessor → DEPI LMS
                            └─ PostgreSQL
```

## Run it locally

Requires Python 3.11+ (tested with 3.13 and 3.14) and PostgreSQL 14+ (tested with 18).

```powershell
cd Backend
py -3.13 -m venv .venv
.venv\Scripts\python -m pip install -r requirements-dev.txt    # the server only: requirements.txt
```

Run every backend command from `Backend\` with the virtual environment's Python,
`.venv\Scripts\python`, as all the commands below do. Alternatively, activate it first with
`.venv\Scripts\Activate.ps1`, so that `python` means the venv. A bare `python` is the system
interpreter, which has none of these packages. `ModuleNotFoundError: No module named 'sqlalchemy'`
(or `fastapi`, `alembic`) means the command ran outside the venv. `No module named 'central_backend'`
means it ran outside `Backend\`.

`requirements.txt` pins the exact versions the tests pass with; `requirements-dev.txt` adds pytest and
httpx. `pyproject.toml` holds the same dependencies as version ranges.

### PostgreSQL

Pick one:

* **An installed PostgreSQL**: create a database and a user for it:
  `createdb -U postgres central`. The URL is then
  `postgresql+asyncpg://postgres:<password>@127.0.0.1:5432/central`.
* **Docker**:
  `docker run -d --name central-pg -e POSTGRES_PASSWORD=dev -p 127.0.0.1:5433:5432 postgres:16`, and use
  `postgresql+asyncpg://postgres:dev@127.0.0.1:5433/postgres`.
* **A throwaway cluster** with the installed binaries, with no password and loopback only. This is
  what the tests do on their own:
  `initdb -D .\pgdata -U postgres -A trust` and then
  `pg_ctl -D .\pgdata -o "-p 5433 -c listen_addresses=127.0.0.1" -l pg.log start`.

### Configure, migrate, start

```powershell
.venv\Scripts\python -m central_backend.cli new-api-key      # prints a key for n8n and its hash
$env:CENTRAL_DATABASE_URL = 'postgresql+asyncpg://postgres@127.0.0.1:5433/postgres'
$env:CENTRAL_CLIENT_API_KEY_HASHES = '<the hash printed above>'
$env:CENTRAL_ENVIRONMENT = 'development'                        # allows plain http on this PC
.venv\Scripts\python -m central_backend.cli migrate
.venv\Scripts\python -m uvicorn central_backend.main:create_app --factory --host 127.0.0.1 --port 8080 --ws-max-size 65536
```

`GET http://127.0.0.1:8080/health` → `{"status":"ok"}`. API docs are at `/docs`, in development only.

### The dashboard's admin account (once)

```powershell
.venvScriptspython -m central_backend.cli create-admin --username admin --display-name "Your name"
```

The password is asked for twice and never shown. There is exactly one admin; coordinators register
on the sign-in page (the admin approves them) or are created by the admin. An old `CENTRAL_ADMIN_USERS`
entry can be moved into the database with `create-admin --from-env` (with several entries, pick one
with `--username`); the variable is no longer read
after that. See [Dashboard/README.md](../Dashboard/README.md).

### Register a Windows agent

```powershell
.venv\Scripts\python -m central_backend.cli create-enrollment-token --label PC-01 --ttl-hours 24
```

Give the printed `zaae_…` token to whoever sets up the PC. It works once and expires. The Windows
side is described in [Windows/CENTRAL-AGENT.md](../Windows/CENTRAL-AGENT.md).

To stop a device's token from working:
`python -m central_backend.cli revoke-device <deviceId>`.

### Tests

```powershell
.venv\Scripts\python -m pytest
```

The tests start their own temporary PostgreSQL cluster from the binaries on this PC. They look in
`CENTRAL_TEST_PG_BIN`, then PATH, then `C:\Program Files\PostgreSQL\*\bin` and `/usr/lib/postgresql/*/bin`.
Alternatively, point `CENTRAL_TEST_DATABASE_URL` at a **disposable** database: every table in it is
emptied between tests. No LMS, Zoom or browser is used.

## Settings (environment)

| Variable | Default | Meaning |
|---|---|---|
| `CENTRAL_DATABASE_URL` | — (required) | `postgresql+asyncpg://…` |
| `CENTRAL_CLIENT_API_KEY_HASHES` | — (required) | Comma-separated **SHA-256 hashes** of the API keys n8n may use. Never the keys themselves. |
| `CENTRAL_ENVIRONMENT` | `production` | `development` allows plain http/ws and serves `/docs`. In `production`, every request that is not https/wss (as the proxy reports it) gets `403 HTTPS required`. |
| `CENTRAL_HEARTBEAT_INTERVAL_SECONDS` | `30` | Told to agents in `welcome`. |
| `CENTRAL_DEVICE_STALE_SECONDS` | `90` | Silence after which a device is offline. Must be ≥ 2 × heartbeat. |
| `CENTRAL_ASSIGNMENT_ACK_TIMEOUT_SECONDS` | `60` | An assignment not accepted in this time goes back to the queue. |
| `CENTRAL_RUNNING_ORPHAN_TIMEOUT_SECONDS` | `1800` | A running job whose device is silent this long fails with `agentLost`. |
| `CENTRAL_BUSY_RETRY_DELAY_SECONDS` | `60` | Default wait before retrying a retryable failure (such as `busy`). |
| `CENTRAL_SESSION_HOURS` | `8` | How long a dashboard sign-in lasts (0.25-72). The old name `CENTRAL_ADMIN_SESSION_HOURS` still works. |
| `CENTRAL_ALLOW_REGISTRATION` | `true` | `false` refuses coordinator self-registration (the sign-up page says so); the admin then creates every account. |
| `CENTRAL_DASHBOARD_DIST` | `../Dashboard/dist` | Where the built dashboard is; without it the backend runs without the page. |
| ~~`CENTRAL_ADMIN_USERS`~~ | — | Retired: accounts live in the `users` table. If still set, the backend logs a warning and ignores it. |
| `CENTRAL_AI_API_KEY` | — | Optional. Turns on "Match with AI" for attendance (an OpenAI-compatible chat-completions API; OpenRouter by default). Without it that button is hidden and the step answers `409`. |
| `CENTRAL_AI_MODEL` | `openai/gpt-4o-mini` | The model asked. |
| `CENTRAL_AI_BASE_URL` | `https://openrouter.ai/api/v1` | The API's base address. |

Behind a TLS-terminating reverse proxy, start uvicorn with
`--proxy-headers --forwarded-allow-ips=<proxy address>`, so the scheme it sees is https/wss.

## API for n8n

Authentication: `X-API-Key: <key>` (or `Authorization: Bearer <key>`) on every `/api/v1/jobs` and
`/api/v1/devices` call. A wrong or missing key gets `401 {"error":"Unauthorized"}`, checked before
the body is read.

### `POST /api/v1/jobs` → `202`

```http
POST /api/v1/jobs
X-API-Key: zaak_…
Idempotency-Key: sheet-AST5_DAT1_S1-2026-09-03        (optional, recommended)
Content-Type: application/json

{
  "type": "recording.process",
  "payload": {
    "group": "AST5_DAT1_S1",
    "recordLink": "https://drive.google.com/file/d/<file id>/view?usp=sharing",
    "date": "2026-09-03",
    "replaceExisting": false
  }
}
```

```json
{ "jobId": "55e61782-e19b-4738-8869-f3f5572c8aab", "status": "queued" }
```

* Payload fields: `group`, `recordLink` and `date` are required. `startTime` (`HH:mm`),
  `replaceExisting` and `dryRun` (both default `false`) are optional. The rules are those of the
  local recording API: see [Windows/RECORDING-API.md](../Windows/RECORDING-API.md) §9.
* `date` is **required** here, unlike the local API. A queued job may run later, so "today" would be
  ambiguous.
* `400 {"error":"Invalid request","details":…}` explains what is wrong. It never echoes what was sent.
* Idempotency: the same `Idempotency-Key` with the same job returns
  `200 {"jobId":…, "status":…, "duplicate":true}`, and no second job is created. Reusing the key
  for a different job is `409`.

### `GET /api/v1/jobs/{jobId}` → `200`

```json
{
  "jobId": "55e61782-…", "type": "recording.process", "status": "succeeded",
  "deviceId": "fa6b79b2-…", "payload": { … },
  "result": { "group": "CAI5_AIS4_S7", "date": "2026-09-01", "alreadyExists": true,
              "message": "CAI5_AIS4_S7: the session already has a recording link, so it was left as it is." },
  "error": null, "attempts": 1, "maxAttempts": 3,
  "createdAt": "…Z", "assignedAt": "…Z", "startedAt": "…Z", "finishedAt": "…Z", "updatedAt": "…Z"
}
```

`status` is one of `queued`, `assigned`, `running`, `succeeded`, `failed`, `cancelled`.

On failure, `error` holds `code`, `message` and `retryable`:

| `code` | What happened |
|---|---|
| `sessionNotFinished`, `sessionNotFound`, `lmsNotSignedIn`, `invalidLink`, `lmsFailed` | Reasons from the LMS step on the agent. |
| `busy` | The PC's dashboard was busy. It is retried automatically, up to 3 attempts. |
| `invalidPayload` | The agent refused the payload. |
| `agentRestarted` | The agent was restarted in the middle of the job. |
| `agentLost` | The device vanished while running the job. |

For `agentRestarted` and `agentLost`, check the LMS before resubmitting. A repeat with
`replaceExisting:false` is safe.

### Other endpoints

* `POST /api/v1/jobs/{jobId}/cancel`: `200` for a queued job (and repeating it is harmless);
  `409` once it has been assigned.
* `GET /api/v1/devices`: every device with `status` (online/offline), `connected`,
  `agentState`, `version`, `capabilities` and `lastHeartbeat`.
* `GET /health`: no key.

### n8n example (HTTP Request node)

* **Submit:** `POST https://<backend>/api/v1/jobs`.
  * Authentication: Header Auth, with `X-API-Key` set to the key.
  * Header: `Idempotency-Key` = `{{ $json.group }}-{{ $json.date }}`.
  * JSON body:
    `{{ JSON.stringify({ type: "recording.process", payload: { group: $json.group, recordLink: $json.link, date: $json.date, replaceExisting: false } }) }}`.
* **Then poll:** `GET https://<backend>/api/v1/jobs/{{ $json.jobId }}` every 15–30 s (a Wait node
  in a loop) until `status` is `succeeded` or `failed`.

## Recordings (metadata from the recordings sheet)

The same client key as the jobs API. These endpoints only **store** what n8n reports: they create
no jobs and do not touch the LMS. `lmsStatus` is `pending` until a later step moves it on.

| Endpoint | |
|---|---|
| `POST /api/v1/recordings/sync` | Create the session's recording, or update it if one with the same **group + date + startTime** exists. |
| `GET /api/v1/recordings` | All recordings, newest date first. Optional `?group=`, `?date=yyyy-MM-dd`, `?status=` (the `lmsStatus`), `?limit=` (1–5000, default 1000), `?offset=`. Answers `{"recordings":[…],"count":n}`. |
| `GET /api/v1/recordings/latest` | The most recently created or updated recordings, `updatedAt` newest first. `?limit=` 1–100, default 20; anything else is `400`. Each item has `id, group, date, startTime, fileName, type, driveLink, zoomLink, source, lmsStatus, updatedAt`. Answers `{"recordings":[…],"count":n}`. |
| `GET /api/v1/recordings/{id}` | One recording, or `404`. |
| `PATCH /api/v1/recordings/{id}` | Edit only the fields sent: `group`, `date`, `startTime`, `fileName`, `type`, `link`, `lmsStatus` (`pending`/`attached`/`failed`). `""` or `null` clears an optional field; `group`, `date` and `lmsStatus` cannot be emptied. A new `link` replaces the stored one (Drive → `driveLink`, Zoom → `zoomLink`, the other cleared), sets `source`, and puts `lmsStatus` back to `pending`; asking for another `lmsStatus` in the same request is `400`. Moving onto another recording's group + date + start time is `409`. Unknown fields `400`, unknown id `404`. Answers the recording (`id, group, date, startTime, fileName, type, driveLink, zoomLink, source, lmsStatus, updatedAt`). |
| `GET /api/v1/groups` | `{"groups":[…],"count":n}`: the distinct group names that have recordings, sorted. |

Sync body (unknown fields are refused):

```json
{ "group": "CAI5_AIS4_S7", "date": "2026-09-11", "startTime": "18:00",
  "fileName": "session.mp4", "type": "recording", "link": "https://drive.google.com/file/d/<id>/view" }
```

**Fields:**
* `group` and `date` (`yyyy-MM-dd`) are required.
* `startTime` (`HH:mm`), `fileName`, `type` and `link` are optional. An empty string counts as not
  given, because empty sheet cells arrive as `""`.
* `link` must be a Google Drive file link (stored as `driveLink`) or a Zoom recording link,
  `https://…zoom.us/rec/share|play/…` (stored as `zoomLink`). Anything else is refused.
* `source` is `drive` when a Drive link is stored, otherwise `zoom`.
* The date and time are stored exactly as sent. The sheet's file names are in UTC, so send the
  session's own (Cairo) date and time if they should match the LMS.

**Answers:** `201 {"action":"created","recording":{…}}` or `200 {"action":"updated","recording":{…}}`.

**On update:**
* Fields that are given replace the stored ones; fields left out keep their values.
* A changed link resets `lmsStatus` to `pending`, since the LMS would then hold an outdated link.
* No start time is one slot for the day: two syncs of the same group and date without `startTime`
  update the same row.

## Attendance (rosters, Zoom snapshots, matching)

Who attended each class meeting, matched against the group's roster. It replaces the Chrome
extension's attendance and the Windows app's local matching with one shared place: rosters, name
memory and results live in PostgreSQL (migration `0006_attendance`, additive: six new tables), and the
same access rules apply as for recordings (a coordinator only sees their groups).

```
Zoom meeting ─▶ Windows app (reads the participant list; files in %LOCALAPPDATA%\ZoomAutoAdmit\Attendance)
             ─▶ agent (AttendanceUploader) ─ POST /api/v1/attendance/snapshots, device token
             ─▶ backend: participants + presence intervals ─▶ matching ─▶ attendance records
             ─▶ dashboard: Attendance / Students pages (review, correct, finalize, export CSV)
```

**Snapshots in.** `POST /api/v1/attendance/snapshots`, with a device token (`Authorization: Bearer
zaad_…`, sent by the agent) or the n8n key:

```json
{ "clientSnapshotId": "win:<session>:<file>",
  "session": { "ref": "win:<windows session id>", "group": "CAI5_AIS4_S7", "date": "2026-09-14",
               "startTime": "18:02", "meetingUrl": "https://us06web.zoom.us/j/…" },
  "capturedAt": "2026-09-14T15:17:00Z", "source": "desktop", "trigger": "scheduled",
  "isComplete": false, "ended": false, "participants": ["Mohab Osama", "مريم عبدالرحمن"] }
```

* `ref` finds the session again (a new one is made the first time; a `ref` of another group is
  `409`). Without `ref`, group + date + start time do. The group is registered if new.
* A `clientSnapshotId` already stored answers `200 {"duplicate": true}` and changes nothing, so the
  agent can safely send again. Otherwise `201 {sessionId, participants, status, summary}`.
* Names are stored raw; Zoom's "(Host, me)", "(Co-host)", "(مضيف)" mark staff, who are set aside.
  `isComplete: false` (the Zoom list may have been scrolled) never counts as someone leaving.
  `ended: true` closes the session.
* A meeting link loses its query (the passcode) before it is stored. At most 1000 names per read.

**Matching** (`attendance_names.py`, `attendance_matching.py`): the Windows app's name rules
(first/family names, spelling variants, Arabizi) and the extension's fuzzy similarity, with their
known gaps fixed: Arabic letter forms, diacritics and tatweel, Arabic-Indic digits, honorifics, and
compound names spelled together or apart (عبد الرحمن / Abdelrahman / Abd El Rahman, Nour El Din /
Noureldin). A student is matched at most once and a Zoom name given to at most one student; a name
two students fit almost equally is *Needs review* for both. One word alone ("Ahmed") never makes a
student present by itself. What a person confirms or rules out is remembered for the group's next
sessions, and their decisions always win over the automatic ones. With `CENTRAL_AI_API_KEY`, the
remaining doubtful names can be put to an AI; only its confident answers are applied, the rest are
shown as suggestions.

Each record is `present`, `needs_review` or `absent`, with the Zoom name(s), a confidence 0-100,
where it came from (`exact`, `alias`, `memory`, `rule`, `fuzzy`, `ai`, `manual`), and the join time,
leave time and time in the meeting (from the reads, so no finer than their spacing).

**Dashboard endpoints** (signed-in users; writes need `X-Dashboard-Request: 1`; a coordinator gets
`404` outside their groups):

| Endpoint | |
|---|---|
| `GET /api/v1/dashboard/students?group=&q=&includeInactive=` · `POST …/students` · `PATCH …/students/{id}` | The roster. Removing a student keeps their past attendance. |
| `POST /api/v1/dashboard/students/import` | `{group, text, dryRun}`: pasted rows or a CSV (tab, `;` or `,`), with or without a header (Name/Email/ID/Order, English or Arabic). Adds and updates, never removes; `dryRun` previews. |
| `GET …/students/{id}/aliases` · `DELETE /api/v1/dashboard/aliases/{id}` | The remembered Zoom names of a student; forget one. |
| `GET /api/v1/dashboard/attendance/sessions?group=&date=&status=&page=` · `POST …/sessions` | Sessions with their counts; create one by hand. |
| `GET …/sessions/{id}` | `{session, records, participants (with the best candidates), snapshots, summary, aiAvailable}`. |
| `POST …/sessions/{id}/participants` | `{names}`: names pasted by hand, matched like a read. |
| `POST …/sessions/{id}/match` | `{useAi}`: match again (and ask the AI). Answers the session plus `aiSuggestions`. |
| `PUT …/sessions/{id}/records/{studentId}` | `{participantId}` gives the student that Zoom name; `{status: "present" \| "absent"}`; `{reset: true}` hands it back to matching. `remember` (default true) keeps it for next time. |
| `POST …/sessions/{id}/participants/{pid}/ignore` | `{ignored}`: not a student (or is one after all). |
| `POST …/sessions/{id}/finalize` · `/reopen` | A finalized session refuses changes (`409`) until reopened. |
| `GET …/sessions/{id}/export.csv` | One row per student (UTF-8 with BOM; cells that could be formulas are neutralised). |

Every change is written to `admin_audit_log` (`student.*`, `attendance.*`).

## Other coordinators' classes, run from one PC

The admin's PC opens and finishes classes for the coordinators the admin turns on, each under that
coordinator's own Zoom and LMS accounts. Migration `0009_delegated_runs` adds `run_delegations` (who
is turned on, and the two accounts their classes use) and `class_plans` (one row per class of theirs).
Admin only; every write also needs `X-Dashboard-Request: 1`, and answers are never cached.

| Endpoint | What it does |
|---|---|
| `GET /api/v1/admin/delegations` | every coordinator, their groups, their LMS sign-ins (never a password), whether we run theirs, and how many of their classes are planned or still need a link |
| `PUT /api/v1/admin/delegations/{coordinatorId}` | `{enabled, lmsAccountId?, zoomAccountId?}`. Both accounts must be that coordinator's own (404 otherwise); a coordinator whose account is not active is refused (409) |
| `GET /api/v1/admin/users/{userId}/lms-accounts` | that coordinator's sign-ins and which is in use |
| `GET /api/v1/admin/users/{userId}/zoom-accounts` | that coordinator's Zoom accounts: which group each hosts and the link its classes open |
| `POST /api/v1/admin/users/{userId}/lms-accounts/{accountId}/secret` | the email and password, for the running PC to sign in to the LMS as them. Only for a coordinator who is turned on (403 otherwise); written to `admin_audit_log` as `lms_secret.read`; never logged, never cached |
| `GET /api/v1/admin/run-plan?from=&to=&coordinator=&status=` | the classes to run. `coordinator` may be given more than once; `onlyDelegated=false` also shows a coordinator who has been turned off |
| `POST /api/v1/admin/run-plan/import` | `{coordinatorId, classes:[{group, date, startTime?, title?, meetingUrl?, zoomAccount?, preferredEngine?}]}` — what that coordinator's own LMS session list showed, read by the app signed in as them. At most 500 rows |
| `PATCH /api/v1/admin/run-plan/{planId}` | `{meetingUrl?, zoomAccount?, preferredEngine?, status?, note?, applyToGroup?}` |

Neither account is typed in twice. Every copy of the app keeps what its PC has against the signed-in
person's account — `GET`/`PUT /api/v1/me/zoom-accounts` for the Zoom ones (the whole set, so an
account removed there stops being offered; never a Zoom sign-in, only the account, its e-mail, its
group and its link) and `/api/v1/me/lms-accounts` for the LMS one. A class's meeting link therefore
comes from that coordinator's own Zoom account for the group.

The timetable comes from the LMS, so `import` owns when a class is; a person owns how it opens.
Re-importing therefore updates the title and never touches a link, a Zoom account or a decision to
skip. A class the LMS lists inherits its link from the group's last known one, or from that coordinator's
Zoom account for the group; `applyToGroup` writes one across every class of that group that has not
happened yet.

`preferredEngine` is `desktop`, `web` or absent (auto); `auto` in a PATCH clears it. A meeting link
must be `https://`. Full description: [DELEGATED-CLASSES.md](../DELEGATED-CLASSES.md).

## Agent authentication and registration

1. An operator runs `create-enrollment-token`. It is single use and expires; only its hash is stored.
2. The Windows app calls `POST /api/v1/agents/register` with
   `{enrollmentToken, installationId, name, version, capabilities}`. It gets back
   `201 {deviceId, deviceToken, heartbeatIntervalSeconds}`.
   * The device token is `zaad_<deviceId>.<secret>`. The server keeps only its SHA-256.
   * The same `installationId` registering again (with a new enrollment token) keeps its device id,
     and the old token stops working.
3. The agent connects to `wss://<backend>/ws/agent` with `Authorization: Bearer <deviceToken>`. A bad
   token is refused before the WebSocket opens (handshake `403`, close code 4401).

Client API keys, enrollment tokens and device tokens are separate secrets. None of them works where
another is expected, and none is the Windows app's local recording-API key.

## WebSocket protocol (`/ws/agent`)

JSON, one message per text frame, at most 64 KB. Types:

| Agent → backend | | Backend → agent | |
|---|---|---|---|
| `hello` | `{version, capabilities, agentState, activeJobId}`, sent first after connecting | `welcome` | `{deviceId, heartbeatIntervalSeconds}` |
| `heartbeat` | `{deviceId, version, status: idle\|busy, capabilities}`, every 30 s | `heartbeat.ack` | `{serverTime}` |
| `job.accepted` | `{jobId}` | `job.assign` | `{jobId, jobType, payload, attempt}` |
| `job.rejected` | `{jobId, reason}` (`busy`, `unsupportedType`) | `job.start` | `{jobId, jobType, payload, attempt}`, the go-ahead |
| `job.started` | `{jobId}` | `job.revoke` | `{jobId}`: do not run it |
| `job.succeeded` | `{jobId, result}` | `ack` | `{jobId, event}`: stored; drop it from the outbox |
| `job.failed` | `{jobId, error:{code, message, retryable, retryAfterSeconds?}}` | `error` | `{code, message}` |

The job lifecycle:

```
queued ─(online, capable, idle device; least recently assigned)─▶ assigned ──job.assign──▶ agent
agent ──job.accepted──▶ backend checks it is still this device's ──job.start──▶ agent runs it
                                            (or job.revoke if it was taken back)
agent ──job.started──▶ running ──job.succeeded / job.failed──▶ succeeded / failed
retryable failure (busy) and attempts < 3 ──▶ queued again after retryAfterSeconds
assigned, not accepted in 60 s, or its device went silent ──▶ queued again
running on a device silent for 30 min ──▶ failed (agentLost)
```

The agent never runs a job before `job.start`. On reconnect, `hello` makes the backend offer
unaccepted jobs again (`job.assign`) and accepted ones again (`job.start`). The agent recognises
job ids it already has, so nothing runs twice. Results that were not yet acknowledged are resent,
and the backend acknowledges a repeat without applying it twice.

## Security notes

* **Stored secrets:** only hashes of tokens and keys are kept, in the database and in the
  environment. The database holds no password, cookie, browser profile, LMS credential or API key.
  Job payloads (the Drive link) are stored, because the agent needs them.
* **What agents can report:** agent results are reduced to known fields before they are stored
  (`alreadyExists`, `dryRun`, `message`, `group`, `date`, `startTime`, and `code`/`retryable` for
  errors).
* **Logs:** JSON lines. Drive links appear only as `drive.google.com/file/d/1AbCdE...`. Anything
  shaped like a key or token is masked even if passed in by mistake.
* **Plain HTTP:** in `production` mode every request that is not https/wss is refused.
