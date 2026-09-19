# Running this repository on a cloud box

A session that opens this repository on a Linux machine has no Zoom, no Windows and no Mac. This is
what it can do, and how to get there in two commands.

```bash
./Scripts/cloud-setup.sh     # PostgreSQL, the Python venv, npm ci, dotnet restore
./Scripts/cloud-verify.sh    # every suite that can run here, with an honest summary
```

`cloud-verify.sh` prints PASSED / FAILED / SKIPPED per check and exits non-zero only on a failure.
**Read the SKIPPED lines before calling anything green.** The most misleading case is the backend
suite: without PostgreSQL on the box, pytest skips every test and still exits 0. The script detects
that and reports SKIPPED rather than PASSED, which is why it exists.

## Which branch

Work on **`Windows_and_web`**. `main` is the GitHub default branch but has diverged: from the common
ancestor `415593a` it carries 39 commits this branch does not have, while this branch carries 51 that
`main` does not. `main` holds the macOS app, `web-extension/` and `automation/`; this branch holds
`Windows/`, `Backend/` and `Dashboard/`. Starting from `main` means not seeing the work below.

## What runs here

| Check | Command |
|---|---|
| Backend tests | `cd Backend && .venv/bin/python -m pytest` |
| Dashboard tests | `cd Dashboard && npm test` |
| Dashboard types | `cd Dashboard && npm run typecheck` |
| Dashboard build | `cd Dashboard && npm run build` |
| Roster tests | `dotnet test Windows/tests/ZoomAutoAdmit.Roster.Tests` |
| Attendance matching tests | `dotnet test Windows/tests/ZoomAutoAdmit.AttendanceMatching.Tests` |

## What does not run here, at all

The Windows agent and the WPF window (21 projects target `net8.0-windows10.0.19041.0`), the macOS
app, waiting-room admission, Zoom UI Automation, and Playwright against live Zoom. A change to any of
them can be written and reasoned about here but **cannot be verified** here. Say so when reporting;
do not let a green backend suite imply the Windows side was exercised.

Because of this, new platform-independent logic belongs in `ZoomAutoAdmit.Roster` or
`ZoomAutoAdmit.AttendanceMatching` — the only two projects whose tests run off Windows.

## Running the backend for real

```bash
initdb -D /tmp/pg -U postgres -A trust
pg_ctl -D /tmp/pg -o "-p 5439" -l /tmp/pg.log start
cd Backend && set -a && . ./cloud.env && set +a
.venv/bin/python -m central_backend.cli migrate
.venv/bin/python -m central_backend.cli create-admin --username admin
.venv/bin/python -m uvicorn central_backend.main:app --port 8000
```

[Backend/cloud.env](Backend/cloud.env) holds development values only, and every name in it is
documented with its default in [Backend/README.md](Backend/README.md#settings-environment). With
`CENTRAL_ENVIRONMENT=development` plain http works and `/docs` is served; under `production` every
non-https request is refused with `403` and nothing will answer on a throwaway box.

The client API key that matches the hash in that file is
`zaak_cloud-dev-key-not-a-real-secret-0000000` — send it as `X-API-Key`. It is a development key with
no meaning anywhere else.

## The two files in the repository root that should not stay

`api.env` and `api_key.env` are the operator's own local scrollback, committed deliberately so a
cloud session has everything without waiting on a person. They contain development values — a
`127.0.0.1` database with no password, `CENTRAL_ENVIRONMENT=development`, two `zaak_` keys generated
on that PC, and a `CENTRAL_ADMIN_USERS` hash for a setting the backend has since retired and now
ignores with a warning.

They are in git history from the commit that added them. Removing the files later hides them from the
latest tree only; the values stay recoverable with `git log -p`. So when the cloud work is done, the
step that actually ends their life is generating new keys, not deleting the files:

```bash
cd Backend && .venv/bin/python -m central_backend.cli new-api-key
```

Put the new hash in `CENTRAL_CLIENT_API_KEY_HASHES`, hand the new key to whoever needs it, and the
committed ones stop meaning anything. To drop the files from the tree as well:

```bash
git rm --cached api.env api_key.env
printf 'api.env\napi_key.env\n' >> .gitignore
git commit -m "The local env files leave the repository"
```

And to erase them from history, which rewrites every commit since and needs a force-push that anyone
else with a clone must be told about:

```bash
git filter-repo --invert-paths --path api.env --path api_key.env
git push --force origin Windows_and_web
```

Rotating the keys is the part that matters. The history rewrite is optional tidying.
