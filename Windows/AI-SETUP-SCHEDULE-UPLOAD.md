# Windows AI setup, timetable upload and cursor effect

## AI Matching

The existing OpenAI Responses name matcher is reused, not rewritten. On **AI Matching**, enter your own OpenAI API key in the masked box and a supported model ID, then click **Test & save securely**. The default ID is `gpt-4.1-mini`; access depends on your OpenAI account. A small, billable synthetic comparison validates authentication, model access and the existing structured response schema. Only a successful response followed by a successful credential write produces **Done**. A failed test does not overwrite a previously saved key.

The key and model are kept together in a Generic credential named `ZoomAutoAdmit/AI/OpenAI` in Windows Credential Manager, scoped to the signed-in Windows user. They are not written to app JSON or logs. The password input is cleared after submission/navigation. Like any desktop BYOK app, the key must briefly exist in process memory to authenticate; this does not protect against malware running as the same Windows user. Never distribute a shared developer key with the application.

After restart, **Test saved key** verifies access again. **Remove saved key** deletes only this app's AI credential. **Cancel operation** cancels pending work. To match, select a group and paste observed names (one per line), then explicitly permit external matching. Uncertain names and candidate roster names are sent to OpenAI; this may incur API charges. Results and review items are displayed in roster order. Existing alias persistence/rules are reused. There is no automatic absence calculation, no autonomous AI call at startup, and no new subscription to attendance/meeting events.

Implementation references: [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs), [Windows CredWrite](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew).

## Schedule upload

**Schedules → Upload Excel** supports the supplied DEPI `.xlsx` format, including metadata above the actual header and the `Session No.`, `Date`, `Session Type`, `Session Content`, `Slot` columns. It reads the first worksheet only. Numeric Excel dates (1900/1904) and ISO/day-first date text are supported. Formula/error cells and external worksheet links are rejected. Compressed size, expanded size and row counts are bounded; macros/formulas are never executed.

Preview shows every row with exclusion reasons. Select the import account and meeting URL independently of the schedule editor. An exact account-ID match to Round Code may preselect the account; display names and menu indexes are never used. The account's saved meeting URL is filled in but remains editable. **Confirm import** adds online rows only, preserving exact local dates and start times. It skips Physical, No Session, invalid rows, past dates and account/date/time duplicates. It does not overwrite existing entries. Import is disabled by default; opt into **Enable imported meetings** to schedule actual starts. Partial persistence/Windows task failures are reported and are not labelled Done.

The runtime schedule model adds optional `OccurrenceDate`. Null keeps the existing weekly behavior. Exact dates use a one-time Windows task and a matching in-app due-date guard; they are never converted to weekly recurrences. Task registration does not require a stored Windows password, but requires a logged-in interactive user. Times follow the PC's local timezone. The Slot end time is informational, not an automatic meeting-stop instruction. The prior cross-process coordination limitations between Windows Task Scheduler and the in-app scheduler are not redesigned by this feature.

The supplied S8 file has 66 entries: 55 online, 9 physical and 2 No Session. Testing the reader does not import them into production storage or register real tasks.

## Ghost Cursor

The application remains native WPF. `GhostCursorOverlay` is a WPF interpretation of the supplied React Bits effect, not an embedded React/Three.js renderer or a pixel-identical shader port. It uses the reference violet tint, a bounded 50-point soft trail, inertia, 1-second idle delay and 1.5-second fade. It follows pointer events only inside the app window and never moves the real cursor. `IsHitTestVisible=false` keeps buttons usable. The rendering timer stops on fade, window deactivation, unload and disable. Its initial enabled state respects Windows animation preferences. Toggle it explicitly in **Settings** to override that default; the toggle currently lasts for the window's lifetime.

## Verification

Tests use fake AI responses and incapable runtime services. They exercise the real compiled WPF window and its PasswordBox/click path, bindings and navigation in both themes. A separate disposable Credential Manager target is used for a real Windows secure-store round trip. No production credentials, Zoom sessions, Windows scheduled tasks or account files are modified by tests. A live OpenAI verification still requires the user to enter their key and press the test button.

To include the provided file in the read-only integration test, set `ZOOM_SCHEDULE_TEMPLATE` to its path. `ZOOM_UI_PREVIEW_DIRECTORY` optionally renders test window PNGs. Without the environment variable, the importer test uses an isolated synthetic workbook fixture.
