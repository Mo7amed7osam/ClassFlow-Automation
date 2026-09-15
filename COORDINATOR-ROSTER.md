# Coordinator roster management

This extends the existing Students page and `/api/v1/dashboard/students` APIs. The existing Viewer/current_viewer access control and Students table remain the source of truth. No schema change or migration is needed; existing migrations remain unchanged.

## Use

Open My groups, then Students for an assigned group. Use Import roster to upload CSV/TSV or paste cells copied from Excel. Supported headers include Full name/Name, Email, Group/Group name, and optional ID and Order. A missing group column uses the selected group. A populated group cell must match that group. Import each group separately.

Preview displays new and updated names, unchanged counts, invalid rows and duplicates. Confirm with Import. Invalid and duplicate rows are skipped; other valid rows are saved. Re-import updates existing students, preserving their database IDs, aliases and attendance relationships. Students absent from an uploaded file are retained. Remove a student using Edit and the existing active checkbox; removed students can be shown and restored. Re-import also restores matching removed students.

CSV files and Excel clipboard paste are supported; native .xlsx workbook upload is not implemented. Email remains optional for compatibility with existing name-only rosters, but a supplied malformed email makes its row invalid.

## Authorization and compatibility

Admins retain access to all groups. Coordinators can create, edit, remove and import only within their currently assigned, unarchived groups. Preview and confirmation independently check current authorization. Assignment removal takes effect on the next request. The server validates access independently of the dashboard.

The existing import request and response fields remain supported. `invalidRows` and `duplicateRows` are additive response fields; `skipped` still includes all diagnostics. Conflicting identifiers cannot silently overwrite a different student. Preview simulates the same changes as import and rolls its database transaction back.

Attendance upload, matching, agent authentication, WebSockets, jobs, Zoom automation and recording synchronization are unchanged. Live external integrations require a real Zoom/n8n smoke test; the automated tests use simulated external services.

## Files changed for this extension

- Backend/central_backend/roster_import.py
- Backend/central_backend/attendance.py
- Backend/tests/test_attendance_api.py
- Dashboard/src/api/attendance.ts
- Dashboard/src/pages/StudentsPage.tsx
- Dashboard/src/pages/GroupsPage.tsx
- Dashboard/src/pages/attendance.test.tsx
- Dashboard/src/pages/roles.test.tsx
- COORDINATOR-ROSTER.md

No commit was made. Other pre-existing workspace changes were preserved.

## Verification results

- Backend: full existing suite passed (321 tests); final roster/attendance suite passed (74 tests, including nine added cases). Combined coverage is 330 distinct tests. This covers device registration, WebSocket connections/reconnections, job dispatch, n8n recording sync, attendance matching, admin access and coordinator scoping.
- Dashboard: full suite passed (63 tests) with one fork worker; the final changed page suites passed (33 tests), including the additional group navigation case. Combined coverage is 64 distinct tests. Production build and TypeScript checking passed.
- Windows solution: 923 tests passed, one skipped. Includes Core, Roster, UIAutomation, AttendanceMatching, Attendance, WebAutomation, CentralAgent, WindowsRuntime and WindowsUI.
- Remaining validation: live Zoom/agent/n8n smoke test was not performed. The backend full run emitted SQLAlchemy connection-cleanup warnings in agent/job tests; no tests failed. Windows builds emitted compiler warnings. Initial Vitest default worker startup timed out; rerunning with `--maxWorkers=1 --pool=forks` passed.
