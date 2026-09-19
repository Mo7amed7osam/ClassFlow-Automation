# Zoom Auto Admit — Windows 11 Native Implementation

This directory contains the native Windows 11 implementation of **Zoom Auto Admit**, built using **C#**, **.NET 8**, and **Windows UI Automation (FlaUI with UIA3)**.

---

## Architectural Principles & Constraints

1. **Isolation from macOS Code**: All Windows components reside strictly in `Windows/`. The existing macOS Swift codebase is untouched.
2. **Background Automation**: All discovery and automation operate safely while Zoom is in the background or behind other windows.
3. **No Brittle Mechanisms**:
   - ❌ No simulated mouse clicks or screen coordinate hardcoding.
   - ❌ No OCR or image recognition.
   - ❌ No Python, Node.js, Electron, or AutoHotkey dependencies.
   - ❌ No forced window activation (`SetForegroundWindow`, focus stealing, etc.).
   - ✔️ Pure Windows UI Automation (FlaUI UIA3) with control patterns (e.g., `InvokePattern`).
4. **Milestone 1 Scope**: Strictly read-only inspection and discovery of the real Zoom Workplace UI Automation hierarchy. **No auto-clicking or mutating actions are performed.**

---

## Solution Structure

```
Windows/
├── ZoomAutoAdmit.Windows.sln
├── README.md
├── src/
│   ├── ZoomAutoAdmit.Core/           # Core models, formatting, and filtering logic
│   ├── ZoomAutoAdmit.UIAutomation/   # FlaUI UIA3 tree inspector & process discovery
│   ├── ZoomAutoAdmit.WebAutomation/  # Managed Playwright Chromium + Zoom Web DOM automation
│   └── ZoomAutoAdmit.Inspector/      # Diagnostic console CLI executable
└── tests/
    ├── ZoomAutoAdmit.Core.Tests/     # Unit tests for formatters and filters
    ├── ZoomAutoAdmit.UIAutomation.Tests/ # Windows automation tests
    └── ZoomAutoAdmit.WebAutomation.Tests/ # Profile, mocked DOM, policy & verification tests
```

## Whose class is this?

A PC runs its own classes, and - on the admin's copy - the classes of the coordinators the admin
turns on. Each class carries its owner: their Zoom account opens the meeting, and their LMS account
presses Run Session, writes the attendance and puts the recording link on it. The LMS sign-in is not
a global setting: `ClassLmsAccounts` maps each group to the account its classes go up under, and
every LMS step asks it first, falling back to the account in use for a group nobody claimed. Two
coordinators' classes therefore run at the same time, each on its own browser profile and its own
lock. See [DELEGATED-CLASSES.md](../DELEGATED-CLASSES.md).

## Auto-Admit Engines

The continuous command supports two isolated engines through the shared
`IAutoAdmitEngine` contract:

- `--engine windows` (default): the existing Windows desktop OCR/UI Automation flow.
- `--engine web`: Playwright DOM automation in an application-managed persistent
  Chromium context.

The web engine does not automate login, read or store plaintext credentials,
take screenshots, run OCR, use desktop mouse coordinates, use SendInput, or
attach to an external browser. It launches Playwright Chromium itself and uses exact accessible button
names inside the Zoom meeting DOM.

Profiles are stored outside the repository under the current operating-system
user's local application-data directory:

```text
ZoomAutoAdmit/
└── Profiles/
    ├── Default/
    ├── account1/
    └── account2/
```

The first run of a profile is visible so the user can log into Zoom manually and
join the meeting. The profile is marked reusable only after Zoom host controls
are detected. Cookies and local storage remain inside Chromium's persistent
profile; no username/password file is created. Future runs reuse the profile and
default to headless mode. Use `--headed` whenever manual session refresh is
needed. Profile directories contain sensitive browser session data and must not
be shared or committed.

The launcher installs the Playwright Chromium runtime automatically when it is
missing. It never requires Chrome to be open.

First run or manual login refresh:

```powershell
dotnet run --project Windows/src/ZoomAutoAdmit.Inspector -- `
  waiting-room-auto-admit `
  --engine web `
  --profile default `
  --meeting-url "https://example.zoom.us/j/123456789" `
  --headed `
  --poll-ms 750 `
  --timeout 0
```

Future runs can omit `--headed`:

```powershell
dotnet run --project Windows/src/ZoomAutoAdmit.Inspector -- `
  waiting-room-auto-admit `
  --engine web `
  --profile default `
  --meeting-url "https://example.zoom.us/j/123456789" `
  --timeout 0
```

`--poll-ms` is constrained to 500–1000ms. `--timeout 0` runs until Ctrl+C.
The existing Windows command remains available with `--engine windows` or by
omitting `--engine`.

---

## Prerequisites

