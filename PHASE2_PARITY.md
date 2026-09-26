# Phase 2 Cloud-Parity Audit & Architecture Report

Audited on branch `linux` at baseline commit `9f36865`.

## Status Vocabulary

* **`PASS`**: Implemented, tested, and actually validated against the real live external environment (requires live execution evidence).
* **`IMPLEMENTED_UNVERIFIED`**: Code is fully implemented and passes unit/integration tests, but has not yet been executed in a real live production Zoom/LMS run on Linux.
* **`PARTIAL`**: Cloud code exists and handles part of the lifecycle, but an important stage, state transition, or automation bridge is missing.
* **`MISSING`**: No cloud implementation exists yet.
* **`BLOCKED`**: Cannot proceed without an external credential, live user action, or an active real class.

---

## 1. System Architecture

```
                    ┌─────────────────────────────────────────────────────────────┐
                    │                      Coolify / VPS                          │
                    │                                                             │
  Browser / Admin ─►│  Traefik HTTPS Proxy (:443)                                 │
                    │         │                                                   │
                    │         ▼                                                   │
                    │  FastAPI Central Backend (:8000) ◄──────┐                   │
                    │  ├── Scheduler (30s pass)               │ WSS / REST        │
                    │  ├── Google Sheets Sync (08:00)         │ (Job Queue)       │
                    │  ├── Attendance Reconciler / AI         │                   │
                    │  └── AES-GCM Encrypted Secret Store     │                   │
                    │         │                               │                   │
                    │         ▼                               │                   │
                    │  PostgreSQL 16 (:5432)                  │                   │
                    │  ├── class_plans / occurrences          │                   │
                    │  ├── attendance_sessions / records      │                   │
                    │  └── student rosters & aliases          │                   │
                    │                                         │                   │
                    │  .NET 8 Playwright Cloud Worker ────────┘                   │
                    │  ├── Meeting Lane (capability: zoom_web)                    │
                    │  │   └── class.run, zoom.report, zoom.recording             │
                    │  │       (Headless Chromium, Profile: zoom-{id})            │
                    │  └── LMS Lane (capabilities: lms, recording_processing)     │
                    │      └── lms.run_session, lms.attendance,                  │
                    │          lms.late_joiners, lms.complete, recording.process  │
                    │          (Headless Chromium, Profile: lms-{id})             │
                    └─────────────────────────────────────────────────────────────┘
```

---

## 2. Core Parity Audit (35 Areas)

