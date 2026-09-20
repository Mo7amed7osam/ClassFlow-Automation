# Feature parity: Windows application → Linux / Coolify

What the system does today, where each part actually runs, what has to change to run it on a Linux
VPS with no Windows machine, and how each one is proved.

Every row was checked against the code, not against the older documents. Where a document and the
code disagreed, the code won and the difference is noted.

**Status vocabulary** — used here and in the test report:

| | |
|---|---|
| `PASSED` | Ran on Linux against the real dependency and gave the right answer |
| `IMPLEMENTED_NOT_LIVE_VERIFIED` | Code written and unit-tested; never run against real Zoom or the real LMS |
| `NOT_STARTED` | No cloud implementation exists yet |
| `BLOCKED` | Cannot proceed without a decision or an access grant named in the row |

---

## 1. What runs where today

The system is two halves that already speak over HTTPS/WSS.

**The server** (`Backend/`, Python 3.11 + FastAPI + SQLAlchemy async + PostgreSQL) keeps people,
groups, accounts, delegations, class plans, recordings and jobs. It never drives a browser. It runs
as **one process**, because the agent WebSocket registry is in memory — documented in
`Backend/ARCHITECTURE.md` and still true in `Backend/central_backend/main.py`.

**The Windows PC** (`Windows/`, C# .NET 8) does all the actual work: opens Zoom, admits the waiting
room, assigns co-hosts, collects attendance, drives the DEPI LMS with Playwright, reads Zoom's
reports and recordings, and writes back to the server.

The migration's whole job is to move that second half onto Linux.

### The existing agent is not the cloud worker

`ZoomAutoAdmit.CentralAgent` already connects to the backend over WSS with a device token, keeps an
outbox and a journal, and survives reconnects. But `JobHandlers.cs` declares exactly one job type:

```
public string JobType => "recording.process";
```

So the existing agent attaches a Drive link to an LMS session and nothing else. Everything in the
screenshot — opening the meeting, admitting, attendance, roles, completion — has no job type and no
server-side path. **A recording-only agent is not a cloud worker**, and this is the single largest
piece of work in the migration.

---

## 2. Portability: measured, not assumed

Fourteen C# projects. I changed `TargetFramework` to `TargetFrameworks` on two of them and built
for `net8.0` to find out what is genuinely portable.

| Project | Lines | Builds for `net8.0` | Real verdict |
|---|---|---|---|
| `Core` | 5,146 | **Yes, unmodified** | Portable. Uses `Environment.SpecialFolder.LocalApplicationData`, which resolves to `~/.local/share` on Linux; every such class already takes an optional path, so the location is injectable |
| `AttendanceMatching` | 1,160 | Yes (already `net8.0`) | Portable |
| `Roster` | 607 | Yes (already `net8.0`) | Portable |
| `WebAutomation` | 7,365 | **Compiles — but does not run** | See below. Only package is `Microsoft.Playwright`, which is cross-platform |
| `CentralAgent` | 1,635 | Not yet tried | Only references `WebAutomation`; expected to follow it |
| `Attendance` | 1,307 | No | References `UIAutomation`; the web half must be split out |
| `SessionRoles` | 1,377 | No | References `UIAutomation` |
| `UIAutomation` | 5,773 | No | FlaUI, 35 P/Invokes in `Interop/NativeMethods.cs`, screen capture, WinRT OCR. **Stays on Windows** |
| `WindowsRuntime` | 3,521 | No | Task Scheduler, keyboard input, desktop launch. **Stays on Windows** |
| `WindowsUI` | 13,044 | No | WPF. **Stays on Windows** |
| `Inspector`, `WaitingRoom*`, `Setup` | 9,407 | No | Desktop diagnostics and installer. **Stay on Windows** |

### The trap in that table — now closed

`WebAutomation` **built for `net8.0` with zero errors and zero warnings**, and that proved nothing.
`Lms/LmsCredentialStore.cs` and `ZoomWebSignIn.cs` hold six `[DllImport("advapi32.dll")]`
declarations for `CredRead`/`CredWrite`. A P/Invoke declaration compiles on every platform and
throws `DllNotFoundException` only when it is called. The platform-compatibility analyzer stayed
silent because the project still carries a Windows target.

This is exactly why "change `TargetFramework`" is not a migration. The real work is replacing the
credential backend.

**This is fixed.** The fix was not to thread a dependency through a dozen call sites, but to move
the choice of store down one level: `ILmsCredentialBackend` (`Read`/`Save`/`Delete` by target) is
selected once by `LmsCredentialBackend.Current`. A Windows process gets Credential Manager without
arranging anything, so every existing PC is unchanged; a process anywhere else sets it at startup,
and one that forgets gets a sentence saying so instead of a `DllNotFoundException` from inside a
P/Invoke. `LmsCredentialStore` keeps its public surface and now only says *which* account is meant.

`ZoomWebSignIn` had the same problem in a different shape: it read `wincred:<target>` references
directly. It now offers any reference it cannot read itself to a `Resolver` the host sets — including
a `wincred:` one reaching a server, which is a coordinator's PC reference travelling with their
account rather than an error.

On Linux the passwords come from the server: `lms_accounts` already stores them AES-GCM encrypted
under `CENTRAL_SECRETS_KEY`, and `POST /api/v1/admin/users/{id}/lms-accounts/{aid}/secret` already
hands one to an authorised caller and audits the read. No new secret store was needed — the cloud
worker becomes another authorised reader of the one that exists, and holds what it is given in
memory only (`InMemoryLmsCredentialBackend`), so a restart asks again rather than keeping anything.

**Measured, and then measured again properly.** The `net8.0` build of
`ZoomAutoAdmit.WebAutomation.Tests` first ran 322 tests, all passing — **on Windows**. Running the
same build inside a Linux container is what it actually takes, and 60 of those 322 failed there, all
with one cause: `RecordingApiServer` is the Windows app's own loopback API, built on `HttpListener`,
and the managed `HttpListener` on Linux refuses its prefix outright.

Compiling for `net8.0` on Windows had said nothing about Linux, exactly as compiling with a Windows
target had said nothing about the P/Invokes. The loopback server is now out of the `net8.0` build —
nothing off Windows wants it, since the worker reaches the backend over its own socket and never
opens a port — while `RecordingApiRequestParser`, which the agent genuinely uses and which is plain
parsing, stays in both.

**On Linux, in a container: 207 WebAutomation tests and 19 CloudWorker tests, all passing.** On
Windows the same assembly still runs all 322. That is the evidence.

---

## 3. Page-by-page parity

The screenshot is the Windows app (`WindowsUI`, 22 XAML views). The web dashboard has 12 pages.
The gap is most of the product.

| Screenshot page | Windows implementation | Web dashboard today | Windows dependency | Cloud replacement | Status |
|---|---|---|---|---|---|
| Dashboard | `DashboardWebView` + `DashboardViewModel` | `OverviewPage` (partial) | None | Extend existing page with worker-sourced stats | `NOT_STARTED` |
| Overview | `OverviewView` | `OverviewPage` | None | Extend | `NOT_STARTED` |
| Sessions | `SessionsWebView` — the screenshot's main page: next class, 5 stat cards, group/day filters, 11-stage timeline | none | LMS read via Playwright | New page + worker job types per stage | `NOT_STARTED` |
| Meetings | `MeetingsView` | none | Desktop Zoom + web | Web engine only | `NOT_STARTED` |
| AI Engine | `AiEngineView` | none | OpenRouter HTTP | Server-side; `CENTRAL_AI_*` already exists | `NOT_STARTED` |
| Schedules | `SchedulesView` | none | **Windows Task Scheduler** | Server-side scheduler in the worker | `NOT_STARTED` |
| Waiting Room | `WaitingRoomView`, `AutoAdmitPanel` | none | **UIA + OCR**, or web DOM | Web DOM only (`ZoomWaitingRoomDom`) | `NOT_STARTED` |
| AI Matching | `MatchingView` | none | `AttendanceMatching` (portable) | Server-side | `NOT_STARTED` |
| Session Roles | `SessionRolesView` | none | `SessionRoles` → UIA | Web co-host path needs verification | `BLOCKED` — see §5 |
| Groups & Students | `RosterWebView` | `GroupsPage`, `StudentsPage` | LMS read | Extend existing | `NOT_STARTED` |
| Attendance | `ExtensionAttendanceView` | `AttendancePage`, `AttendanceSessionPage` | UIA panel **or** web | Web source + Zoom report | `NOT_STARTED` |
| Recordings | `RecordingsDashboardView`, `CentralRecordingsView` | `RecordingsPage` | Already a job type | Reuse `recording.process` | Partly exists |
| Coordinators & groups | `AccountsView` | `UsersPage`, `AccountPage` | None | Extend; delegation API exists | Partly exists |
| Server | `LogsView` | `AgentsPage` | None | Extend with worker health | Partly exists |
| `This PC` / Idle | Local process state | none | **Is the Windows PC** | Replace with Cloud Worker heartbeat | `NOT_STARTED` |
| Start Automation | `MainViewModel` | none | Local | Persisted desired-state + worker | `NOT_STARTED` |
| Admitting / Auto co-host toggles | `AutoAdmitPanel` | none | UIA or web | Web policy | `NOT_STARTED` |

### The Sessions timeline

The eleven stages on the class card — `Zoom / Run / Attendance / Late joiners / Complete / Ended /
Zoom report / Zoom recording / Drive / Material / Assignment` — are not a straight line. Read from
the code and from the commit history:

- `Zoom` opens at `Opens 18:45`, fifteen minutes before the class.
- `Ended` is when the meeting closed; `Zoom report` **can only exist after** `Ended`, which is why
  commit `fd4f3f8` moved it there.
- `Late joiners` corrects attendance twice, from snapshots and again from Zoom's report
  (commit `d900a93`), so it is not finished when `Attendance` first fills.
- `Material` and `Assignment` have their own due dates (`Due 27 Sep 19:…` in the screenshot) and are
  independent of the meeting.

The web page must model these as dependencies with their own clocks, not as a sequence.

---

## 4. Background workflows to port

| Workflow | Today | Cloud replacement | Status |
|---|---|---|---|
| Timetable discovery | `LmsSessionRunner` reads each coordinator's LMS list, every 6 h, 3 profiles in parallel | Worker job; `POST /admin/run-plan/import` already exists | `NOT_STARTED` |
| Delegation refresh | Windows PC polls `GET /admin/delegations` every 5 min | Worker poll, unchanged contract | `NOT_STARTED` |
| Meeting startup | `ScheduledClassStarter` + **Windows Task Scheduler**, one task per class | Server-side scheduler with leases | `NOT_STARTED` |
| Waiting-room admission | `WebAutoAdmitEngine` (portable) or UIA | Web engine only | `NOT_STARTED` |
| Attendance snapshots | `AttendanceCollector` per live meeting | Worker, web source | `NOT_STARTED` |
| Attendance correction | Zoom participants report after `Ended` | `ZoomParticipantsReportReader` (portable) | `NOT_STARTED` |
| Name matching | `AttendanceMatching` + optional AI | Server-side; already portable | `NOT_STARTED` |
| LMS completion | `LmsSessionRunner` | Worker | `NOT_STARTED` |
| Recording link → LMS | `RecordingLinkProcessor` via `recording.process` | **Already works** | Exists |
| n8n integration | `X-API-Key` → backend | Unchanged | Exists |
| Sheets / Drive | `RecordingSheet`, `RecordingLinks` | Worker | `NOT_STARTED` |

### Concurrency, and what must not be copied

`ProfileOperationLock` is a **cross-process file lock per browser profile**. `DELEGATED-CLASSES.md`
records that the lock is taken per profile name, so two coordinators' LMS steps run side by side.
That design carries to Linux unchanged and is the reason simultaneous sessions work at all.

The rule it enforces must survive: **never open one `userDataDir` in two processes, and never copy a
live profile to fake concurrency.** The existing policy gives the first live class the Zoom desktop
app and later ones the web engine, up to eight profiles per account. On Linux there is no desktop
app, so **every** class is a web profile and the per-account ceiling becomes the real concurrency
limit. That ceiling has not been measured and must not be promised.

---

## 5. What is genuinely at risk

These are the rows that decide whether the migration can be completed, and none can be settled by
reading code.

| Risk | Why | What would settle it |
|---|---|---|
| **Co-host assignment on web** | `SessionRoles` drives the desktop UI. `ZoomWebToolbar` and `ZoomWebBreakoutRooms` exist, but nothing in the repository assigns a co-host through the web client | One live test meeting on Linux |
| **Waiting-room admission on web** | `ZoomWaitingRoomDom` + `WebAdmissionVerifier` exist and are used by `WebAutoAdmitEngine`, so this is the best-supported path — but has never run headless on Linux | One live test meeting, headless |
| **Zoom reports on web** | `ZoomParticipantsReportReader` scrapes `zoom.us/account/my/report`. Scraping an account page may need a paid plan for some report types | A test account of the real plan |
| **Zoom sign-in on Linux** | `ZoomWebSignIn` reads a saved credential and may hit MFA/CAPTCHA. MFA must never be bypassed | A Connect-account flow with a temporary remote browser |
| **Headless viability** | Zoom's web client may refuse headless Chromium | Test headless first, fall back to Xvfb |
| **LMS behaviour** | DEPI LMS automation has only ever run on Windows Chromium | A test coordinator account |

Nothing in this table will be reported as working until it has run against the real system. A build
that succeeds, a unit test that passes, and a mocked page are not evidence.

---

## 6. What does not migrate

Kept on Windows, and the Windows application keeps working exactly as it does now:

- `UIAutomation` — FlaUI, 35 P/Invokes, screen capture, WinRT OCR. There is no Linux equivalent and
  the task forbids Wine, a Windows VM, and a local agent.
- `WindowsRuntime` — Task Scheduler, keyboard input, desktop Zoom launch.
- `WindowsUI`, `Inspector`, `WaitingRoomTester`, `Setup`.

The consequence is that **desktop Zoom automation has no cloud equivalent**. Everything the cloud
does, it does through the Zoom web client. Where a capability exists only in the desktop path, it is
listed as unsupported rather than quietly dropped.

---

## 7. Decisions still needed

Listed here rather than guessed. Each one changes what gets built.

1. **Zoom plan** — does the account whose reports are read have a paid plan? Some report pages are
   plan-gated, and the task forbids making a new subscription mandatory.
2. **Who connects accounts** — the admin for everyone, or each coordinator for themselves? This
   decides whether the Connect-account flow is an admin page or a coordinator page.
3. **Concurrency target** — how many simultaneous classes must one VPS carry? Each web class is a
   Chromium profile; the number sets the RAM and `/dev/shm` budget.
4. **Migration scope** — every coordinator at once, or one group as a pilot?
5. **Test access** — a Zoom account and a DEPI LMS account authorised for testing, and a class that
   is safe to run against. Without these, every live row stays `IMPLEMENTED_NOT_LIVE_VERIFIED`.
6. **VPS capacity and domain** — RAM, cores, disk, and the hostname the dashboard answers on.

Credentials are never to be sent in chat; they belong in the server's own secret storage.

---

## 8. Change log

| Date | Change |
|---|---|
| 2026-09-19 | `Core` and `WebAutomation` multi-target `net8.0;net8.0-windows10.0.19041.0`. Both build for `net8.0`; the full Windows solution still builds with 0 errors and 0 warnings. The `net8.0` build of `WebAutomation` is **not** runnable yet — see §2 |
| 2026-09-19 | The credential seam is built: `ILmsCredentialBackend`, `LmsCredentialBackend.Current`, `InMemoryLmsCredentialBackend`, and a resolver for Zoom sign-in references. `CentralAgent` multi-targets too, with `FileDeviceTokenStore` for a machine that has no Credential Manager |
| 2026-09-19 | `ZoomAutoAdmit.CloudWorker` added: `net8.0` only, on purpose, so a Windows-only reference cannot creep in unnoticed. Settings from the environment, a six-check preflight that actually launches Chromium, in-memory credentials, and enrolment against the existing device-token flow. It does **not** connect yet, because there are no job types for the class stages to answer for |
| 2026-09-19 | `deploy/`: Dockerfiles for the backend and the worker, `docker-compose.coolify.yml`, `.env.example`, and `COOLIFY_DEPLOYMENT.md` |
| 2026-09-19 | Both images built and the stack run. **Chromium 151.0.7922.34 launches headless on Linux inside the worker image** — the portability question this whole migration rests on. The `/dev/shm` guard was tested both ways. Postgres and backend both report healthy, `0001`→`0013` migrate from empty into 24 tables, and the several-admins rules were exercised over HTTP against that database: two admins created, the second signed in as `admin` and made a third, promoting a coordinator refused with `400`, deleting one answered with what went. Five defects were found by building rather than by reading, and fixed |
| 2026-09-20 | The class stages exist. Nine job types on the server (`class.open`, `class.admit`, `class.attendance`, `class.end`, `zoom.report`, `zoom.recording`, `lms.run_session`, `lms.attendance`, `lms.complete`), each routed by capability so a recording-only agent is never handed one, with a shared validator that insists a class says whose it is. Three of them are built in the worker, on the same `LmsSessionRunner` the Windows app drives. **Running the portable tests inside a Linux container for the first time found 60 failures the Windows `net8.0` build had hidden** - all `RecordingApiServer`, all `HttpListener`. It is out of the portable build now: 207 + 19 tests pass on Linux, 322 still pass on Windows |
| 2026-09-20 | A worker can get the sign-in a class is written up under, without a standing key to anyone's account: `POST /api/v1/agent/jobs/{jobId}/lms-secret` answers only for a job that device holds and has not finished, only for the account its payload names, and only while that coordinator is turned on; turning one off closes it at once. Audited under the device's name. Found on the way: `ILmsCredentialStore` did not carry the browser profile, so `LmsSessionRunner` fell back to the shared legacy one for any store that was not the concrete class - two coordinators' stages would have signed each other out. The profile is on the interface now |
| 2026-09-20 | The worker connects. It registers with its enrolment token, opens the socket, and the backend lists it `online`, `connected: true`, `capabilities: ["lms"]` - run against a real backend in a container, not mocked. The account source reads the job id from `CentralAgentService.RunningJobId`, so a sign-in is asked for only while a stage is actually being run |
| 2026-09-20 | A class stage ran the whole way for the first time: job created on the backend, taken by the worker over its socket, the LMS sign-in fetched with the device token, **Chromium launched on Linux and opened the real DEPI LMS sign-in page**, the sign-in refused (the account was a seeded test one), and the failure recorded on the job with its reason. The read is in `admin_audit_log` under the device's name. What is proved is the whole chain up to the LMS's own login form; what is not is any LMS step succeeding, which needs a real account |
| 2026-09-20 | **Every browser this system launches was running without a sandbox, and the code said otherwise.** Playwright's `chromiumSandbox` defaults to false - it passes `--no-sandbox` unless asked not to - and the preflight additionally carried `--no-sandbox=false`, which reads as if it keeps the sandbox on and does the opposite, because Chromium reads that switch by its presence and not its value. Both launch paths now ask for the sandbox by name. Measured in the container: without `seccomp=unconfined` Chromium refuses to start and the check fails; with it - which is what `docker-compose.coolify.yml` already sets - the sandbox builds and the check passes. The compose file was right all along; the flag was hiding the fact that nothing was testing it |
