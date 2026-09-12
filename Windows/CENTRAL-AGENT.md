# Central agent (Phase 1)

> **Phase 1 foundation — not production-ready.** Nothing starts automatically yet, and the WPF app
> does not show the agent.

The agent keeps this PC connected to the [central backend](../Backend/README.md) and runs the jobs
it is given. It connects **out** (`wss://<backend>/ws/agent`), so the PC needs no public IP, no open
port and no tunnel. The local recording API on `127.0.0.1:47821` is unchanged and still works on its
own.

```
backend ══ outbound WebSocket (device token) ══▶ CentralAgentService
                                                     │  recording.process
                                                     ▼
                              RecordingProcessJobHandler
                                  → RecordingApiRequestParser   (the local API's own rules)
                                  → RecordingLinkProcessor.AttachProvidedLinkAsync
                                  → lms-dashboard profile lock → LmsSessionRunner → DEPI LMS
```

**Reuse, not duplication.** The agent runs the exact workflow `serve-api` runs:
* It is built by `RecordingWorkflow.CreateForApi()`, so it has no Zoom source.
* It uses the same `ProfileOperationLock`, so a central job never drives the dashboard at the same
  time as the app, a scheduled meeting or the local API. That case is answered as `busy` and retried.
* It calls the local workflow in process, not over HTTP, so it needs no local API key.

## Commands

The executable is `ZoomAutoAdmit.Inspector.exe`, in the WPF app's Release folder.

| Command | What it does |
|---|---|
| `agent-register --backend https://central.example.com [--name PC-01]` | Registers this PC once. The enrollment token (from the backend operator, `zaae_…`) is read from `ZOOM_AUTO_ADMIT_ENROLLMENT_TOKEN`, or asked for with the typing hidden. It is never taken from the command line, where other programs can read it. |
| `agent-run [--background]` | Stays connected and runs jobs until Ctrl+C. Exit codes: `0` stopped, `1` not registered, `3` the backend refused the device token (it stops instead of retrying). |
| `agent-status` | Shows the installation id, device, backend, whether a token is stored, and how many job messages are waiting to be sent. |

`--backend` must be `https://`. Plain `http://` is accepted only for a backend on this PC (for
development).

## What is stored on the PC

| Where | What |
|---|---|
| `%LOCALAPPDATA%\ZoomAutoAdmit\Central\device.json` | Installation id (made once, never regenerated), device id, backend URL, name. Nothing secret. |
| Windows Credential Manager `ZoomAutoAdmit/Central/DeviceToken` | The device token, for this Windows account only, like the LMS sign-in. |
| `%LOCALAPPDATA%\ZoomAutoAdmit\Central\jobs.json` | Job journal: ids and states of the last 500 jobs, the final result of each, and the job messages the backend has not yet acknowledged. Never a payload, link or token. |
| `%LOCALAPPDATA%\ZoomAutoAdmit\Logs\central-agent.log` | The log (rolls over at 5 MB). |

To register again (for example after the device was revoked), get a new enrollment token and run
`agent-register` again. The installation id, and so the device, stays the same.

## Behaviour

* **Heartbeat:** every 30 s, `{deviceId, version, status: idle|busy, capabilities}`. The backend
  counts a device as offline only after 90 s of silence.
* **Reconnect:**
  * After a drop, it waits 1, 2, 4, 8, 16, 30, 30… s, each ±20 % at random, so PCs do not all
    reconnect together. The delays start over after a connection that lasted.
  * If the backend is silent for 75 s (it answers every heartbeat), the connection counts as dead
    and is replaced. This covers sleep/wake, network changes and a backend restart.
* **One job at a time:**
  * A job is **accepted** when assigned, and only **run** after the backend confirms with `job.start`.
  * A second job while busy is turned down (`busy`), and the backend gives it to another device or
    retries.
* **No double execution:**
  * The journal records a job as running *before* it runs.
  * A repeated `job.assign` or `job.start` for a known job id is answered from the journal (the
    stored result is sent again), never run again.
  * A job interrupted by a restart is reported as `agentRestarted` instead of being re-run.
  * `replaceExisting:false` in the LMS step remains the last line of defence.
* **No lost results:** a job keeps running if the connection drops. Its result stays in the journal's
  outbox until the backend acknowledges it, and is sent on the next connection.
* **Result mapping:**

  | Outcome | Reported as |
  |---|---|
  | Attached | `job.succeeded {alreadyExists:false}` |
  | Link already there | `job.succeeded {alreadyExists:true}` |
  | Dry run | `job.succeeded {dryRun:true}` |
  | Busy | `job.failed {code:"busy", retryable:true, retryAfterSeconds:60}` |
  | LMS failure | `job.failed {code:<reason>}` |

* **Logs:** lines like `[AGENT] event=job_started jobId=… jobType=recording.process`. The events are
  `connecting`, `connected`, `welcome`, `heartbeat`, `job_received`, `job_started`, `job_succeeded`,
  `job_failed`, `duplicate_job`, `disconnected`, `reconnect_scheduled` and `unauthorized`. The
  recording step adds its usual `[RECORDINGS]`/`[LMS]` lines. There is never a token, key, cookie,
  password, profile path or full Drive link.

## Try it locally

1. Start the backend as in [Backend/README.md](../Backend/README.md), in development mode, on
   `http://127.0.0.1:8080`.
2. Create an enrollment token there:
   `python -m central_backend.cli create-enrollment-token --label PC-01`.
3. On the PC:
   ```powershell
   $env:ZOOM_AUTO_ADMIT_ENROLLMENT_TOKEN = '<zaae_… token>'
   .\ZoomAutoAdmit.Inspector.exe agent-register --backend http://127.0.0.1:8080 --name PC-01
   Remove-Item Env:\ZOOM_AUTO_ADMIT_ENROLLMENT_TOKEN
   .\ZoomAutoAdmit.Inspector.exe agent-run
   ```
4. Submit a job with the n8n key (`POST /api/v1/jobs`), then read it (`GET /api/v1/jobs/{jobId}`).
   Use `"dryRun": true` first.

To undo a local trial:
* `cmdkey /delete:ZoomAutoAdmit/Central/DeviceToken`
* delete `%LOCALAPPDATA%\ZoomAutoAdmit\Central`.