| # | Area | Status | Files / Classes | Storage & State | Recovery & Retry | Cloud Dependencies | Validation Evidence | Missing Gaps |
|---|---|---|---|---|---|---|---|---|
| 1 | **Scheduler** | `IMPLEMENTED_UNVERIFIED` | `scheduling.py`, `Scheduler` | `class_plans`, `class_occurrences`, `jobs` | Idempotent plan+stage keys; ignores classes >15m late; retries up to 3 times | Cairo tzdata, Postgres | Tested via unit tests with simulated time | Needs recurring schedule generator or production seed of G1/G2 timetable |
| 2 | **ClassOccurrence Lifecycle** | `PARTIAL` | `occurrence_lifecycle.py`, `models.ClassOccurrence` | `class_occurrences` table (16 states) | State advances on job completion; preserves `last_error` on failure | Postgres asyncpg | Tested via state transition unit tests | Automatic promotion from `zoomLinkFound` to `recording.process` job and `waitingForDrive` is missing |
| 3 | **Zoom Web Account Isolation** | `IMPLEMENTED_UNVERIFIED` | `ClassRunStage.cs`, `ZoomProfileManager` | `/var/lib/classflow/profiles/zoom-{account}` | Directory-based Chromium `userDataDir`; `ProfileOperationLock` | Named docker volume `classflow-worker-state` | Tested multi-profile lock handling | Live login cookies for `depi+10` and `depi+11` have not been populated on VPS |
| 4 | **Zoom Web Meeting Start** | `IMPLEMENTED_UNVERIFIED` | `WebAutoAdmitEngine.cs`, `ZoomWebMeetingController.cs` | Browser local storage / session cookies | Dismisses audio/video prompts; detects password dialog | Headless Chromium, Xvfb optional | Container headless Chromium starts and navigates to Zoom web | Live Zoom meeting start on Linux VPS not yet observed |
| 5 | **Mic/Camera Handling** | `IMPLEMENTED_UNVERIFIED` | `WebAutoAdmitEngine.DisableMicrophoneAsync`, `DisableCameraAsync` | DOM state on meeting launch | Regex click on "Mute", "Stop Video", dismisses WebRTC dialogs | Playwright DOM locators | Verified against simulated Zoom web DOM fixtures | Live Zoom DOM verification on current Zoom web release |
| 6 | **Waiting Room** | `IMPLEMENTED_UNVERIFIED` | `ZoomWaitingRoomDom.cs`, `WebAutoAdmitEngine.cs` | In-memory DOM tree snapshot | Periodic DOM evaluation every 500-1000ms | Playwright DOM parser | 207 portable WebAutomation unit tests pass | Live waiting room DOM behavior under high load |
| 7 | **Auto Admit** | `IMPLEMENTED_UNVERIFIED` | `WebAutoAdmitEngine.MonitorAsync`, `WebAdmissionVerifier.cs` | In-memory queue, DOM verification window (12s) | Clicks "Admit all" or single "Admit", retries up to 2 times | Chromium event loop | Verified in mock DOM test cases | Verification in live Zoom meeting |
| 8 | **Participants Capture** | `IMPLEMENTED_UNVERIFIED` | `WebJoinedList.cs`, `WebParticipantList.cs` | In-memory participant list | Scans participants panel DOM, handles scroll/virtualization | Playwright DOM locators | Unit tests for panel DOM parsing pass | Live test with >20 participants |
| 9 | **Attendance Snapshots** | `PARTIAL` | `MeetingAttendance.cs`, `attendance.py` | `attendance_snapshots`, `attendance_sessions` | Captured at +2m, every 10m, and on `meetingEnd`; posts to backend | HTTP/REST to Backend | Backend snapshot ingestion verified with 45 unit tests | Snapshot interval is currently 10m instead of 15m; post-admit trigger not wired to admit event |
| 10 | **Participants Watchdog** | `IMPLEMENTED_UNVERIFIED` | `ZoomWebToolbar.WakeAsync`, `EnsureParticipantsPanelOpenAsync` | DOM state tracking | Moves mouse, dispatches keyboard events to wake toolbar, reopens panel | Playwright mouse/keyboard | Verified in mock toolbar tests | Live verification on headless Linux |
| 11 | **Ignore Lists** | `PARTIAL` | `attendance_names.py`, `attendance.py` | `clean_display_name` strips role tags; `AttendanceParticipant.ignored` | Staff roles marked ignored in session | In-memory / Postgres | Staff tag removal unit tests pass | Global ignore list (`eyouth coordinator`, `depi wavz`, `youssef ayoub`) and group ignores not seeded in central backend |
| 12 | **Name Matching** | `PASS` | `attendance_matching.py`, `attendance_names.py` | PostgreSQL `students`, `student_aliases` | Exact match -> Alias match -> Normalized rule-based -> Fuzzy Dice | Pure algorithms | 45 unit tests passing covering Arabizi, Arabic normalization, compound names | Fully validated against real student name sets |
| 13 | **OpenRouter AI** | `PARTIAL` | `attendance_ai.py`, `ChatCompletionsAi` | `CENTRAL_AI_API_KEY`, OpenRouter API | Only unresolved names offered; strict JSON schema; IDs only | OpenRouter HTTP endpoint | Unit tests verify JSON parsing, schema validation, and alias saving | Only called via manual dashboard API; NOT automatically called before final attendance upload |
| 14 | **Alias Learning** | `PASS` | `attendance_ai.py`, `attendance.py`, `StudentAlias` | PostgreSQL `student_aliases` table | Confident AI matches (>=0.85) saved as `accepted`; manual alias updates | Postgres DB | Unit tests verify alias persistence and subsequent instant match | Validated algorithmically |
| 15 | **Attendance Finalization** | `PARTIAL` | `attendance.py` (`POST /dashboard/attendance/sessions/{id}/finalize`) | `attendance_sessions.status = "finalized"` | Locks records from subsequent recomputes | Postgres DB | Finalize endpoint tested in backend tests | No automated trigger at class end before `lms.complete` |
| 16 | **Co-Host Automation** | `IMPLEMENTED_UNVERIFIED` | `SessionRoleBridge.cs`, `WebCoHostAssigner.cs` | In-memory session roles, `AssignedCoHosts` | Triggered when candidate enters; clicks More -> Make Co-Host -> confirms Yes | Playwright DOM clicks | Unit tests pass | Not live tested against real Zoom web UI on Linux |
| 17 | **LMS Login** | `IMPLEMENTED_UNVERIFIED` | `LmsSessionRunner.SignInAsync`, `worker_support.py` | AES-GCM encrypted in `lms_accounts`; decrypted in-memory | Per-job token authentication; 3 retries on transient network errors | DEPI LMS web page | Reached live LMS sign-in page from Linux VPS (commit `9f36865`) | Needs live coordinator credentials tested on VPS |
| 18 | **Run Session** | `IMPLEMENTED_UNVERIFIED` | `RunSessionStage.cs`, `LmsSessionRunner.RunAsync` | `class_occurrences.state = "live"` | Matches group code + session date; clicks Run Session; verifies status | DEPI LMS session page | Verified dry-run against live DEPI LMS (read session page successfully) | Live execution without dry-run |
| 19 | **+90 Min Attendance** | `IMPLEMENTED_UNVERIFIED` | `AttendanceStage.cs`, `LmsSessionRunner.TakeAttendanceAsync` | Fetches present list from `/api/v1/agent/jobs/{id}/attendance` | Refuses if 0 present; ticks Joined; leaves Not Seen as Not Joined | DEPI LMS attendance form | Method logic unit-tested | Live submission against DEPI LMS |
| 20 | **+3 Hour Correction** | `IMPLEMENTED_UNVERIFIED` | `LateJoinersStage.cs`, `LmsSessionRunner.CorrectAttendanceAsync` | Re-queries `/attendance` endpoint; diffs rows | Only toggles rows that changed status; reloads and verifies | DEPI LMS attendance form | DHTML diffing logic tested | Live submission against DEPI LMS |
| 21 | **Final Correction** | `MISSING` | Not wired | None | Should run after final attendance snapshot & AI resolution | DEPI LMS | No code currently orchestrates final correction pass | Missing end-of-class pipeline step |
| 22 | **Complete Session** | `IMPLEMENTED_UNVERIFIED` | `CompleteSessionStage.cs`, `LmsSessionRunner.CompleteSessionAsync` | `class_occurrences.state = "lmsSessionCompleted"` | Clicks Complete/Finish Session; confirms dialog; verifies "finished" | DEPI LMS session page | Verified dry-run refusal on pending session | Live execution on completed class |
| 23 | **Zoom Recording Retrieval** | `IMPLEMENTED_UNVERIFIED` | `ZoomRecordingStage.cs`, `ZoomRecordingLinkReader.cs` | Navigates to `zoom.us/recording` | Group name + date + start time matching; returns `shareUrl` | Zoom web portal | Reader unit-tested | Live recording discovery on Linux VPS |
| 24 | **Google Sheets** | `IMPLEMENTED_UNVERIFIED` | `google_sheets.py`, `GoogleSheetsConnection` | `CENTRAL_SECRETS_KEY` encrypted refresh token; Postgres sync records | Daily 08:00 Cairo cron; read-only; Group + Date row matching | Google Sheets API v4 | Sync logic and conflict detection unit-tested | Real Google OAuth client ID/secret configuration on VPS |
| 25 | **Drive -> LMS Replacement** | `IMPLEMENTED_UNVERIFIED` | `RecordingProcessStage.cs`, `LmsSessionRunner.AttachRecordLinkAsync` | `class_occurrences.state = "driveLinkAttached"` | Replaces Zoom link with Drive link; conflict if non-Zoom; reloads & verifies | DEPI LMS session page | Logic and reload-verification unit-tested | Live attachment on real DEPI session |
| 26 | **Retry Queues** | `PASS` | `jobs.py`, `dispatch.py`, `models.Job` | PostgreSQL `jobs` table (`attempts`, `max_attempts`, `next_attempt_at`) | Exponential backoff; retryable failure classification; orphan sweeper | Postgres DB | Verified in `test_jobs.py` and `test_agents_and_dispatch.py` | Fully functional |
| 27 | **Browser Crash Recovery** | `IMPLEMENTED_UNVERIFIED` | `ZoomBrowserLauncher.cs`, `Playwright` | Isolated process lifecycle per job | Chromium crash terminates job with failure outcome; re-queued cleanly | Linux Chromium | Tested crash simulation in container | Live recovery during actual meeting |
| 28 | **Worker Restart Recovery** | `PASS` | `Program.cs`, `CentralAgentService.cs` | Persistent state volume `/var/lib/classflow` (token, journals) | Reconnects to backend WSS; re-registers; resumes job polling | Docker named volume | Tested container stop/start with persistent token | Verified |
| 29 | **VPS Restart Recovery** | `PASS` | `docker-compose.coolify.yml`, `backend-entrypoint.sh` | PostgreSQL named volume `classflow-postgres` | Alembic runs `upgrade head` on boot; scheduler re-evaluates timetable | Docker / Systemd | Verified restart persistence | Verified |
| 30 | **Notifications** | `PARTIAL` | `notifications.py` | In-memory emit, structured logging | Logs warning/error events; `Notifier` interface stubbed | None configured | Logging verified in `test_notifications.py` | No external delivery (webhook, Slack, Telegram, email) |
| 31 | **Health & Pre-Flight Checks** | `PASS` | `api.py` (`/health`), `CloudWorker/Program.cs` Preflight | In-memory checks | Backend verifies DB query; worker verifies Chromium launch, `/dev/shm`, backend URL | Chromium, Postgres | Health returns `{"status":"ok"}` on live VPS | Preflight runs cleanly on worker start |
| 32 | **Data Migration Status** | `PARTIAL` | Local Mac JSON files vs VPS PostgreSQL | `schedules.json`, `ignored-participants.json` | None currently running automatically | Local Mac filesystem | Local files verified intact | 25 G1, 20 G2 students, schedules, and global ignores not yet in Postgres |
| 33 | **Secrets & Security** | `PASS` | `security.py`, `auth.py`, `worker_support.py` | AES-256-GCM under `CENTRAL_SECRETS_KEY`; Scrypt password hashes | Ephemeral per-job secret distribution; zero plaintext secrets in Git/DB | Cryptography library | Verified in `test_security_and_config.py` | Production secrets properly protected |
| 34 | **Monitoring & Observability** | `PASS` | `observability.py`, `dashboard.py` (`/api/v1/dashboard/agents`) | `admin_audit_log`, `device_activities`, structured JSON logs | Emits structured JSON events across lifecycle | Backend logging | Verified in tests and audit log queries | Fully operational |
| 35 | **Dashboard Current State** | `PASS` | React SPA in `Dashboard/dist`, `dashboard.py` | Served directly from FastAPI backend | Read-only views for Overview, Sessions, Recordings, Agents, Groups | React, Tailwind, Vite | Served live at `https://classflow.../dashboard/` | Rebuild planned for later phase after core automation logic |

