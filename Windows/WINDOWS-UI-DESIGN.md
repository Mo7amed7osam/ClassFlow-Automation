# Windows UI visual layer

## References and scope

The latest supplied archive `zoom-auto-admit-ai-standalone-pages.zip` is the current palette/component reference, superseding the earlier visual pass. Its HTML was inspected as reference content, not executed as the application or treated as operational instructions. Demo users, meeting counts, provider statuses, AI percentages, and absence results are not production data.

This implementation is native WPF/XAML; it does not introduce React, a WebView, Three.js, external fonts or background model requests. Existing runtime adapters, account switching, meeting lifecycle, scheduler, attendance collection, roster storage, and matching engine are unchanged.

## Theme and components

- `Themes/Dark.xaml`: `#05080F` canvas, `#E8EDF7` text, blue/violet CTA gradient (`#3B7CF6 → #6D5CF6 → #8B5CF6`).
- `Themes/Light.xaml`: `#E6EFFB` canvas, `#12233D` text, white cards and blue CTA gradient (`#4B8BF4 → #427DEB → #3A6FE6`). Sidebar is inset and rounded.
- `Themes/Controls.xaml`: reusable rounded cards, buttons, editable inputs, dropdowns, selection states, tables and slim scrollbars.
- `MainWindow.xaml`: resizable window chrome, grouped sidebar, actual page search, quick navigation and theme toggle. The theme toggle and Settings appearance buttons affect this window only; no runtime settings are changed.
- `Views/EngineGlyph.xaml`: native scalable geometric illustration, not an indicator of AI connectivity.

The original screen data/commands remain available. Sidebar navigation changes presentation, not service allocation or execution. Original numeric page indexes 0–6 are preserved for existing commands; new destinations are appended.

## Page mapping

| Page | Data / actions |
| --- | --- |
| Overview | Existing session count/status, account/group counts, runtime logs, quick navigation. Unknown telemetry shows an em dash. |
| Start Meeting | Original account, meeting URL, engine preference and Start command. |
| Meetings | Original active-session source and per-session Stop command, with rounded session cards. |
| Accounts | Original storage/editor and Switch Account command; cards say Saved, not falsely Connected. |
| Schedules | Original list/editor, save/delete commands and runtime feedback. No scheduler rules changed. |
| Groups & Students | Existing ordered group roster, import, editing, search and movement commands. |
| Legacy students | Earlier standalone student records retained. |
| Logs | Original runtime log stream and Clear command. |
| Attendance | Existing collector snapshot history, raw names, explicit session/group selection, matching results and review. No absence calculation. |
| AI Matching | Secure key entry, editable model selection, real response validation, existing rule/alias/AI matching and review. |
| AI Engine | Existing model/validation status, TestSavedCommand, navigation to AI setup and logs. Usage reporting and weight editing are explicitly disabled, not new business features. |
| Waiting Room | Active monitors, session-scoped verified admission events and real runtime diagnostics. No reconstructed pending queue or extra Admit logic. |
| Settings | Working light/dark appearance controls, Ghost Cursor toggle, storage information and AI configuration navigation. |

This is a native interpretation of the reference rather than a pixel-identical image reproduction. No sample data is shipped into account/roster storage. See `CONNECTED-UI-VALIDATION.md` for the current data connections and validation boundaries.

## Validation and visual previews

`RedesignViewTests` opens the real compiled MainWindow on an STA thread with isolated fake services that cannot launch meetings, switch accounts, write production configuration, start scheduling, or call external AI. It traverses all thirteen pages in both themes, checks exact canvas colors, binding health, preserved command targets, disabled unsupported tools, page search/navigation, dropdown opening and minimum-size rendering. It also exercises the real key-entry click handlers against a fake AI service, including success and invalid-key responses.

To generate PNG previews during this test:

```powershell
$env:ZOOM_UI_PREVIEW_DIRECTORY = Join-Path $PWD 'Windows\TestResults\StandaloneUi'
dotnet test Windows/tests/ZoomAutoAdmit.WindowsUI.Tests -c Release -p:BaseOutputPath=bin/Connected/ --filter FullyQualifiedName~RedesignViewTests
```

Previews use a clearly labeled synthetic account/group and an idle runtime. They are UI render evidence, not proof of a live Zoom session. `App.xaml.cs` startup/shutdown handling is unchanged. Full production EXE startup is intentionally not used for visual regression tests because it initializes real runtime/scheduler services.

### Relocated workspace validation — 2026-08-31

- Repository: `D:\Zoom Admite\zoom-auto-admit`.
- Release build with `-p:BaseOutputPath=bin/Connected/`: 0 warnings, 0 errors.
- Full solution: 544 passed, 0 failed, 0 skipped across 8 test projects.
- UI suite: 63 passed; all 13 compiled pages rendered in both themes, plus minimum-size and Ghost Cursor checks.
- Corrected header description clipping and shortened the Overview AI setup label for the minimum window size.
- All 152 non-UI source files retained their pre-validation hashes; existing uncommitted changes were preserved.
- `git diff --check` passed. No commit or push.
- EXE: `Windows/src/ZoomAutoAdmit.WindowsUI/bin/Connected/Release/net8.0-windows10.0.19041.0/ZoomAutoAdmit.WindowsUI.exe`.
- Test reports: `Windows/TestResults/StandaloneValidation`; render previews: `Windows/TestResults/StandaloneUi`.
