# Student roster (legacy standalone records)

For the new group-based source of truth, use **Roster Management**; see
[GROUP-ROSTER.md](GROUP-ROSTER.md). The Windows **Students** tab preserves earlier
ungrouped records independently. No matching, absence calculation, reporting or attendance mutation is
implemented. The module does not reference Auto Admit, Attendance, account storage,
scheduling, or meeting lifecycle components. The WPF UI calls its service directly
using MVVM; no business logic lives in the view.

## UI

- **New** creates a draft with a generated student ID; an institutional ID can be
  entered instead before the first save.
- Select a saved student to edit their name, aliases (one per line), or optional
  email. Saved student IDs cannot be changed.
- **Delete** requires confirmation; it affects only the roster.
- Search filters by student ID, full name, aliases, or email. This is ordinary text
  search, not attendance matching.
- Refresh reloads local storage. Status messages explain successful operations,
  invalid data, duplicate IDs, stale edits, locked storage and import errors.
- Students is appended after Logs so existing tab indexes/navigation are preserved.

## Model and storage

Default file: `%LOCALAPPDATA%\ZoomAutoAdmit\Roster\students.json`

```json
{
  "schemaVersion": 1,
  "students": [
    {
      "studentId": "ST-001",
      "fullName": "Example Student",
      "aliases": ["Example S", "طالب تجريبي"],
      "email": null
    }
  ]
}
```

IDs are unique case-insensitively; names, aliases and emails may overlap across
students. No automatic student merges occur. Values are trimmed, Unicode is
preserved, and blank/invalid mandatory fields are rejected. ID/name/alias lengths
and optional email syntax are validated. Passwords are neither requested nor stored.

The store uses a cross-process exclusive file lock for each read-modify-write.
Writes use a unique temporary file and atomic replacement; `students.json.bak`
contains the immediately previous version (not an unlimited version history).
Stale edits/deletions are rejected rather than overwriting another window's changes.
Corrupt JSON, unsupported schemas and duplicate stored IDs fail without overwriting
the original file. Cancelled operations clean up temporary files. Restore a backup
manually only after closing writers and preserving a copy of the current file.

The `.lock` file may remain on disk; only its open file handle signifies a lock.
Do not delete it while an application holds it. Locks expire automatically when the
process exits. The store waits up to ten seconds for other writers; cancellation is
supported. Network-share/distributed locking is outside this local-store contract.

Names and emails are personal data. Files inherit the user's local directory ACLs;
this module provides no additional encryption, synchronization or retention policy.
Logs contain operation names/counts, not full student records.

## CSV and Excel import

Use **Import CSV / Excel** in Students. CSV must be comma-delimited UTF-8 (BOM,
quoted commas and escaped quotes are supported). Excel `.xlsx` reads the first
worksheet using standard OOXML shared/inline strings; Excel installation is not
required. Legacy `.xls`, macro workbooks, password-protected files and formulas
are not supported. Paste formula results as values before import.

Required headers: `studentId`, `fullName`.
Optional: `aliases`, `email`. Header case, spaces, underscores and hyphens are
ignored. Additional columns are ignored. Separate imported aliases with `|`.

```csv
studentId,fullName,aliases,email
ST-001,Example Student,Example S|طالب تجريبي,
ST-002,"Surname, Given",Given S,student@example.com
```

In Excel, store student IDs as **Text** to preserve leading zeros and long numeric
identifiers. Numeric cell values are read as stored, not using Excel display-format
masks; no number/date/formula formatting or recalculation is performed. Names and
IDs spanning multiple lines are invalid. Save only the intended students in the first
sheet before import; do not use hidden rows/sheets as an import-exclusion mechanism.

Import is **add-only and all-or-nothing**: duplicate IDs (within the file or already
stored) and any invalid row reject the entire batch. It never silently updates or
merges existing students. Use the editor for deliberate changes.

Limits: 20 MB import file, 64 MB expanded workbook/current roster, 2,000 ZIP entries,
100,000 imported students. External workbook relationships and XML DTDs are rejected;
macros/formulas are never executed. Stored files must be local, not downloaded into
the roster path as a substitute for validation.

## Verification

Roster tests cover CRUD, duplicates, optimistic conflicts, concurrent writers,
corrupt schemas, cancellation, CSV/XLSX parsing, atomic import rejection and logs.
UI tests cover editing, search, deletion confirmation, import feedback, and loading
the compiled Students view with bindings on an STA thread. No Zoom meeting or live
scheduler is started by these tests.

```powershell
dotnet build Windows/ZoomAutoAdmit.Windows.sln
dotnet test Windows/ZoomAutoAdmit.Windows.sln
```
