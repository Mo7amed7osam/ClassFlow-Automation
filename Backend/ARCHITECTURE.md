# Central Backend — architecture (Phase 1)

> Phase 1 foundation. **Not production-ready.** See "Phase 2" at the end.

## What was found in the repository (Phase 0)

| Area | Finding | Consequence |
|---|---|---|
| Solution | `Windows/ZoomAutoAdmit.Windows.sln`, .NET 8 (`net8.0-windows10.0.19041.0`): Core, WebAutomation (Playwright: Zoom web + DEPI LMS), WindowsRuntime, UIAutomation, WPF `WindowsUI`, console `Inspector`, xUnit test projects. The root also holds an older macOS Swift tool and `main.py`. | No backend and no shared server code exist, so the backend is a new, separate project: `Backend/`. |
| Recording workflow | `RecordingLinkProcessor.AttachProvidedLinkAsync(ProvidedRecordLinkRequest)` is the only path from "group + Drive link + date" to the LMS. `RecordingWorkflow.CreateForApi()` builds it without Zoom. `RecordingApiRequestParser` validates the JSON body. `ProfileOperationLock` is a cross-process file lock on the `lms-dashboard` profile. | The agent reuses all of these in-process. It does not call `127.0.0.1:47821` over HTTP and needs no local API key. It cannot run two dashboard operations at once with the local API or the app, because both take the same lock. |
| Local API | `RecordingApiServer` (http.sys `HttpListener`, loopback only, `X-API-Key`). | Untouched. It still runs alongside the agent. |
| DI / config | No DI container: objects are composed by hand in `Inspector/Runtime` and `WindowsUI`. Configuration comes from environment variables and JSON files under `%LOCALAPPDATA%\ZoomAutoAdmit`. Secrets live in Windows Credential Manager (`LmsCredentialStore`, `AiCredentialStore`, P/Invoke). | The agent follows the same patterns: constructor-injected services, a JSON identity file, and the device token in Credential Manager. |
| Logging | `ConsoleLogger` (with the `EntryWritten` event mirrored to files by commands), plus `Action<string>` log delegates. `[AREA] message` lines. | The agent logs `[AGENT] event=… key=value` lines through the same delegate. |
| Runtime | Only `Microsoft.NETCore.App` and `WindowsDesktop.App` are installed; there is no ASP.NET Core. | Agent uses `System.Net.WebSockets.ClientWebSocket` (in-box). Tests use an `HttpListener` WebSocket server. |
| Tests / build | xUnit 2.6 + Moq; `dotnet build`/`dotnet test` over the solution. | New `ZoomAutoAdmit.CentralAgent` + `.Tests` projects in the same solution. |

## Proposal

```
 n8n ──HTTPS (X-API-Key)──▶  Central Backend (FastAPI, one process)  ◀──WSS (device token)── Windows Agent ×N
                              │ REST: /api/v1/jobs, /api/v1/devices          outbound only; no open ports
                              │ REST: /api/v1/agents/register (enrollment)    │
                              │ WS:   /ws/agent                                ▼
                              │ dispatcher + sweeper (asyncio tasks)     RecordingLinkProcessor (existing)
                              ▼                                                 │ lms-dashboard profile lock
                          PostgreSQL: devices, enrollment_tokens, jobs, job_events     ▼
                                                                                DEPI LMS
```

* **Backend** = Python 3.11+ FastAPI + SQLAlchemy 2 (async, asyncpg) + Alembic + PostgreSQL. No Redis.
  It runs as **one instance** in V1, because the WebSocket connection registry is in memory.
  It never automates a browser.
* **Two authentication domains, never mixed:**
  * API clients (n8n) send an `X-API-Key`. The server holds only SHA-256 hashes of the keys, in an env var.
  * Agents authenticate with a device token issued at registration. The server stores only its
    SHA-256. Each device has its own token.
  * Registration requires a single-use, expiring enrollment token created by an operator (CLI).
* **Job lifecycle** (one job type in V1: `recording.process`):
  `queued → assigned → (accepted) → running → succeeded | failed`, and `queued → cancelled`.
  * The agent executes a job only after the backend confirms its acceptance (`job.start`). A job
    that was re-queued meanwhile is revoked instead.
  * Every agent event is persisted in `job_events` and acknowledged (`ack`), so the agent can drop it
    from its outbox.