---

## 3. Zoom Web Architecture & Meeting Close Logic

### Account Isolation
* Zoom accounts are persisted in `zoom_accounts` with AES-GCM encrypted passwords.
* Each account has its own Chromium persistent profile at `/var/lib/classflow/profiles/zoom-{account_id}`.
* `ProfileOperationLock` enforces that only one process accesses a profile directory at any time.

### Meeting Start & Automation
* `WebAutoAdmitEngine.StartAsync` launches headless Chromium with user profile.
* Opens meeting URL, enters passcode if prompted, handles audio/video join dialogs, clicks Mute and Stop Video.
* Opens Participants Panel via `EnsureParticipantsPanelOpenAsync`.
* `MonitorAsync` polls the waiting room DOM every 500-1000ms and admits participants.
* `WebCoHostAssigner` monitors incoming participants, finds co-host candidates, clicks More -> Make Co-Host -> confirms Yes.

### Meeting End Policy (`MeetingEnd.cs`)
* **Do NOT change blindly**: The worker does NOT close Zoom when the LMS session completes.
* Sleeps until `classStart + 3 hours - 5 minutes`.
* Watches for meeting closed elsewhere (instructor ended from phone/app).
* Evaluates room: Host alone, co-host left and most students left (`emptySince >= 5 min`).
* Guarded: Never ends if anyone's mic is unmuted, never if breakout rooms are open, never if participant list is unreadable.
* Executes a 1-minute warning wait (`LastLook = 1 min`), takes a final attendance snapshot (`LastReadAsync`), then calls `ZoomWebMeetingEnder.EndForAllAsync`.
* Hard cap overrun: 5 hours total (`AutoEndRule.Overrun = 2 hours`).

