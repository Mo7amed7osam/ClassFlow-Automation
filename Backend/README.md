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

Requires Python 3.11+ (tested with 3.13) and PostgreSQL 14+ (tested with 18).

```powershell
cd Backend
py -3.13 -m venv .venv
.venv\Scripts\python -m pip install -r requirements-dev.txt
```

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
