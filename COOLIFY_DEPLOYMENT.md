# Deploying ClassFlow Automation on Coolify

For whoever sets up the VPS. It assumes Coolify is already running on it and that you can add a
resource; it changes nothing about Coolify itself and touches no other application on the machine.

**Read §0 first.** Part of this system is built and part is not, and deploying it without knowing
which is which will waste your afternoon.

---

## 0. What you are deploying, honestly

| | State |
|---|---|
| Backend (FastAPI) + dashboard | Built and tested. 410 tests pass against a real PostgreSQL |
| PostgreSQL, volumes, migrations | Built. Migration chain `0001` → `0013`, single head |
| Cloud worker: settings, preflight, credentials, enrolment | Built. Runs, checks a machine, reports honestly |
| Cloud worker: the class stages | **Partly.** The three LMS stages are built and unit tested; the Zoom stages are not, and the worker does not claim to do them. None has run against the real LMS |
| Dockerfiles and compose | **Built and run.** Both images build; the stack comes up, migrates and answers. See §7 |

The backend now knows ten job types: `recording.process` as before, and nine stages of a class.
Each is routed by the capability a device reports, so a recording-only agent is never handed one.

The worker runs three of them — `lms.run_session`, `lms.attendance` and `lms.complete` — on the same
`LmsSessionRunner` the Windows app drives, under the account the class names. It reports only the
`lms` capability, so the six Zoom stages (opening a meeting, admitting, snapshots, ending, and
Zoom's own report afterwards) are never given to it: those jobs stay queued, which is visible, rather
than being taken and failed, which reads as a class that went wrong.

A worker off Windows has no Credential Manager, so a class's LMS sign-in comes from the server:
`POST /api/v1/agent/jobs/{jobId}/lms-secret`, with the device's own token. What keeps that from
being a standing key to every coordinator's account is that it is tied to the work — the server
answers only for a job **that device** holds and has not finished, only for the account that job's
payload names, and only while that coordinator is turned on. Turning one off closes their sign-in
again at once, even to a worker already holding one of their jobs. Every read is written to
`admin_audit_log` under the device's name, and the password never reaches a log. The worker keeps
it in memory for the one stage and asks again after a restart.

`FEATURE_PARITY.md` has the full inventory.

So: **this deployment gets the backend, the dashboard and a worker that is ready to be given work.**
It does not yet run a class by itself. `ZoomAutoAdmit.CloudWorker/Program.cs` says the same thing and
deliberately does not connect, rather than registering a worker that looks healthy and does nothing.

What is worth doing now is everything in §1–§6: the stack up, the domain working, an admin signed
in, the worker enrolled and its preflight green. That is the foundation, and it is the part that
needs a real machine to validate.

---

## 1. Before you start

- A VPS with Coolify, with room for PostgreSQL, a Python service and a Chromium container.
  **Capacity has not been measured.** Each concurrent class is its own Chromium; budget roughly
  400–700 MB per class plus the backend and the database, and start with
  `ZAA_MAX_CONCURRENT_SESSIONS=2` until you have measured this machine.
- A domain pointed at the VPS. Coolify gets the certificate.
- The repository: `https://github.com/Mo7amed7osam/ClassFlow-Automation`, branch **`Windows_and_web`**.
  Not `main` — the two have diverged and `main` does not contain the backend or the dashboard.

Never put a password, key or token in a chat message, an issue or a commit. Everything secret goes
into Coolify's own environment editor, which is what §2 is about.

---

## 2. The settings

Copy the names from [`deploy/.env.example`](deploy/.env.example) into Coolify's Environment
Variables for the resource. Three you generate yourself:

```bash
# The database password
openssl rand -base64 24

# The key that encrypts stored LMS passwords. 32 random bytes, base64.
python -c "import base64,secrets;print(base64.b64encode(secrets.token_bytes(32)).decode())"

# An API key for n8n. Prints the key once and the hash to store; the key is never kept anywhere.
python -m central_backend.cli new-api-key
```

`CENTRAL_CLIENT_API_KEY_HASHES` takes the **hash**, never the key. Give the key itself to n8n.

**`CENTRAL_SECRETS_KEY` deserves its own sentence.** It is not a password you can reset. Every LMS
password stored on the server is encrypted with it, so a database backup taken without it is
unreadable, and changing it makes every stored password unreadable at once. Keep it wherever the
backups are kept, and not only in Coolify.

---

## 3. Creating the resource

In Coolify: **New Resource → Docker Compose**.

| | |
|---|---|
| Repository | `https://github.com/Mo7amed7osam/ClassFlow-Automation` |
| Branch | `Windows_and_web` |
| Base directory | `/` (the repository root) |
| Compose file | `deploy/docker-compose.coolify.yml` |
| Domain | on the **`backend`** service only |

The build contexts are the repository root, because the worker's project references reach up into
`Windows/src/`. That is why the compose file says `context: ..`.

Three services come up: `postgres`, `backend`, `worker`. Only `backend` gets a domain. PostgreSQL
and the worker have no published port and nothing connects in to them — the worker only ever
connects out.

---

## 4. First run

### Migrations

They are not run automatically, on purpose: an automatic migration on every deployment is how a
half-finished schema change reaches production at the worst moment. Run it yourself, once, from the
backend container:

```bash
python -m central_backend.cli migrate
```

It applies `0001` through `0013`. Every migration only adds; none drops data. Running it again when
there is nothing to do is safe.

### The first admin

```bash
python -m central_backend.cli create-admin --username admin
```

It asks for the password twice and never prints it. After this, **an admin makes other admins from
the Users page** — a coordinator is never promoted, and the account's role is fixed when it is
created. This command stays the way back in if every admin is ever locked out.

### Checking the backend

```bash
curl -fsS https://<your domain>/health
```

`{"status":"ok"}` means the app is up **and** its database answers. A 503 means the database is not
reachable, which is a different problem from a 502.

Then open `https://<your domain>/dashboard/` and sign in.

---

## 5. Enrolling the worker

The worker proves who it is with a device token. It gets one by presenting a single-use enrolment
token, once.

```bash
# On the backend container
python -m central_backend.cli create-enrollment-token --label cloud-worker-1
```

Put the printed token in `ZAA_ENROLLMENT_TOKEN` in Coolify and redeploy the worker. It swaps it for
a device token written to the state volume at `/var/lib/classflow/device-token`, mode 0600.

**Then clear `ZAA_ENROLLMENT_TOKEN` from Coolify.** It is spent, and leaving it there only causes
confusion at the next deployment.

The device token is a file, not a keyring entry, and that is a real trade: a container has no
Windows account to hang a secret on. What limits it is what the token is — it identifies one device
to one backend, it is revoked from the dashboard the moment it is suspect, and it opens nothing
else: not the LMS, not Zoom, not the database.

```bash
# If a worker is ever compromised
python -m central_backend.cli revoke-device <deviceId>
```

### Checking the machine

```bash
dotnet ZoomAutoAdmit.CloudWorker.dll preflight
```

Six checks, exit code 0 when all pass:

| Check | Why it is there |
|---|---|
| Platform | Names the OS; warns if this is Windows, where the desktop app is the better tool |
| Time zone | `Africa/Cairo` must resolve. A slim image has no tzdata and every class time would be wrong |
| State directory | Must be a writable volume; state in the image is lost on every deployment |
| Browser profiles | The same, for the signed-in Zoom and LMS sessions |
| Shared memory | `/dev/shm` ≥ 512 MB. **The single most common cause of a browser container that "just crashes"** — Docker's default is 64 MB and Chromium dies with no readable message. The compose file sets `shm_size: 1gb` |
| Browser | Actually launches Chromium and loads a page |

The worker refuses to start if the browser, the clock or either directory fails, rather than
connecting, taking work and failing every class it is given.

---

## 6. Connecting accounts

Not yet possible from the dashboard, and this is one of the pieces that is not built. Today a
coordinator's LMS sign-in and Zoom accounts reach the server from their own copy of the Windows app
(`DELEGATED-CLASSES.md`). Moving that onto the web needs a Connect-account flow with a temporary
remote browser for the interactive sign-in, because Zoom's own login may ask for MFA or a CAPTCHA —
and neither is to be bypassed. When one is needed, the right behaviour is to report `Needs login` or
`Needs verification` and tell the authorised person, which is what the existing code already does on
Windows.

---

## 7. What has not been tested, and what to do about it

Be precise about this when you report progress.

### What was run, and what it said

Both images were built and the whole stack was brought up on Docker, 2026-09-19:

| | |
|---|---|
| Both images build | Backend, with the dashboard built by Node inside it, and the worker |
| **Chromium runs headless on Linux** | `Chromium 151.0.7922.34, headless` - it launched and loaded a page inside the container. This was the hardest unknown in the deployment, and it is answered |
| The `/dev/shm` check is real | At Docker's default it reports `only 64 MB` and fails; with `shm_size: 1gb` it passes. Tested both ways |
| The stack comes up | PostgreSQL healthy, backend healthy, `/health` gives `{"status":"ok"}`, `/dashboard/` gives 200 |
| Migrations run clean | `0001` through `0013` from an empty database, 24 tables |
| Several admins work on a real database | Two admins created, the second signed in with `role: admin`, and it made a third through the API |
| The rules hold live | Promoting a coordinator is refused with `400`. Deleting one answers `{"deleted":true,...}` and says what went with them |

Away from Docker: 410 backend tests against a real PostgreSQL, 322 web-automation tests on the
`net8.0` build, and the whole Windows solution still building unchanged.

### What is still unverified

| | |
|---|---|
| **Nothing has touched real Zoom or the real LMS.** | No admission, no attendance, no LMS step, no co-host assignment, no Zoom report has been run. Chromium starting headless is necessary and not sufficient: whether *Zoom's web client* tolerates a headless browser is a different question. If it refuses, set `ZAA_HEADLESS=false` and the container's Xvfb gives it a display |
| **The class stages do not exist.** | The worker has no job types for them, so there is nothing to run even with credentials |
| **Concurrency is a guess.** | `ZAA_MAX_CONCURRENT_SESSIONS=2` is a deliberately small default, not a measurement |
| **Only on Docker Desktop.** | The stack has not been run on a real VPS, nor through Coolify itself |

### Reproducing the build

```bash
# From the repository root, on the VPS or any Linux box
docker build -f deploy/backend.Dockerfile -t classflow-backend .
docker build -f deploy/worker.Dockerfile  -t classflow-worker  .

# The worker's own opinion of the machine, before Coolify is involved
docker run --rm --shm-size=1gb   -e ZAA_BACKEND_URL=https://example.com   classflow-worker preflight
```

All six checks passing there means the machine can do the work.

Five things had to be fixed to get that far. They are in the history rather than left for you to
find: `EnableWindowsTargeting` on restore, because restore reads every target framework a
referenced project declares and stops at the Windows one; a `.dockerignore`, without which a
Windows `obj/` lands on top of the container's own restore and the error names an unrelated
package; taking the Playwright driver from the NuGet package, because publish leaves only a
PowerShell wrapper; installing the browsers with the node that ships inside that driver; and
`uvicorn --factory`, because the app is built by `create_app()` and without it the container
restarts for ever on "Attribute 'app' not found".


## 8. Operating it

**Updating.** Coolify redeploys on a push to `Windows_and_web`. Run `migrate` yourself afterwards if
the release added a migration.

**Draining.** `SIGTERM` asks the worker to stop; it finishes the step it is in. A redeployment can
still interrupt a live meeting — there is no seamless continuity, and it is better to deploy between
classes than to claim otherwise.

**Backups.** The database and `CENTRAL_SECRETS_KEY`, together, or neither is any use:

```bash
docker exec <postgres container> pg_dump -U classflow classflow | gzip > classflow-$(date +%F).sql.gz
```

Restore into an empty database and run `migrate`. **Test a restore before you need one** — an
untested backup is a belief, not a backup.

The volumes `classflow-postgres` and `classflow-worker-state` survive a redeployment. Deleting the
Coolify resource removes them.

**What stays inside.** PostgreSQL and the worker have no published port. Do not add one, do not
expose a VNC or a browser-debugging port, and do not mount the Docker socket into any of these
services.

**The browser's sandbox stays on, and it is now actually checked.** `--no-sandbox` is the usual
advice for Chromium in a container and it is the wrong trade for a browser that visits pages on the
open web. The compose file uses `seccomp=unconfined` instead, which is the narrow permission the
sandbox itself needs: Docker's default seccomp profile blocks the unprivileged user namespaces
Chromium builds its sandbox from.

Both launch paths ask for the sandbox by name, because Playwright's default is to pass
`--no-sandbox` for you. If a deployment drops `security_opt`, the worker's preflight fails with a
message naming this, rather than starting a browser with no sandbox and reporting it as fine -
which is what happened until 2026-09-20.

---

## 9. When something is wrong

| What you see | What it usually is |
|---|---|
| Worker exits 1 straight away | Preflight failed. The log names which check and what to do |
| `403 HTTPS required` on every request | `CENTRAL_ENVIRONMENT=production` and the proxy's headers are not trusted. Check `CENTRAL_FORWARDED_ALLOW_IPS` |
| Chromium dies with nothing in the log | `/dev/shm`. Confirm `shm_size: 1gb` reached the container |
| `The time zone 'Africa/Cairo' is not on this machine` | tzdata missing from the image |
| Worker will not enrol | The enrolment token is single-use and expires. Make a fresh one |
| `503` from `/health` | The app is up; PostgreSQL is not answering it |
| LMS password reads fail with 503 | `CENTRAL_SECRETS_KEY` is not set on the backend |

---

## 10. Questions the answers to which change the build

Still open, and worth settling before the class stages are built:

1. Does the Zoom account whose reports are read have a paid plan? Some report pages are plan-gated.
2. How many simultaneous classes must one VPS carry? It sets the memory budget.
3. Every coordinator at once, or one group as a pilot?
4. A Zoom account, a DEPI LMS account and a class that is safe to automate against, for testing.
   Without them every live row stays unverified, however much code exists.