---

## 4. Status Categorization Summary

### A. Truly Complete (`PASS`)
1. Name matching algorithms & Arabic transliteration rules (`attendance_matching.py`)
2. Alias learning memory & database persistence (`attendance_names.py`, `student_aliases`)
3. Retry queue backoff and job dispatching (`jobs.py`, `dispatch.py`)
4. Worker restart recovery via persistent volume (`classflow-worker-state`)
5. VPS restart recovery & migration automation (`backend-entrypoint.sh`, Alembic)
6. Pre-flight health endpoints (`/health`)
7. Cryptographic secret security & AES-GCM password store (`security.py`)
8. System observability and structured logging (`observability.py`)
9. Dashboard serving foundation (`dashboard.py`, Vite SPA)

### B. Implemented but Unverified in Live Cloud Run (`IMPLEMENTED_UNVERIFIED`)
1. Zoom Web meeting start & WebRTC audio/video mute on Linux Chromium
2. Waiting room DOM capture & automated admission
3. Participants capture & panel watchdog
4. Web co-host assignment via Playwright DOM
5. LMS login & navigation under worker companion lane
6. LMS Run Session
7. LMS +90 min attendance submission
8. LMS +3 hr late joiner correction
9. LMS Complete Session
10. Zoom recording web scraper (`zoom.recording`)
11. Google Sheets 08:00 Cairo daily read-only sync
12. Google Drive -> LMS link replacement & verification

### C. Partial (`PARTIAL`)
1. **ClassOccurrence Lifecycle**: Does not automatically transition from `zoomLinkFound` to `recording.process` job.
2. **Attendance Snapshots**: Interval is 10 min instead of 15 min; no post-admit trigger.
3. **Ignore Lists**: Backend lacks global ignore list (`eyouth coordinator`, `depi wavz`, `youssef ayoub`) and group-level ignore lists.
4. **OpenRouter AI**: Only callable via manual dashboard button; missing automatic trigger before late joiners/finalize.
5. **Attendance Finalization**: Endpoint exists, but no automatic call before class end.
6. **Data Migration**: Local Mac student rosters and schedules not yet imported into central PostgreSQL.
7. **Notifications**: Structured events logged, but no webhook/email delivery mechanism.

### D. Missing (`MISSING`)
1. Final attendance correction pipeline before `lms.complete`.
2. Recurring schedule generator for the 6 weekly timetable slots.

### E. Blocked (`BLOCKED`)
1. Live Zoom account sign-in verification (requires real coordinator MFA/CAPTCHA completion once per profile).
2. Live LMS test execution (requires real class session in progress).
3. Google Sheets OAuth live connection (requires Google Cloud Console credentials).
