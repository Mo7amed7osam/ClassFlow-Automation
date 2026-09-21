# Zoom Auto Admit — working notes

One repository, five parts. They ship separately and share the central backend's HTTP/WS contract.

| Part | Language | Where | Runs on |
|---|---|---|---|
| macOS menu-bar app | Swift 5.9 | `Sources/`, `Tests/`, `Package.swift`, `Scripts/` | macOS 13+, Apple Silicon |
| Windows agent + UI | C# / .NET 8, WPF, FlaUI (UIA3) | `Windows/src/`, `Windows/tests/` | Windows 10/11 |
| Central backend | Python 3.11+, FastAPI, SQLAlchemy async, PostgreSQL | `Backend/central_backend/` | anywhere |
| Dashboard | React 19 + TypeScript + Vite + Tailwind 4 | `Dashboard/src/` | anywhere; served by the backend from `Dashboard/dist` |
| Chrome extension | JS | `Windows/ZoomAutoAdmit.ChromeExtension/` | Chrome |

This file describes the `Windows_and_web` branch, where the Windows agent, the backend and the
dashboard are built. Branch from it unless told otherwise.

`main` is the repository's default branch and has **diverged**, not fallen behind: from the common
ancestor `415593a`, `main` carries 39 commits and `Windows_and_web` 51, and neither contains the
other. They have been developed in largely separate trees — `main` in `Sources/` (the macOS app),
`web-extension/` and `automation/`; `Windows_and_web` in `Windows/`, `Backend/` and `Dashboard/`, with
a small overlap in `Sources/ZoomAutoAdmitCore`. `main` also renames the project to **ClassFlow
Automation** in its README, a name this branch's documents do not yet use, and the GitHub repository
itself is now `Mo7amed7osam/ClassFlow-Automation`.

So check which branch is checked out before trusting a path or a name from memory, and do not assume
work present on one side exists on the other. A session that starts from `main` is not looking at the
Windows, backend or dashboard work described here.

## Build and test

Backend — the part most work touches, and the one that runs on any machine:

```bash
cd Backend && python -m venv .venv && .venv/bin/pip install -e ".[dev]" && .venv/bin/python -m pytest
```

On Windows use `.venv\Scripts\python` instead. The tests bring up a throwaway PostgreSQL cluster from
the binaries on the machine (`CENTRAL_TEST_PG_BIN`, then `PATH`, then the usual install folders). With
no PostgreSQL present **the whole suite skips** rather than fails — install `postgresql` or point
`CENTRAL_TEST_DATABASE_URL` at a disposable database, and read the skip notice before calling the
suite green.

Dashboard:

```bash
cd Dashboard && npm ci && npm test && npm run typecheck
```

`npm run build` runs `tsc --noEmit` then `vite build`. The backend serves the result from
`CENTRAL_DASHBOARD_DIST` (default `../Dashboard/dist`), which is not committed.

Windows, on a Windows machine with the .NET 8 SDK:

```powershell
dotnet test Windows/ZoomAutoAdmit.Windows.sln -c Release
```

Nearly every project targets `net8.0-windows10.0.19041.0` and will not build off Windows. Exactly two
are plain `net8.0` with no Windows-only references, so they and their test projects build and run
anywhere:

```bash
dotnet test Windows/tests/ZoomAutoAdmit.Roster.Tests
dotnet test Windows/tests/ZoomAutoAdmit.AttendanceMatching.Tests
```

`ZoomAutoAdmit.Core` and `ZoomAutoAdmit.SessionRoles` are Windows-targeted despite holding logic that
need not be. Prefer `Roster` or `AttendanceMatching` for new platform-independent logic, so it stays
testable without a Windows machine.

macOS, on a Mac: `swift test`, and `./Scripts/build-app.sh release` for the app bundle.

`Windows/Codex-runner.ps1` is the local escape hatch: the user starts it on their Windows PC, and it
runs `build`, `run`, `test` or `cmd <line>` requests written to `Windows/.Codex-run-request`, logging
to `Windows/Codex-runner.log`. Only useful in a session with access to that machine.

## What cannot be verified away from the real machines

