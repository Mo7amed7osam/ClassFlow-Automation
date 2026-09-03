# Dynamic group roster (Windows)

Open **Roster Management** in the Windows WPF application. Select a group to open its roster. Use New group, Save / Rename, and Delete for group management. Group IDs are stable; rename changes the display name only. There is no fixed group count or group-specific branching.

The module `ZoomAutoAdmit.Roster` has no dependencies on meeting automation, accounts, schedules, admission, or attendance collection. It does not match participants or calculate attendance.

## Storage and initial data

Groups are stored independently at `%LOCALAPPDATA%\ZoomAutoAdmit\Roster\groups.json`. Each group contains its ID, display name, UTC creation time, revision, and students. Each student contains a stable ID, parent group ID, mandatory positive numeric `order`, full name, aliases, and optional email.

Only when this file does not exist, the generic initializer reads the embedded data file `src/ZoomAutoAdmit.Roster/Data/initial-groups.json`. It seeds **CAI4_AIS4_S7 (27 students)** and **CAI4_AIS4_S8 (25 students)** in the supplied order and generates stable student IDs. These identifiers are roster data, not hardcoded application rules or account mappings. An existing groups file is never overwritten or reseeded. Deleting a group through the UI does not recreate it next launch.

The earlier standalone **Students** page and `students.json` remain available for legacy records. They are not automatically assigned to groups or deleted. Use **Roster Management** for the new group-based source of truth.

Writes use an exclusive file lock, temporary file, atomic replacement, and a single previous-version `groups.json.bak`. Stale edits are rejected with a refresh message. Invalid/corrupt documents and failed imports are not partially overwritten. The backup is the previous save, not permanent history. There is a 64 MB document safety limit.

## Ordering and duplicate handling

- Students are shown and persisted by numeric `order`, never alphabetically. Column sorting is disabled.
- New student defaults to the next order number. Edit order explicitly or use Move up / Move down (clear search first).
- Manual movement swaps existing order slots, preserving gaps. Deletion does not renumber remaining students.
- Group IDs and student IDs must be unique (case-insensitively); student IDs are unique across groups.
- Order numbers must be positive and unique within a group. The same order in different groups is valid.
- Repeated names and aliases are allowed; identity is the student ID, not name matching.
- Group/student search does not change saved order.

## Import into an open group

Use **Import CSV / Excel** after creating/opening a group. Minimal UTF-8 CSV:

```csv
Order,Name
1,Ahmed Mohamed
2,Mohamed Ali
```

Excel `.xlsx` uses the same headers in its first worksheet. Required headers are `Order` and `Name` (`FullName` is also accepted). Optional columns are `StudentId`, `GroupId`, `Aliases`, and `Email`; aliases are separated by `|`. Omitted IDs are generated; a supplied GroupId must match the selected group. Input rows may be out of order: their explicit numeric order is retained.

Imports are add-only and all-or-nothing. Duplicate IDs or order numbers, including collisions with existing students, reject the complete import. Re-import is not an update. Formulas, legacy `.xls`, and files above the importer safety limits are rejected. No Excel installation is required.

## Validation

Tests cover group/student CRUD, dynamic groups, stable IDs, numeric order and manual movement, duplicates, stale updates, concurrent writes, round-trip persistence, corrupt-file preservation, initial data, CSV/XLSX imports, and the compiled WPF view's bindings and disabled alphabetical sorting.

```powershell
dotnet build Windows/ZoomAutoAdmit.Windows.sln
dotnet test Windows/ZoomAutoAdmit.Windows.sln --no-build -m:1
```
