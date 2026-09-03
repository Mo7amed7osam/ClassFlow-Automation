# Connected Windows UI and real model validation

## What changed

- Both AI test buttons pass the populated PasswordBox to the same test operation. When it is empty, the securely stored key is used. Previously **Test saved key** ignored the typed field, producing “Enter an API key first” when no credential existed.
- The editable model dropdown preserves the selected ID. A real POST to the fixed OpenAI Responses endpoint uses that model and a synthetic name pair, with `store: false` and a strict JSON schema. The key is saved only after a completed, schema-valid answer; HTTP 200 alone is not success. A semantic confidence score is not used to decide whether a credential is valid.
- Invalid credentials, permission errors, model access errors, quota/rate limits, server/network failures and incomplete/refused/malformed responses have distinct feedback. Raw provider error messages are never displayed or logged because they can echo a secret. Only HTTP status and allowlisted error codes cross the adapter.
- Keys remain in Windows Credential Manager. Editing a key or model invalidates the previous ready state. Failed tests do not overwrite a previous saved key.
- Attendance reads the collector's existing per-session JSON files. It shows timestamp, account, engine, session ID, snapshot reason, completeness and raw names (including duplicates). Refresh preserves selection/results; another session writing a snapshot does not change the selected session.
- Selecting a snapshot and roster group allows the existing rule/alias/AI pipeline to run on that snapshot only. External matching requires a tested key and explicit consent. Results preserve roster order; low-confidence cases remain in the review list. There is no absence calculation.
- Waiting Room displays real active monitors, session-scoped lifecycle/admission events, and existing detection/hover/click diagnostics. This is an event monitor, **not** a reconstructed live participant queue. No global log-line name matching, fabricated pending count or manual Admit action was added. The dashboard metric is explicitly **Verified admissions**, not “users waiting”.
- Overview uses real matching confidence, session state and recent admission events. Logs include AI setup, attendance, matching and admission diagnostics.

## Integration boundaries

`AttendanceHistoryReader` is a read-only, bounded, cached presentation adapter for `JsonAttendanceSnapshotStore`. `AttendanceViewModel` selects and matches data without changing the collector. It does not mix snapshots from different sessions or automatically infer a roster from an account label. The user selects the roster explicitly. Up to 2,000 recent snapshots are displayed; unreadable/oversized files are counted and reported.

`MeetingActivityFeed` subscribes to the existing `MeetingLifecycleEvents` on the runtime instance and retains up to 200 events. It preserves session/account/engine identity. No Auto Admit, OCR, accessibility, account switch, scheduler rule or meeting state-machine changes are needed. The existing `WindowsUiService` exposes the feed and forwards manual attendance capture to the existing bridge.

UI refresh runs every five seconds and is stopped at disposal. Attendance history refreshes while its page is open; model requests are explicit, never started by the refresh timer. Current matching results are in memory, not a new reporting/persistence system.

## Tests / evidence

Final validation: build succeeded with **0 warnings / 0 errors**; **544 tests passed** (Core 185, UIAutomation 64, Web 45, WindowsRuntime 78, WindowsUI 63, Attendance 24, Roster 39, AttendanceMatching 46). `git diff --check` passed. No commit or push was performed.

- Real HTTP adapter with mock responses: 401, 403, 404, quota/rate-limit 429, 400, 500, malformed/empty/incomplete/refused answers and timeouts; no invalid result is saved as ready.
- Real connected matching service: exact-name rules make no AI request; an uncertain name calls the selected model; the next run reuses its approved alias. Provider failures appear as safe diagnostics and review, not absence.
- Actual collector JSON round-trip through the reader, duplicate preservation, partial/corrupt snapshots, per-session selection, refresh stability and independent Desktop/Web event ownership.
- Compiled WPF MainWindow is opened in an STA test host with isolated fake runtime/credentials/AI. Both themes and all pages are rendered; the actual PasswordBox/buttons are exercised, including invalid-key feedback and snapshot-to-roster matching. Preview fixtures are explicitly synthetic, not a live Zoom session.
- Visual inspection caught the missing `PART_EditableTextBox` in the custom ComboBox template. It now displays the model ID and supports both typing a custom ID and selecting a preset; UI tests exercise both operations in both themes.
- An explicit live diagnostic against OpenAI using a deliberately invalid synthetic key returned `invalid_api_key`, displayed as **Not valid**. The production AI credential target had **no saved key** during validation. A real user's successful key/model response therefore still needs testing in the new UI; no key was extracted from the old open PasswordBox or written to a diagnostic file.

The user's Release process was running and locking its DLLs. Validation output uses a separate directory; that process and its sessions are left alone:

```powershell
dotnet build Windows/ZoomAutoAdmit.Windows.sln -c Release -p:BaseOutputPath=bin/Connected/
dotnet test Windows/ZoomAutoAdmit.Windows.sln -c Release -p:BaseOutputPath=bin/Connected/ --no-build -m:1
```

New executable: `Windows/src/ZoomAutoAdmit.WindowsUI/bin/Connected/Release/net8.0-windows10.0.19041.0/ZoomAutoAdmit.WindowsUI.exe`.

## Usage

1. Open the new executable when ready; avoid leaving two production scheduler instances running. Existing sessions are not stopped by this change.
2. AI Matching → paste key into the masked field → choose/type a model ID → **Test connection**. The test may incur normal API charges. Expect “Done — Valid” only after the model answers correctly formatted JSON, otherwise read the specific error.
3. Attendance → choose a captured snapshot → choose its roster → permit external matching if appropriate → **Match snapshot**. Raw snapshots remain separate from matching results; uncertainty never means absence.
4. Waiting Room → inspect active monitors and verified events. Existing engines continue to own admission actions.

OpenAI Docs informed the error classification and structured-response validation: [error codes](https://developers.openai.com/api/docs/guides/error-codes), [structured outputs](https://developers.openai.com/api/docs/guides/structured-outputs), [GPT-5.4 mini](https://developers.openai.com/api/docs/models/gpt-5.4-mini).
