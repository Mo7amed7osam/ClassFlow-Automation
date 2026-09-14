# Zoom Auto Admit

Native macOS menu-bar automation for admitting Zoom Waiting Room participants without stealing focus.

![Platform](https://img.shields.io/badge/platform-macOS%2013%2B-111827?style=flat-square)
![Language](https://img.shields.io/badge/language-Swift-ff6b35?style=flat-square)
![Dependencies](https://img.shields.io/badge/dependencies-none-198754?style=flat-square)

Zoom Auto Admit watches Zoom's Accessibility hierarchy and presses `Admit All` when it is available. It can also press individual `Admit` buttons, run as a native menu-bar app, and schedule meetings from saved account profiles. It never uses coordinates, screenshots, Zoom APIs, or foreground activation on the monitoring path.

## Highlights

- Runs quietly from the macOS menu bar with no Dock icon.
- Monitors Zoom in the background while it remains on the current Desktop.
- Uses Accessibility permission and PID-scoped UI reads instead of mouse coordinates.
- Includes a diagnostic inspector for reviewing Zoom's live Accessibility tree.
- Supports scheduled meeting startup, account selection, and launch at login.
- Has a reusable monitor and workflow core with unit tests and no third-party dependencies.
- Optional **Automation** (menu → Automation…): DEPI dashboard Run Session, attendance upload and late-joiner correction, recording links, an n8n recording API, Excel timetable and roster import, Zoom Web meetings for overlapping classes, and automatic co-host. Uses a bundled Node.js + Playwright helper; the core app still runs without it.

## Requirements

- macOS 13 or later
- Apple Silicon Mac
- Zoom Workplace for macOS
- Accessibility permission for the installed app
- Swift 5.9 toolchain (included with Xcode Command Line Tools)

## Quick start

### Chrome / Edge extension (Zoom Web Client)

For meetings hosted at `app.zoom.us`, download `zoom-auto-admit-web.zip` from the
[`web-v0.2.0` release](https://github.com/Mo7amed7osam/zoom-auto-admit/releases/tag/web-v0.2.0),
unzip it, open `chrome://extensions` or `edge://extensions`, enable **Developer
mode**, then choose **Load unpacked** and select the unzipped folder.

The web extension now includes attendance tracking: import the official roster,
start attendance, keep Zoom's Participants panel open, and it captures visible
participant names every 15 seconds. Results preserve the roster's original order,
show captured-but-unmatched Zoom names separately, and enforce one-to-one matching
so one student cannot be marked present under two different Zoom identities.

OpenRouter is optional and is used only when you press **Match unresolved names
with AI**. Create a key at <https://openrouter.ai/settings/keys>, save it in the
Attendance screen, and use `openrouter/free` for the free-model router. A `402`
means the selected model/account cannot currently serve the request; switch to
`openrouter/free` and check the key's limits or account balance. The key is stored
only in the current browser profile—use a restricted key and do not share it.

Full extension instructions are in [`web-extension/README.md`](web-extension/README.md).

### Native macOS app

Build the release app:

```sh
./Scripts/build-app.sh release
```

Install and launch it:

```sh
ditto "dist/Zoom Auto Admit.app" "/Applications/Zoom Auto Admit.app"
open "/Applications/Zoom Auto Admit.app"
```

Then grant Accessibility access to `/Applications/Zoom Auto Admit.app` in **System Settings → Privacy & Security → Accessibility**. The app's menu includes **Open Accessibility Settings**, **Check Again**, and diagnostic status details.

Run the test suite:

```sh
swift test
```

Inspect Zoom's live hierarchy while its Participants panel is open:

```sh
./run-inspect.sh
```

## Attendance snapshots

A schedule records attendance only when its **Attendance** setting links a saved student group. Once the scheduled workflow verifies the meeting, the app creates and persists one `AttendanceSession`, takes a `meeting_started` snapshot within a few seconds, checks for a periodic snapshot every five seconds without continuously scanning Zoom, coalesces Auto Admit bursts into one `post_admit` snapshot, and supports **Attendance → Take Snapshot Now**.

Attendance is evidence-only: only Zoom rows exposed as `ZMHCTableItemType_PANELIST` are eligible, Host/me/co-host rows and group ignore names are excluded, and `WAITINGLIST` rows never count. Successful snapshots accumulate as a union; an unreadable snapshot removes nobody and does not stop Auto Admit or the scheduler.

The schedule and attendance pipeline writes compact diagnostics to:

```sh
tail -f "$HOME/Library/Logs/Zoom Auto Admit/scheduler.log"
```

Relevant lines begin with `[attendance]` and report schedule/group linkage, session creation, snapshot reason, Zoom PID and AX window count, raw PANELIST rows, filtered names, persistence, reconciliation, and the next due snapshot. No OpenRouter key or credential is logged.

For a live isolation test, start a linked scheduled meeting and choose **Attendance → Take Snapshot Now**. Then verify `parser available=true`, a `snapshot-captured` line, and `session-saved=true`. Persisted sessions are under `~/Library/Application Support/Zoom Auto Admit/Attendance/` and appear in **Attendance → Review Attendance…**.

### Match with AI

Add an OpenRouter key under **Settings → AI attendance matching**, then open an attendance session and press **Match with AI**. Exact names, learned aliases, and other confidently resolved deterministic matches remain local. After those are removed, one request contains every unresolved student and every still-unclaimed observed PANELIST identity; no fuzzy score, token overlap, transliteration score, or script check filters AI candidate pairs.

The request uses temporary IDs such as `s0` and `z0`, never session/student UUIDs. OpenRouter is instructed to compare the whole remaining roster globally, account for Arabic/English transliteration and incomplete names, and return one-to-one assignments. Returned IDs are validated against that exact request and its current session observations. A model response cannot create attendance evidence, reference another group, reuse a claimed Zoom identity, or overwrite a manual decision. Matches below the group's threshold—or explicitly marked `needs_review`—stay **Needs Review**.

## Native menu bar app

The primary build is a native macOS menu-bar application. It has no normal window, uses an SF Symbol status icon, and runs with AppKit's accessory activation policy plus `LSUIElement=true`, so it does not appear in the Dock during normal use.

The menu shows monitoring, Zoom, Waiting Room, Accessibility, last-action, Auto Admit, Start/Stop, Launch at Login, and Quit controls. Monitoring runs on a dedicated utility queue and does not depend on the menu being open.

### Build the application

```sh
cd "/Users/mohamedhosam/Documents/ChatGPT/New project/zoom-auto-admit"
./Scripts/build-app.sh release
```

The build script compiles the arm64 release executable, creates the bundle, validates `Info.plist`, and verifies its code signature. It prefers an installed **Developer ID Application** or **Apple Development** signing identity so Accessibility grants remain associated with a stable code identity across rebuilds. If no suitable identity is present, it warns and falls back to ad-hoc signing; ad-hoc rebuilds can require removing and re-adding the Accessibility entry. Set `ZOOM_AUTO_ADMIT_SIGNING_IDENTITY` to select a specific local signing identity.

```text
/Users/mohamedhosam/Documents/ChatGPT/New project/zoom-auto-admit/dist/Zoom Auto Admit.app
```

### Install and open

Copy the generated app to Applications in Finder, or run once:

```sh
ditto "/Users/mohamedhosam/Documents/ChatGPT/New project/zoom-auto-admit/dist/Zoom Auto Admit.app" "/Applications/Zoom Auto Admit.app"
open "/Applications/Zoom Auto Admit.app"
```

After installation, normal use does not require Terminal.

### Grant Accessibility to the app

1. Open **Zoom Auto Admit** from `/Applications`.
2. Click its menu-bar icon. The menu will show **Accessibility permission required**.
3. Choose **Open Accessibility Settings**.
4. In **System Settings → Privacy & Security → Accessibility**, add `/Applications/Zoom Auto Admit.app` with the `+` button if it is not already listed, then enable it.
5. Quit and reopen the app if macOS has just changed the permission, then choose **Check Again**.

Grant access to **Zoom Auto Admit.app itself**, not Terminal. No Automation/System Events or Full Disk Access permission is required.

**Check Again** performs fresh calls to both `AXIsProcessTrusted()` and `AXIsProcessTrustedWithOptions(prompt=false)`; the result is not cached. It also verifies the actual running path, PID, bundle identifier, executable, installed `/Applications` copy, and code signature. If Accessibility is genuinely unavailable to the process — the trust APIs disagree, or an Accessibility call returns `kAXErrorAPIDisabled` — the menu shows **Permission granted — relaunch app**. No other AXError can produce that state; see *Background monitoring architecture* below.

Choose **Open Accessibility Diagnostic Log** from the menu, or inspect it directly:

```sh
tail -n 100 "$HOME/Library/Logs/Zoom Auto Admit/accessibility.log"
```

For a live unified-log stream:

```sh
log stream --style compact --level info \
  --predicate 'subsystem == "com.mohamedhosam.ZoomAutoAdmit"'
```

The diagnostic distinguishes `notTrusted`, `appBundleMismatch`, `possibleStaleTCCEntry`, `relaunchRequired`, and `trusted`. macOS exposes no public API for reading the Accessibility pane or TCC database directly, so `possibleStaleTCCEntry` is an evidence-based diagnosis: the installed app matches, both trust APIs remain false after the user explicitly chooses **Check Again**, or the recorded code-sign identity changed.

If an older ad-hoc build is already enabled but remains untrusted, quit Zoom Auto Admit, remove its Accessibility row with the `−` button, install the newly signed build, add `/Applications/Zoom Auto Admit.app` again, enable it, and relaunch the app.

### Use and test

1. Start a Zoom meeting as Host, enable Waiting Room, and open Participants.
2. Keep Zoom in the currently active macOS Desktop. Zoom may remain behind another application.
3. Join from a second account/device and leave it waiting.
4. Ensure **Auto Admit** is checked. The menu should change to Monitoring and then record the admit under Last action.
5. Use **Stop Monitoring** and **Start Monitoring** to pause/resume safely. **Quit** cancels the monitor before terminating.
6. Enable **Launch at Login** after the app is installed in `/Applications`; this uses `SMAppService.mainApp`.

The menu-bar release deliberately does not perform automatic Cross-Space switching. If Zoom's meeting window is on another Desktop, the menu reports that state and asks you to move Zoom to the current Desktop or assign Zoom to All Desktops. The experimental CLI Cross-Space mode remains available for diagnostics but is not enabled by the app.

Participant names are retained only in an in-memory list of up to ten recent actions and are discarded when the app quits.

Small macOS command-line utility that watches Zoom's exposed Accessibility hierarchy and presses a Waiting Room `Admit All` button when one is available. If Zoom exposes only exact `Admit` buttons, it presses one per scan and rescans after the UI updates. It never changes Zoom settings, uses coordinates, uses screenshots, calls a Zoom API, or touches another application.

This first version is intentionally driven by the live Accessibility hierarchy. Zoom can change its UI between releases, so run the inspector with the Participants panel open before using the monitor.

## Files

- `Package.swift` — SwiftPM manifest for macOS 13+ and the two executables.
- `Sources/ZoomAXSupport/ZoomAXSupport.swift` — shared process validation, AX tree traversal, exact title/description matching, identifier-first Waiting Room checks, and AX press support.
- `Sources/InspectZoom/inspect_zoom.swift` — prints the Zoom window hierarchy, including role, title, description, value, identifier, path, enabled state, and actions.
- `Sources/AutoAdmit/auto_admit.swift` — guarded polling loop, logging, dry-run mode, signal-based stop handling, and safe no-op behavior when Zoom or the panel disappears.
- `Sources/ZoomAXSupport/AXAccess.swift` — typed AXError handling: separates `apiDisabled` (permission) from `invalidUIElement` (stale) and `cannotComplete`/`noValue` (transient), plus bounded AX messaging timeouts.
- `Sources/ZoomAXSupport/ZoomScanner.swift` — one complete, freshly acquired scan of the Zoom hierarchy by PID, and the Accessibility-first meeting-location classifier.
- `Sources/ZoomAXSupport/ZoomAXActivityObserver.swift` — `AXObserver` on the Zoom process; an accelerator only, never the source of truth.
- `Sources/ZoomAutoAdmitCore/AutoAdmitMonitor.swift` — reusable background monitor, retry state machine, and event model used by the menu-bar app.
- `Sources/ZoomAutoAdmitCore/ZoomAccess.swift` — the single seam through which every Zoom-facing Accessibility call flows, so the state machine is testable against real failure modes.
- `Sources/ZoomAXSupport/ZoomMenuBar.swift` and `ZoomMenuBarAccess.swift` — pure account matching over Zoom's Switch account submenu, and the guarded live press.
- `Sources/ZoomAXSupport/ZoomMeetingPresence.swift` — active/notActive/unknown meeting detection from Accessibility evidence only.
- `Sources/ZoomAXSupport/ZoomUICapture.swift` — read-only capture of Zoom's live hierarchy for diagnostics.
- `Sources/ZoomAutoAdmitCore/Scheduling/` — schedule models, recurrence arithmetic, JSON persistence, the firing service and the workflow log.
- `Sources/ZoomAutoAdmitCore/Workflow/` — the scheduled-start state machine, its automation seam and the live implementation.
- `Sources/ZoomAutoAdmitApp/SchedulerCoordinator.swift` and `SchedulerWindowController.swift` — scheduler wiring into the existing monitor, and the schedules/profiles editor.
- `Sources/ZoomAutoAdmitCore/ZoomAXActivitySource.swift` — keeps the observer following Zoom's PID across launches and quits.
- `Sources/ZoomAutoAdmitApp/` — AppKit menu-bar application, state, menu controller, launch-at-login integration, and uncached Accessibility/code-sign diagnostics.
- `AppBundle/Info.plist` and `Scripts/build-app.sh` — menu-only app metadata and reproducible `.app` bundle builder.
- `Tests/ZoomAXSupportTests/ZoomAXSupportTests.swift` — pure matcher tests covering the observed Zoom hierarchy and safety rejection cases.
- `run-inspect.sh` and `run-auto-admit.sh` — optional convenience launchers.

No third-party dependencies are required. SwiftPM builds natively for Apple Silicon.

## Background monitoring architecture

The goal is that Zoom stays open in the background, on the same Desktop, possibly
completely covered by another window, while participants are admitted and the
foreground application is never disturbed.

**Accessibility is addressed by PID, never by focus.** Each scan builds a fresh
`AXUIElementCreateApplication(zoomPID)`, reads `AXWindows` from it, walks the
meeting hierarchy and re-acquires the Admit button. Nothing consults
`frontmostApplication`, `NSRunningApplication.isActive` (except to label the menu),
`AXFocusedWindow`, `AXMainWindow` or `kCGWindowIsOnscreen` before deciding whether
to scan. A covered background window on the current Space answers Accessibility
exactly like a frontmost one.

**No Accessibility reference outlives its scan.** The application element, the
window list, the subtree and the buttons are all created inside one scan and
dropped at the end of it, so a Zoom redraw can cost at most one scan.

**Permission truth.** `AXIsProcessTrusted()` and
`AXIsProcessTrustedWithOptions(prompt:false)` decide permission, and nothing else
does. The system-wide `kAXFocusedApplication` probe is kept for the diagnostic log
but never gates monitoring: it reads *the frontmost application*, so it returns
`cannotComplete` whenever that unrelated app is slow to build its Accessibility
tree (Chrome and other Electron-style apps do this routinely) and `noValue`
whenever nothing holds Accessibility focus. Treating those as a permission failure
is what previously produced a false **Permission granted — relaunch app** state
while another window covered Zoom.

**Transient failures retry instead of escalating.** A failed scan is classified as
`apiDisabled`, `invalidUIElement` or transient. Anything but `apiDisabled` waits
250–500 ms, re-acquires every reference and scans again, up to three attempts per
poll. Only if all attempts fail does the menu show *Zoom: Temporarily unavailable
/ Waiting Room: Retrying…*, and the next poll recovers on its own. The monitor is
never stopped for a Zoom-side failure.

**Same Space versus another Space.** Accessibility reachability is the signal.
While the meeting hierarchy answers, Zoom is same-Space and monitorable no matter
what covers it. `kCGWindowIsOnscreen` is never used on its own to decide a Space,
because a covered window on the current Space still reports `true`. Only when the
hierarchy stays absent across every retry *and* CoreGraphics still sees a meeting
window is the state reported as another Desktop.

**Hybrid observer plus polling.** An `AXObserver` registered on the Zoom process
(created/destroyed, window created, row count, selected rows, value and layout
changes) asks for an early scan, coalesced to at most one extra scan per 350 ms so
a chatty Zoom cannot spin the CPU. The 0.75 s polling loop keeps running and stays
authoritative, because Zoom's participant list is a custom control whose
notification fidelity cannot be relied on. Observer registration failure costs
latency, never correctness.

**The menu bar app never brings Zoom forward.** It contains no
`NSRunningApplication.activate`, `AXRaise`, `AXFrontmost`, unhide, Space change or
synthetic event code at all, and `Scripts/build-app.sh` fails the build if any is
introduced. The only activation code in the repository lives in the `auto-admit`
CLI behind the opt-in `--cross-space` flag.

## Automation (DEPI dashboard, recordings, Zoom Web, co-host)

These features were first built for the Windows app (branch `Windows_and_web`) and work the same way here. They are off until turned on in **Automation…** from the menu bar. Browser work is done by `automation/`, a small Node.js + Playwright helper the app runs as a child process; requests go over stdin, so passwords never appear in the process list.

**Requirements:** Node.js 20+ (`brew install node`) and Google Chrome (or run `npm run install-browser` in `automation/` once for Playwright's Chromium). `Scripts/build-app.sh` installs the helper's packages and bundles it into the app.

| Feature | Where | What it does |
| --- | --- | --- |
| Run Session | Automation → LMS | When a scheduled meeting linked to a group goes live, signs in to the DEPI dashboard and presses **Run Session** on that group's session, picked by the scheduled time. |
| Attendance upload | Automation → LMS | 90 minutes in, ticks Joined for every student the register marks **Present** and Not-joined for everyone else the dashboard lists. Every row is decided first; if one row cannot be ticked, nothing is submitted. |
| Late-joiner correction | Automation → LMS | 3 hours in, opens View details and flips only the rows that disagree, then reloads and confirms. |
| Recording link | Automation → LMS / Follow-ups | Finds the group's cloud recording in My Recordings (longest one inside the session's hours, checked against the link's own start time) and puts its share link on the session. A link already there is left alone. |
| Follow-up queue | Automation → Follow-ups | The steps above are written to `~/Library/Application Support/Zoom Auto Admit/LMS/follow-up.json`, retried every 15 minutes up to 8 times, and still run after a restart. |
| Recording API | Automation → Recording API | `POST /api/recordings/process` with `X-API-Key` attaches a Google Drive link n8n sends. Loopback only by default; use a tunnel to reach it. Same contract and status codes as `Windows/RECORDING-API.md`. |
| Excel timetable | Schedules → Import Excel… | Reads the DEPI `.xlsx` timetable and adds online sessions as one-time schedules; skips Physical, No Session, past dates and times already taken. Imported schedules stay disabled until enabled. |
| Excel roster | Schedules → Groups → Import Excel… | `Order` + `Name` (or `FullName`) headers, optional `StudentId`, `Aliases` (`|`-separated), `Email`. Formulas are refused. |
| Zoom Web meetings | Schedules → Zoom Accounts → Engine | **Web** always uses the browser, **Auto** uses the desktop app while it is free and the Zoom Web Client when it already holds a meeting, so classes can overlap. The web extension's own admit and attendance script runs inside the page; its attendance feeds the normal register. Sign in once per account with **Sign in to Zoom Web…**. |
| Co-host | Schedules → Groups → Co-host candidates | Every 20 seconds during a register, a listed person who has joined is made co-host through the row's *More options → Make co-host*, confirmed from Zoom's list. Nobody who is not listed is ever made co-host. |
| Open before meetings | Automation → Web & Co-host | A LaunchAgent opens the app (by bundle id) a few minutes before each meeting in the next two weeks, so a class starts even after the app was quit. |

The dashboard group is the group's **LMS group code**, or its name when that is empty. The dashboard sign-in and the API key are stored in the Keychain. **Rehearse only** opens everything and logs the decision without pressing anything that writes.

Run the helper's tests with `cd automation && npm test`.

## Scheduled meetings

A schedule opens Zoom, switches to a saved account, starts a specific meeting,
verifies that it really started, and only then hands over to the existing Auto
Admit monitor. Schedules live in a readable JSON file and survive relaunches.

### Account identity

Zoom's own application menu exposes everything the account layer needs, which is
what the implementation uses:

```
Zoom Workplace ▸ Switch account ▸ "Display Name(email@example.com)"   ✓ = signed in
```

Three properties of that menu shaped the design, all confirmed by live capture:

* It is an *application-level* Accessibility element, so it stays readable when
  every Zoom window has been moved to another Space and `AXWindows` is empty.
* Zoom builds the submenu eagerly, so saved accounts are enumerated without
  opening any menu or clicking a profile picture.
* `AXMenuItemMarkChar` is `"✓"` on the signed-in account, which is how the
  active account is read.

Accounts are matched on **email**, because display names collide — the captured
client has three different accounts all showing "eyouth coordinator". An
identifier without an `@` falls back to display-name matching, where a collision
aborts the workflow as ambiguous rather than picking one.

The `Sign out` submenu lists the *same account titles* as `Switch account`.
Selection is therefore scoped structurally to the `Switch account` submenu, the
live element is re-verified (role, title, Zoom's `menuItemDidClicked:`
identifier, enabled, `AXPress`) immediately before pressing, and a test asserts
that no account entry can ever originate from the sign-out branch.

### Starting the meeting

Meetings are started with Zoom's public `zoommtg` URL scheme, which the installed
client registers under `CFBundleURLSchemes`:

```
zoommtg://zoom.us/start?confno=<meeting id, digits only>
```

This was chosen over navigating Zoom's meeting list because the list lives inside
a window, and windows disappear from Accessibility whenever Zoom sits on another
Space — the exact state Zoom was found in during discovery. A meeting number is
also stable, unlike a row position. A schedule can instead choose *Personal
meeting*, which presses Zoom's own `Start meeting` menu entry.

Because meetings are addressed by number, there is no meeting list to be
ambiguous about; the ambiguity guard lives on accounts, where collisions are
real.

### Proving the meeting started

An `AXPress` returning success proves nothing, so the workflow waits for
Accessibility evidence of an actual meeting: a window titled `Zoom Meeting`, or a
meeting hierarchy. CoreGraphics is deliberately not accepted as evidence — this
app holds no Screen Recording permission, so every Zoom CG window name comes back
empty and the off-Space heuristic scores Zoom's ordinary main window exactly like
a meeting window.

That also gives meeting detection three answers rather than two: `active`,
`notActive`, and `unknown` when Zoom's hierarchy is unreachable. Before starting
anything the workflow refuses to disturb a call already in progress; if the state
is `unknown` it spends its one permitted foreground interruption bringing Zoom
forward and asks again, and stops entirely if it still cannot tell.

### The pre-join preview

Zoom usually shows a preview window before a meeting actually begins, offering
Audio, Video and Start. The workflow handles it rather than sitting in front of
it waiting — which is exactly what it did before this was added:

```
state=verifyingMeeting — Start requested via Zoom menu: Start meeting
[90 seconds later] state=failed — The meeting did not start
```

Zoom labels these controls with the **action they perform, not the state they are
in**: "Mute" means the microphone is currently live, "Unmute" means it is already
muted, "Start Video" means the camera is off, "Stop Video" means it is on.
Reading that backwards would switch a microphone *on* moments before a meeting,
so the mapping is explicit, tested in both directions, and anything outside the
known vocabulary is reported as `unknown`.

For each device the workflow reads the live state first and only acts when it
must:

```
Pre-join preview detected
Microphone state: ON
Turning microphone OFF
Microphone state verified: OFF
Camera state: ON
Turning camera OFF
Camera state verified: OFF
Start button found
Pressing Start
Meeting verified (ax-meeting-window-title)
Auto Admit active
```

If a device is already off, the log reads `Microphone state: OFF — no action` and
nothing is pressed. Every one of these aborts the workflow *before* Start rather
than guessing:

* the control cannot be found,
* several controls match the same device,
* the state cannot be read confidently,
* the control was pressed but the device did not actually turn off,
* the Start button cannot be identified, or more than one candidate matches.

When any of those happen the live preview hierarchy is written to
`~/Library/Logs/Zoom Auto Admit/zoom-prejoin-snapshot.log`, so the vocabulary can
be extended from real data instead of guesswork. `Join Audio` is deliberately
classed as *off*: audio is not connected, nothing is transmitting, and pressing
it would turn audio on.

`Start` is matched exactly, never as a substring, so `Start Video` and
`Join with Computer Audio` can never be mistaken for it. Windows showing
in-meeting participant structure are never treated as a preview, so the
automation cannot press controls during a live call.

### Focus

The startup workflow may bring Zoom forward — opening menus and starting a
meeting need it. Auto Admit monitoring never does: once the meeting is verified
the existing background monitor takes over unchanged, and `Scripts/build-app.sh`
fails the build if a focus-stealing call appears anywhere outside the two
allowlisted startup-workflow files.

### Files

Schedules and account profiles:

```
~/Library/Application Support/Zoom Auto Admit/schedules.json
```

Workflow log:

```
~/Library/Logs/Zoom Auto Admit/scheduler.log
```

No password, passcode or token is ever stored; a profile holds only the email of
an account that is already signed in to Zoom.

### Capturing Zoom's hierarchy again

If a future Zoom build changes its UI, re-capture the live hierarchy from the
menu bar item **Capture Zoom UI Snapshot**, which writes:

```
~/Library/Logs/Zoom Auto Admit/zoom-ui-snapshot.log
```

The command-line inspector can do the same when its terminal is
Accessibility-trusted:

```
./run-inspect.sh --account-ui
./run-inspect.sh --meetings-ui
```

## Required macOS permission

Enable Accessibility for the program that actually executes the Swift code:

1. Open **System Settings → Privacy & Security → Accessibility**.
2. Unlock the pane if macOS asks for an administrator password.
3. Add and enable the terminal app you use, such as **Terminal**, **iTerm**, or **Visual Studio Code**, when running through `swift run`.
4. After building a stable binary, you can add and enable that binary directly instead. The release paths are printed by the build commands below.
5. Quit and relaunch the terminal or binary after changing permission if macOS does not immediately recognize it.

The inspector calls Apple's trusted-client check and may open the relevant pane. The utility does not need Full Disk Access.

### Automation / System Events

This implementation uses `AXUIElement` directly and does **not** use AppleScript or `System Events`, so it does not need **Privacy & Security → Automation → System Events** permission. If you later replace it with an AppleScript that sends UI events through System Events, macOS will ask you to allow the terminal/script host to control **System Events**; that is an additional Automation permission, not a substitute for Accessibility.

## Build

From this directory:

```sh
swift build -c release
```

The binaries will be under `.build/arm64-apple-macosx/release/` on Apple Silicon. If Swift chooses a different SDK path, use the exact path printed by:

```sh
swift build -c release --show-bin-path
```

## Inspect Zoom first

1. Start or join a Zoom meeting as Host.
2. Open the **Participants** panel. Enable Waiting Room and have the second test account/device waiting if you want to inspect a populated state.
3. Run:

```sh
./run-inspect.sh --max-depth 14 2>&1 | tee zoom-accessibility.txt
```

Or use the built binary:

```sh
"$(swift build -c release --show-bin-path)/inspect-zoom" --max-depth 14 | tee zoom-accessibility.txt
```

Look for `identifier="ZMHCTableItemType_WAITINGLIST"`, `identifier="ZMHCTableItemType_WAITINGLIST_Group"`, and an `AXButton` whose exact title or description is `Admit` or `Admit All`. The printed `path=` is the accessibility path, not a screen coordinate. A matched control is summarized under `[GUARDED CANDIDATE]`, including its evidence, participant name when exposed, title, description, and path.

If the inspector says Zoom was not found, start Zoom and retry. If it says no AXWindow elements or no marker appears, see Troubleshooting below.

## Run safely

Start with a dry run. It performs no click:

```sh
./run-auto-admit.sh --dry-run
```

Then run the real monitor:

```sh
./run-auto-admit.sh
```

The default interval is 0.75 seconds and is deliberately bounded to 0.5–1.0 seconds. To stop it, press **Ctrl-C** in that terminal. SIGTERM also stops it cleanly. `--once` is useful for a single validation pass:

```sh
./run-auto-admit.sh --dry-run --once
```

The monitor logs every candidate and every successful admit action with an ISO-8601 timestamp. For the observed participant-row hierarchy, dry run prints `Found Waiting Room participant: <name>` followed by `Would press Admit`. When no participant is waiting, it takes no action and stays quiet. It admits at most one individual participant per scan, then rebuilds the tree; this avoids pressing stale buttons while Zoom re-renders the list. `Admit All` is preferred whenever it is exposed in the same Waiting Room context.

## Cross-Space operation

On the tested macOS 27.0 system with Zoom Workplace (`us.zoom.xos`), direct off-Space AX traversal is not available. `CGWindowListCopyWindowInfo(.optionAll)` continues to enumerate the Zoom-owned `Zoom Meeting` window by PID and reports it as off-screen, but Zoom omits that window from its application `AXWindows` array. Because there is no AX window element, the utility cannot read its descendants or perform `AXRaise` on it in the background.

macOS has no public API that exposes Mission Control Space IDs or switches/restores an arbitrary Space by ID. Private CoreGraphics/WindowServer Space APIs are deliberately not used.

Cross-Space mode therefore uses a bounded public-API fallback:

1. Run the ordinary background AX scan first.
2. Use CoreGraphics metadata to verify a layer-0 Zoom Meeting window exists and is not on the current Space.
3. Capture the current frontmost application, its focused AX window, and its on-screen CoreGraphics window IDs.
4. Temporarily set the verified Zoom process's `AXFrontmost` attribute. If needed, request AppKit activation and temporarily unhide/unminimize the exact Zoom Meeting window.
5. Run the unchanged strict Waiting Room matcher and press only a guarded candidate.
6. Raise the previously focused window, restore its application, and restore Zoom's hidden/minimized state.

### CoreGraphics diagnostics and meeting-window learning

Dump every WindowServer record owned by the Zoom PID without filtering by title:

```sh
./run-inspect.sh --cg-windows
```

The output includes window number, owner PID/name, window name, layer, on-screen state, bounds, alpha, and sharing state. It also prints the exact list option:

```text
kCGWindowListOptionAll (rawValue=0); kCGWindowListOptionOnScreenOnly is NOT set
```

The implementation passes only `.optionAll` to `CGWindowListCopyWindowInfo`; it does not combine `.optionOnScreenOnly` or pre-filter records by `Zoom Meeting`.

For the strongest discovery, start the monitor while the Participants panel is accessible in the current Space. It identifies a meeting AX tree structurally through Zoom participant identifiers or the exact `Participants list` AX outline, correlates that AX window to a normal-layer CoreGraphics window by bounds/title, and remembers the CG window ID for the lifetime of the Zoom process. When the meeting moves to another Space and loses its AX tree or CG title, that learned ID remains primary evidence.

If the monitor starts after Zoom is already off-Space and no title is available, discovery can retain a lower-confidence candidate using only the verified Zoom PID, normal layer, nonzero meeting-sized bounds, nontransparent alpha, off-screen state, and exclusions for known non-meeting Zoom windows. This evidence permits only temporary Zoom exposure. It never authorizes a click; `WAITINGLIST`, exact Admit label, `AXButton`, enabled state, `AXPress`, and Breakout rejection remain mandatory.

To compare the same live meeting in both states:

```sh
./run-inspect.sh --cg-windows | tee cg-current-space.txt
# Move Zoom to the other Space, then:
./run-inspect.sh --cg-windows | tee cg-other-space.txt
```

Enable it explicitly:

```sh
./run-auto-admit.sh --cross-space
```

The first off-Space probe occurs immediately. With no candidate, probes back off from 15 to 30 to 60 seconds. This avoids activating Zoom every 0.75-second polling cycle. Change the initial interval, with a minimum of five seconds, using `--cross-space-interval`.

Important limitations:

- A brief visible Space transition or flicker may occur; public APIs cannot guarantee a completely background-only exposure.
- Exact Space restoration cannot be guaranteed if the prior app has no focused AX window or has windows assigned to every Space. The utility logs whether the prior on-screen window was verifiably restored.
- An off-Space full-screen window and a normal window on another Space both appear as an off-screen WindowServer window. Public APIs do not provide a reliable distinction.
- A minimized meeting is recognized when Zoom exposes its AX window and `AXMinimized`; a hidden app is recognized through `NSRunningApplication.isHidden`. An unexposed, off-screen window can only be classified as another Space or full-screen Space.
- macOS exposes no public, reliable “someone is waiting” signal while Zoom withholds the off-Space AX subtree. Cross-Space mode therefore uses infrequent probes. It is opt-in for this reason.
- `--dry-run --cross-space` never presses Admit, but it does exercise temporary exposure/restoration and can therefore produce the same brief Space transition.

For a one-shot diagnostic dry run:

```sh
./run-auto-admit.sh --cross-space --dry-run --once
```

Run the matcher tests with:

```sh
swift test
```

## Safe test procedure

Use a meeting that is not sensitive:

1. On the Host account, enable Waiting Room and open Participants.
2. Join from a second Zoom account or another device and remain in the Waiting Room.
3. Run `--dry-run --once` and confirm the log names an exact `Admit All` or `Admit` button and shows a Waiting Room context.
4. Stop the dry run, start the real monitor, and confirm the waiting test participant is admitted.
5. End the meeting and press Ctrl-C to stop the utility.

Do not test with real guests until the dry-run output and one controlled test behave as expected.

## Safety behavior

Before any possible press, the code:

- resolves a currently running Zoom process using only the known Zoom bundle identifiers;
- obtains only that process's AX windows;
- requires the control to be an enabled `AXButton` with an available `AXPress` action;
- accepts only exact normalized `Admit` / `Admit All` descriptions, or exact supported titles—never loose substring matching;
- primarily requires a structural `ZMHCTableItemType_WAITINGLIST` participant cell or `ZMHCTableItemType_WAITINGLIST_Group` marker;
- retains a tightly scoped accessible `Waiting Room` text check only for older Zoom trees that do not expose those identifiers;
- rejects a candidate whose ancestor path is marked Breakout/Break Out;
- rechecks that the same Zoom process still exists immediately before pressing.

If any condition is absent, the scan is a no-op. The Participants panel closing is therefore handled as an ordinary no-op rather than an exception.

## Troubleshooting: “Admit” cannot be found

Run the inspector while the relevant panel is open and, ideally, while one test participant is waiting:

```sh
./run-inspect.sh --max-depth 20 | tee zoom-accessibility.txt
```

Then provide the complete output (or at least the lines around the Participants panel, Waiting Room, and any Admit-like controls). The important fields are `AXRole`, `title=`, `description=`, `value=`, `actions=`, `path=`, and whether `WAITING-ROOM-MARKER` appears. Also include your Zoom version and whether you use the standard or new Zoom Workplace client.

Common causes:

- Accessibility is enabled for Terminal but the binary is being launched by another host, such as VS Code; enable that host too.
- The Participants panel is closed, collapsed, or rendered in a separate Zoom window; open it and inspect again.
- Zoom exposes a localized title or description; do not edit the allowlist blindly. Capture the inspection output first so the exact role/title/context can be reviewed.
- Zoom no longer exposes either the `WAITINGLIST` identifiers or a tightly scoped accessible Waiting Room marker. The safety rule intentionally refuses to click in that case.
- A Zoom update changed the AX hierarchy or stopped exposing the controls. The utility cannot safely compensate with coordinates or image matching.

## Notes

The process uses macOS Accessibility only. It does not disable Waiting Room, modify Zoom preferences, use undocumented APIs, or interact with Breakout Rooms.