Waiting-room admission, Zoom UI Automation, the WPF window, Playwright against live Zoom, macOS
Accessibility, and Keychain all need a real desktop with Zoom signed in. Away from one, say what was
checked and what was not instead of implying the feature was exercised.

## On a cloud box

Read [CLOUD.md](CLOUD.md) first. `./Scripts/cloud-setup.sh` then `./Scripts/cloud-verify.sh` gets a
fresh Linux machine to the point where everything runnable runs, and the verify script reports
PASSED / FAILED / SKIPPED honestly — including the trap where the backend suite skips every test and
still exits 0 because the box has no PostgreSQL.

## Secrets

`Backend/cloud.env` holds development settings for a cloud box and is committed. Every name in it is
documented with its default in [Backend/README.md](Backend/README.md#settings-environment); use that
table when something needs configuring.

`api.env` and `api_key.env` in the root are the operator's local scrollback, committed deliberately
and temporarily so a cloud session is not blocked waiting on a person. Their values are development
ones: a `127.0.0.1` database with no password, `development` as the environment, and two `zaak_` keys
from that PC. They are to be rotated and dropped when the cloud work ends, and
[CLOUD.md](CLOUD.md) says how.

Nothing else: no real key, token or password goes into a commit, an issue, a PR or any other outbound
message. `CENTRAL_AI_API_KEY` in particular stays empty in the committed file and is exported in the
shell of the one session that needs it.

`diagnostics/` and `Windows/diagnostics/` are screen captures written while the app runs. They are
regenerated, never source, and stay out of the repository.

## Conventions

- Prose in docs and commit messages is plain declarative English describing what the thing does, not
  what was done to it. Existing commit subjects are the model.
- No new third-party dependency without a reason that the existing stack cannot serve. Both desktop
  apps advertise having none beyond their platform SDKs (plus FlaUI/Playwright on Windows).
- The Windows automation path takes no screenshots, no OCR, no mouse coordinates, no `SendInput`, and
  never forces a window to the foreground. `Windows/README.md` states this as a constraint; keep it.
- Every document in the list below is current and worth reading before changing the area it covers.

## Documentation map

- [README.md](README.md) — the macOS app, accounts, hybrid session allocation
- [Backend/README.md](Backend/README.md) — endpoints, CLI, settings table, tests
- [Backend/ARCHITECTURE.md](Backend/ARCHITECTURE.md) — data model and job lifecycle
- [Dashboard/README.md](Dashboard/README.md) — pages and roles
- [Windows/README.md](Windows/README.md) — solution structure, engines, constraints
- [Windows/CENTRAL-AGENT.md](Windows/CENTRAL-AGENT.md) — how an agent PC enrolls and takes jobs
- [Windows/ATTENDANCE.md](Windows/ATTENDANCE.md), [Windows/ATTENDANCE-MATCHING.md](Windows/ATTENDANCE-MATCHING.md) — attendance capture and name matching
- [Windows/SESSION-ROLES.md](Windows/SESSION-ROLES.md), [Windows/SESSION-ROLES-INSPECTION.md](Windows/SESSION-ROLES-INSPECTION.md) — roles in a session
- [DELEGATED-CLASSES.md](DELEGATED-CLASSES.md) — one PC running several coordinators' classes, each under their own accounts
- [Windows/GROUP-ROSTER.md](Windows/GROUP-ROSTER.md), [Windows/STUDENT-ROSTER.md](Windows/STUDENT-ROSTER.md), [COORDINATOR-ROSTER.md](COORDINATOR-ROSTER.md) — the rosters
- [Windows/RECORDING-API.md](Windows/RECORDING-API.md) — recordings
- [Windows/WINDOWS-UI-DESIGN.md](Windows/WINDOWS-UI-DESIGN.md) — the WPF window's design language
- [Windows/ACCOUNT-IDENTITY.md](Windows/ACCOUNT-IDENTITY.md), [Windows/OPENROUTER-SETUP.md](Windows/OPENROUTER-SETUP.md), [Windows/AI-SETUP-SCHEDULE-UPLOAD.md](Windows/AI-SETUP-SCHEDULE-UPLOAD.md) — identity and the AI-assisted setup