* **Delivery guarantees:**
  * The backend never loses a queued job.
  * The agent keeps a journal of job ids and results, and an outbox of unacknowledged events on
    disk, so a reconnect neither re-executes a job nor loses a result.
  * `replaceExisting=false` in the LMS step remains the last line of defence against a double write.
* **Liveness:** the agent sends a heartbeat every 30 s. A device is offline after 90 s without a
  heartbeat, not after one miss.
  * An assigned but unaccepted job is re-queued after 60 s, or once its device goes offline.
  * A running job whose device stays offline for 30 min fails with `agentLost`.
* **Agent reconnect:** exponential backoff 1, 2, 4, 8, 16, 30, 30… s with ±20 % jitter, reset after a
  good session. If the server stays silent for 75 s (heartbeat acks stop), the connection counts as
  dead, which covers sleep/wake and network changes.

## Database (migration `0001_initial`, additive only; its downgrade refuses to drop data)

| Table | Columns |
|---|---|
| `devices` | `id` uuid PK, `installation_id` uuid UNIQUE, `name`, `version`, `status` (online/offline), `agent_state` (idle/busy), `capabilities` jsonb, `token_hash` (SHA-256), `last_heartbeat`, `last_assigned_at`, `connected_at`, `revoked_at`, `created_at`, `updated_at` |
| `enrollment_tokens` | `id`, `token_hash` UNIQUE, `label`, `expires_at`, `used_at`, `used_by_device_id` → devices |
| `jobs` | `id` uuid PK, `type`, `device_id` → devices, `payload` jsonb, `status` (queued/assigned/running/succeeded/failed/cancelled), `result` jsonb, `error` jsonb, `idempotency_key` UNIQUE, `attempts`, `max_attempts`, `available_at`, `created_at`, `assigned_at`, `accepted_at`, `started_at`, `finished_at`, `updated_at`; indexes (status, available_at, created_at) and (device_id, status) |
| `job_events` | `id` bigserial, `job_id` → jobs, `device_id`, `event_type` (created, assigned, accepted, started, succeeded, failed, retry_scheduled, rejected, assignment_expired, assignment_undelivered, agent_lost, cancelled), `payload` jsonb, `created_at` |
| `zoom_accounts` (migration `0010_zoom_accounts`) | `id` uuid PK, `user_id` → users, `account_id`, `label`, `zoom_email`, `group_name`, `default_meeting_url`, `preferred_engine`, `active`, `created_at`, `updated_at`; unique (user_id, lower(account_id)). No Zoom sign-in is stored |
| `run_delegations` (migration `0009_delegated_runs`, `zoom_account_id` added by `0010`) | `coordinator_id` uuid PK → users, `enabled`, `lms_account_id` → lms_accounts, `zoom_account_id` → zoom_accounts, `zoom_account`, `created_by`, `created_at`, `updated_at` |
| `class_plans` (migration `0009_delegated_runs`) | `id` uuid PK, `coordinator_id` → users, `group_name`, `session_date`, `start_time`, `title`, `meeting_url`, `zoom_account`, `preferred_engine` (desktop/web), `source` (lms/manual), `status` (planned/skipped/opened/done/failed), `note`, `imported_at`, `created_at`, `updated_at`; unique (coordinator_id, group_name, session_date, coalesce(start_time, '')) |
| `recordings` (migration `0002_recordings`) | `id` uuid PK, `group_name` (indexed), `session_date` date (indexed), `start_time`, `file_name`, `record_type`, `drive_link` text, `zoom_link` text, `source` default 'zoom', `lms_status` default 'pending', `lms_updated_at`, `created_at`, `updated_at`; unique index (group_name, session_date, coalesce(start_time, '')) |

## Phase 2 (not built)

* TLS termination and deployment: a reverse proxy with HTTPS/WSS, hosting, backups.
* More than one backend instance, which needs Postgres `LISTEN/NOTIFY` or Redis for dispatch.
* An admin UI, a device revocation endpoint, rate limiting, key rotation, metrics.
* Starting the agent automatically with the app; showing agent status in the WPF app.
* Retention and redaction of old job payloads; per-group device routing; more job types.