- **Operating System**: Windows 11 (or Windows 10 x64)
- **.NET Runtime & SDK**: .NET 8.0 SDK (`winget install Microsoft.DotNet.SDK.8` or [dot.net](https://dotnet.microsoft.com/download/dotnet/8.0))
- **Zoom Client**: Zoom Workplace for Windows desktop client installed and running
- **Web engine**: An HTTPS Zoom meeting URL; Chromium is managed by Playwright

---

## Building and Testing

### 1. Restore NuGet Packages
```powershell
dotnet restore Windows/ZoomAutoAdmit.Windows.sln
```

### 2. Build the Solution
```powershell
dotnet build Windows/ZoomAutoAdmit.Windows.sln --configuration Release
```

### 3. Run Unit Tests
```powershell
dotnet test Windows/ZoomAutoAdmit.Windows.sln --configuration Release
```

> **Testing Classification Note**:
> - **UNIT TESTED**: CLI argument parsing, output formatters, tree search filtering, candidate sorting logic, safe property extraction models.
> - **REQUIRES LIVE ZOOM VALIDATION**: Real-time tree traversal against a live Zoom Workplace process on a Windows desktop.
> - **OPT-IN, TOUCHES THE REAL TASK SCHEDULER**: `LiveTaskSchedulerCreatesAndDeletesRealWindowsTask` is skipped unless `ZOOMAUTOADMIT_LIVE_SCHEDULER_TESTS=1` is set. When it runs it creates and deletes one task under `\ZoomAutoAdmitTests\`, never under `\ZoomAutoAdmit\` where the scheduled meetings live. No other test touches the Task Scheduler.

---

## Running the Inspector CLI

Navigate to the built output directory or run directly via `dotnet run`:

```powershell
dotnet run --project Windows/src/ZoomAutoAdmit.Inspector -- <command> [options]
```

### Available Commands

| Command | Description |
|---|---|
| `inspect` | Safely traverses and prints the exposed Zoom UI Automation tree (read-only). |
| `inspect --all` | Deeper diagnostic inspection with higher element and depth limits. |
| `processes` | Lists candidate Zoom processes, paths, PIDs, and top-level window handles. |
| `find "<term>"` | Searches the hierarchy for elements whose Name, AutomationId, or ClassName contains the search term. |

### Diagnostic Options

- `--max-depth <N>`, `-d <N>`: Maximum tree recursion depth (default: `15`, `--all`: `35`).
- `--max-elements <N>`, `-m <N>`: Maximum visited elements threshold (default: `800`, `--all`: `3000`).
- `--pid <PID>`, `-p <PID>`: Target a specific Zoom process ID (if multiple Zoom instances run).

---

## Live Discovery Scenarios (Required for Baseline UI Mapping)

To accurately derive reliable automation selectors without guessing, capture the UI Automation output for the following three scenarios:

### Scenario A — Zoom Home
1. Open Zoom Workplace normally.
2. Ensure you are on the main dashboard / home screen.
3. Run:
   ```powershell
   dotnet run --project Windows/src/ZoomAutoAdmit.Inspector -- inspect
   ```
4. **Target Controls to Discover**:
   - "New Meeting", "Join", "Schedule" buttons
   - "Meetings", "Team Chat", navigation tabs
   - Profile avatar / account settings button
   - Record exact `ControlType`, `AutomationId`, `ClassName`, and pattern availability (e.g. `InvokePattern`).

---

### Scenario B — Saved Accounts / Switch Account
1. Open Zoom Workplace.
2. Click on the profile/account icon to open the account switching menu/panel.
3. Run:
   ```powershell
   dotnet run --project Windows/src/ZoomAutoAdmit.Inspector -- inspect
   ```
4. **Target Controls to Discover**:
   - Account list items / email entries
   - Active/selected account indicator
   - "Switch Account" or "Add Account" buttons
   - Menu/List hierarchy structure and supported patterns (`SelectionItemPattern`, `InvokePattern`).

---

### Scenario C — Active Host Meeting + Waiting Room
1. Start a Zoom meeting as Host.
2. Ensure **Waiting Room** is enabled.
3. Open the **Participants** panel.
4. Have a secondary test device/account join the meeting and enter the Waiting Room.
5. Run the inspector and find commands:
   ```powershell
   dotnet run --project Windows/src/ZoomAutoAdmit.Inspector -- inspect
   dotnet run --project Windows/src/ZoomAutoAdmit.Inspector -- find "Admit"
   ```
6. **Target Controls to Discover**:
   - Participants list container
   - Waiting room section header
   - Individual participant rows / labels
   - Individual "Admit" button (`AutomationId`, `ControlType`, `InvokePattern`)
   - "Admit All" button (`AutomationId`, `ControlType`, `InvokePattern`)

---

## First Live Inspection Command

To inspect your currently running Zoom Workplace client right now:

```powershell
dotnet run --project Windows/src/ZoomAutoAdmit.Inspector -- inspect
```
