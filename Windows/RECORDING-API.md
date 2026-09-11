# Recording API (for n8n)

A small, authenticated HTTP endpoint on a Windows PC that puts a recording link on a DEPI LMS
session. n8n sends the **Google Drive link** it read from the recordings sheet; the application
opens the matching session on the dashboard and writes that link — exactly as sent — into
*Add / Edit Record Link*. **It never opens or searches Zoom.**

```
Google Sheet ─▶ n8n (loop → memory check → IF new → Data Table insert → HTTP POST)
                                                                          │  X-API-Key
                                                                          ▼
                      Windows PC: API ─▶ lms-dashboard browser profile ─▶ DEPI LMS
                                                           session → Record Link = the Drive URL
```

The API holds no Google Sheets logic; n8n decides what is new. The Windows PC stays responsible
for everything that needs its browser: the automation, the LMS sign-in, the browser profiles and
the attaching itself. Nothing of that moves to n8n or to a tunnel provider — they only carry one
small JSON request and its JSON answer.

---

## 1. How it is built

| | |
|---|---|
| Runs as | `ZoomAutoAdmit.Inspector.exe serve-api` — the same executable the scheduled meetings run. No Windows Service. |
| HTTP server | Windows' built-in http.sys (`HttpListener`). Needs no ASP.NET Core runtime and, on loopback, no administrator rights. |
| Listens on | `127.0.0.1` **and** `[::1]` (IPv4 and IPv6 loopback), port `47821` — by default. Never on every address. See §5 for the one alternative. |
| Reached from elsewhere | Only through a tunnel or reverse proxy **you** install and configure (§5, Scenario B). The application opens no public listener and never touches the router. |
| Path of a request | connection check → HTTPS check → key check → parse & validate → `RecordingLinkProcessor.AttachProvidedLinkAsync` → take the `lms-dashboard` browser profile → `LmsSessionRunner.AttachRecordLinkAsync(group, recordLink, …)` → release. |
| Zoom | Not involved. The API's workflow is built without a Zoom source at all. |
| Terminal | `lms-record-link` is unchanged: it still finds the recording in Zoom and attaches the Zoom link, through the same dashboard step. |
| WPF app | Not changed by this feature. |

## 2. What the Windows PC needs

Any Windows 10/11 x64 PC can run it, as long as it has:

| Needed | Why | Admin? |
|---|---|---|
| **.NET 8 Runtime** and **.NET 8 Desktop Runtime** (x64) | The Inspector and the WPF app. The ASP.NET Core runtime is *not* needed. | installer |
| This application, built in **Release** (`dotnet build -c Release` in `Windows\`) | The executable in §4. | no |
| **Playwright Chromium** — once, from the Release folder: `.playwright\node\win32_x64\node.exe .playwright\package\cli.js install chromium` | The browser the dashboard step drives (goes to `%LOCALAPPDATA%\ms-playwright`). | no |
| **The LMS dashboard sign-in, saved in the app** (it lives in Windows Credential Manager) and the `lms-dashboard` browser profile it creates under `%LOCALAPPDATA%\ZoomAutoAdmit\Profiles` | The dashboard step signs in with it. Without it the API answers `reason: lmsNotSignedIn`. | no |
| **`ZOOM_AUTO_ADMIT_API_KEY`** for your Windows user (§3) | Without it the API refuses to start. | no |
| **Scenario B only:** a tunnel connector on this PC (Cloudflare `cloudflared`, or Tailscale) | The only way in from another machine. | its own installer |

Not needed for the API: Zoom sign-ins or the Zoom (`s7`, `s8`, …) profiles, an AI key, any open
inbound port.

The PC must be on, signed in (for `api-autostart`), and awake when n8n calls.

## 3. Settings

Environment variables **for your Windows user**. After setting or changing any of them, **sign out
of Windows and back in** (programs already open do not see new user variables).

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `ZOOM_AUTO_ADMIT_API_KEY` | **yes** | — | Sent by n8n as `X-API-Key`. ≥ 20 characters. Without it the API refuses to start. |
| `ZOOM_AUTO_ADMIT_API_HOST` | no | `127.0.0.1` | Where to listen. Empty, `127.0.0.1`, `localhost` or `::1` all mean **loopback** (`127.0.0.1` + `[::1]`). The only other accepted value is **one private IP address of this PC** (§5, B3). Refused, with the reason: `0.0.0.0`, `::`, `*`, `+`, host names, public addresses, addresses this PC does not have. |
| `ZOOM_AUTO_ADMIT_API_PORT` | no | `47821` | 1024–65535. |
| `ZOOM_AUTO_ADMIT_API_ALLOWED_CLIENTS` | only with a private `HOST` | — | Who may connect to that private address: IPs and/or ranges, e.g. `10.0.0.9` or `100.64.0.0/10`, separated by commas. A range that is everyone (`/0`) is refused. Ignored on loopback (there, only this PC can connect). |
| `ZOOM_AUTO_ADMIT_API_LOCK_WAIT_SECONDS` | no | `120` | How long a request waits for a busy dashboard profile before answering 409. **Behind Cloudflare set `45`** (§5, B1). |

(`ZOOM_AUTO_ADMIT_ZOOM_TIMEZONE` is only used by the terminal's `lms-record-link`.)

**Create the key** — in PowerShell, as your normal user. It is stored for your Windows user only
and copied to the clipboard for n8n; it is not printed and not written to any file.

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
[Environment]::SetEnvironmentVariable('ZOOM_AUTO_ADMIT_API_KEY', $key, 'User')
$key | Set-Clipboard
Remove-Variable key, bytes
```

Other settings are set the same way, e.g.
`[Environment]::SetEnvironmentVariable('ZOOM_AUTO_ADMIT_API_LOCK_WAIT_SECONDS', '45', 'User')`.

## 4. Start it

The executable is in the WPF app's Release folder (the one the scheduled meetings use):

```
<repo>\Windows\src\ZoomAutoAdmit.WindowsUI\bin\Release\net8.0-windows10.0.19041.0\ZoomAutoAdmit.Inspector.exe
```

- **By hand** (a console with the log; Ctrl+C stops it): `ZoomAutoAdmit.Inspector.exe serve-api`
- **At every sign-in** (no admin rights; one file in your Startup folder):
  `ZoomAutoAdmit.Inspector.exe api-autostart --enable` · `api-autostart` shows the state ·
  `api-autostart --disable` removes it. It runs `serve-api --background`, which hides its window.
- **Stop a background one:** Task Manager → *ZoomAutoAdmit.Inspector* → End task.
  (Scheduled meetings also run as *ZoomAutoAdmit.Inspector* — check the command line.)

The first log line says exactly where it listens, e.g.
`[API] Recording API started on http://127.0.0.1:47821 and http://[::1]:47821 (loopback only).`
Only one API can hold the port; a second start says so and exits. After moving or rebuilding the
project elsewhere, run `api-autostart --enable` again from the new location.

**Log:** `%LOCALAPPDATA%\ZoomAutoAdmit\Logs\recording-api.log` (rolls over at 5 MB): each request's
group, date and flags, the link as a short preview (`drive.google.com/file/d/1A1bzB...`), each
dashboard step, and each answer's status, duration and origin — `(from 127.0.0.1 via 203.0.113.7)`,
where *via* is the client address a tunnel reports (logged only, never trusted). **Never** the API
key, other headers, passwords, cookies, browser profile contents or paths, the full recording link,
or any AI key.

## 5. Deployment

### Scenario A — n8n and the application on the same machine

Nothing to configure beyond §3. n8n calls:

```
http://127.0.0.1:47821/api/recordings/process
```

| n8n runs… | |
|---|---|
| natively on this PC (`npx n8n`, n8n desktop) | Works. `http://localhost:47821` works too (IPv4 or IPv6). |
| in **Docker** on this PC | Try `http://host.docker.internal:47821/health` from inside the container (not tested here). The API answers only connections that reach it on loopback; if the answer is `403 Forbidden`, the log shows the address it came from — run n8n natively, or use Scenario B's connector on this PC. Do not widen `HOST` for it. |

### Scenario B — n8n on another machine or in the cloud

The API **stays on loopback**. A tunnel connector runs **on the same Windows PC**, makes an
*outbound* encrypted connection to its provider, and forwards requests to `127.0.0.1:47821`:

```
n8n (cloud / other host)
   │  HTTPS  +  edge access check (Cloudflare Access service token / tailnet membership)
   │         +  X-API-Key
   ▼
tunnel provider edge  ══ outbound tunnel, started by the PC ══▶  connector on the Windows PC
                                                                      │ http://127.0.0.1:47821
                                                                      ▼
                                                                 Recording API (loopback only)
```

- No port is opened on the router or in Windows Firewall; nothing listens on a public address.
- HTTPS is terminated by the provider; the last hop (connector → API) never leaves the PC.
- The connector keeps the public host name in the `Host` header; the API does not care, so **no
  Host-header rewriting is needed** (checked: a request on loopback carrying a tunnel's host name is
  served).
- The provider's credentials (Cloudflare tunnel file, Tailscale login) stay with the connector on
  the PC, **never in this repository**.

n8n then calls:

```
https://<the host name you gave the tunnel>/api/recordings/process
```

Choose one:

#### B1 — Cloudflare Tunnel + Cloudflare Access (works with n8n Cloud)

1. In Cloudflare, create a tunnel and install `cloudflared` on the Windows PC (the dashboard shows
   the command; installing it as a service needs admin). Give it a **public hostname**, e.g.
   `recordings.example.com`, with **service `http://127.0.0.1:47821`**. With a local config file
   instead, the ingress rule is:
   ```yaml
   ingress:
     - hostname: recordings.example.com
       path: ^/(health|api/recordings/process)$     # nothing else is forwarded
       service: http://127.0.0.1:47821
     - service: http_status:404
   ```
2. **Cloudflare Access → Applications → Self-hosted** for that host name, with a **Service Auth**
   policy allowing one **service token** (Access → Service credentials). Without this, anyone on
   the internet can reach the endpoint and only the API key stands in the way.
3. Turn on **Always Use HTTPS** for the domain. (The API also refuses any request Cloudflare marks as
   plain HTTP — `403 HTTPS required`.)
4. Set `ZOOM_AUTO_ADMIT_API_LOCK_WAIT_SECONDS` = `45`. Cloudflare gives up on an answer after
   100 seconds (`524`); a run takes 15–40 s, so a 45 s wait for a busy profile keeps the worst
   case inside that. If a 524 still happens, the work finishes on the PC anyway, and n8n's retry
   answers `alreadyExists: true`.
5. In n8n, send three headers: `X-API-Key`, `CF-Access-Client-Id`, `CF-Access-Client-Secret`.

#### B2 — Tailscale Serve (self-hosted n8n that can join your tailnet)

For n8n on a server or PC you control. (n8n Cloud cannot join a tailnet — use B1.)

1. Install Tailscale on the Windows PC and on the n8n host, same tailnet. In the admin console,
   enable **MagicDNS** and **HTTPS certificates**.
2. On the Windows PC: `tailscale serve --bg http://127.0.0.1:47821` (Tailscale 1.52+; older
   versions use a different syntax, see `tailscale serve --help`). `tailscale serve status` shows
   it, `tailscale serve reset` removes it.
3. Restrict in the tailnet policy (ACL/grants) who may reach the PC on 443 — ideally only the n8n
   host.
4. n8n calls `https://<pc-name>.<tailnet>.ts.net/api/recordings/process` with `X-API-Key`.

**Never use `tailscale funnel`** for this — Funnel publishes to the whole internet with no access
check in front.

#### Any other tunnel or reverse proxy on this PC

Fine, if it: runs on this PC and forwards to `http://127.0.0.1:47821`; serves **HTTPS only**
externally; passes the `X-API-Key` header through unchanged; does not cache; allows at least
`LOCK_WAIT + 60` seconds for an answer; and, where it can, adds its own access check. Forward only
`/health` and `/api/recordings/process`.

#### B3 — a reverse proxy on another machine of a private network (only if B1/B2 do not fit)

For a proxy you run elsewhere on a LAN or VPN (e.g. nginx on an office server). The API then
listens on **one private address of this PC** and answers **only** the listed clients:

```powershell
[Environment]::SetEnvironmentVariable('ZOOM_AUTO_ADMIT_API_HOST', '10.0.0.5', 'User')             # this PC's private address
[Environment]::SetEnvironmentVariable('ZOOM_AUTO_ADMIT_API_ALLOWED_CLIENTS', '10.0.0.9', 'User')   # the proxy's address
```

Windows needs two one-time changes, **in an administrator terminal** (the API prints the exact first
command if they are missing):

```powershell
netsh http add urlacl url=http://10.0.0.5:47821/ user="$env:USERDOMAIN\$env:USERNAME"
New-NetFirewallRule -DisplayName 'Zoom Auto Admit recording API' -Direction Inbound -Protocol TCP -LocalPort 47821 -RemoteAddress 10.0.0.9 -Action Allow -Profile Private,Domain
```

(Undo: `netsh http delete urlacl url=http://10.0.0.5:47821/` and
`Remove-NetFirewallRule -DisplayName 'Zoom Auto Admit recording API'`.)

Caveats: the proxy → PC hop is plain HTTP, so the key crosses that network unencrypted — use it only
on a network you trust or one that is itself encrypted (a Tailscale tailnet address); the proxy must
serve HTTPS to n8n and send `X-Forwarded-Proto`; while on a private address the API does **not**
answer `127.0.0.1`. Not tested live here (it needs the admin changes above).

## 6. Verify connectivity

In Windows PowerShell `curl` is an alias for `Invoke-WebRequest`; type `curl.exe`.

**On the Windows PC** — is it running?

```bash
curl.exe http://127.0.0.1:47821/health
```

`{"status":"ok"}`. "Could not connect" → not running; read the log.

**On the Windows PC** — a dry run (fills the link box, does not save), reading the key from your
user settings so it is never typed or shown:

```powershell
$headers = @{ 'X-API-Key' = [Environment]::GetEnvironmentVariable('ZOOM_AUTO_ADMIT_API_KEY', 'User') }
$body = @{ group = 'CAI5_AIS4_S7'; recordLink = 'https://drive.google.com/file/d/<id>/view?usp=sharing'; date = '2026-09-01'; dryRun = $true } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:47821/api/recordings/process -Headers $headers -ContentType 'application/json' -Body $body
```

(Windows PowerShell turns a 4xx/5xx answer into an error; the answer's JSON is in the error text.)

**From outside (Scenario B)**, in this order — each step isolates one layer:

| Check | Expect | If not |
|---|---|---|
| `https://<host>/health` with the edge credentials (Cloudflare: the two `CF-Access-Client-*` headers) | `{"status":"ok"}` | 403 from Cloudflare → the service token/policy; 502/1033 → connector not running or wrong service URL; `403 {"error":"Forbidden"}` from the API → the connector is not forwarding to `127.0.0.1` |
| the same **without** the edge credentials (B1) | blocked by Cloudflare Access | the Access application does not cover the host name |
| `POST /api/recordings/process` without `X-API-Key` | `401 {"success":false,"error":"Unauthorized"}` | — |
| `http://` instead of `https://` | redirect, or `403 HTTPS required` | turn on Always Use HTTPS |
| n8n's node with `"dryRun": true` | `200 … "dryRun": true` | read §8's answers; the PC's log shows the request `(from 127.0.0.1 via <n8n's address>)` |

## 7. Security model

| Layer | What protects it |
|---|---|
| **Network** | Loopback by default: nothing off the PC can open a connection. The application refuses every setting that would listen more widely (all addresses, host names, public IPs). The private-address mode requires an allow-list, an admin reservation and a firewall rule — three deliberate steps. Every connection's **real** source address is checked before anything is read; forwarded headers are never trusted for that. |
| **Transport** | HTTPS at the tunnel edge. The API refuses requests a proxy marks as plain HTTP (`X-Forwarded-Proto: http` or `Forwarded: proto=http`) before looking at the key. The only unencrypted hop is connector → API inside the PC (B3: inside your private network). |
| **Edge access** | Cloudflare Access service token (B1) or tailnet membership + ACL (B2), in front of the API. |
| **Authentication** | `X-API-Key` on every request except `/health`, checked before the body is read; compared as SHA-256 in constant time; the key exists only in your user's environment and in the n8n credential. |
| **Input** | JSON only, ≤ 16 KB, unknown fields refused, Google Drive file links only. No file paths, no downloads, no uploads. |
| **Browsers** | No CORS headers at all (no `Access-Control-Allow-Origin`, preflights get 405): n8n is a server, and no web page can call the API from a browser. `X-Content-Type-Options: nosniff`, `Cache-Control: no-store`. |
| **Answers & log** | Errors never contain internals (those go to the log). Neither answers nor log contain the key, headers, cookies, LMS credentials, browser profile contents or directories, or the full link. |
| **What never leaves the PC** | LMS credentials (Windows Credential Manager), cookies and browser profiles (`%LOCALAPPDATA%\ZoomAutoAdmit\Profiles`), the browser automation. |

`/health` needs no key by design and answers only `{"status":"ok"}`. There is no other
unauthenticated endpoint.

If the key leaks: create a new one (§3), sign out and in (or restart the API), and update the n8n
credential. For B1 also rotate the service token.

## 8. The contract

**URL:** `<base>/api/recordings/process` and `<base>/health`, where `<base>` is
`http://127.0.0.1:47821` (Scenario A) or `https://<tunnel host name>` (Scenario B). Path and
method are the same everywhere.

### `POST /api/recordings/process`

| Header | Value |
|---|---|
| `X-API-Key` | the configured key |
| `Content-Type` | `application/json` |
| (B1 only) `CF-Access-Client-Id`, `CF-Access-Client-Secret` | the Cloudflare service token — checked by Cloudflare, never seen as meaningful by the API |

```json
{
  "group": "AST5_DAT1_S1",
  "recordLink": "https://drive.google.com/file/d/XXXXX/view?usp=sharing",
  "date": "2026-09-03",
  "replaceExisting": false
}
```

| Field | | Rules |
|---|---|---|
| `group` | **required** | The group as the dashboard lists it. Trimmed. Letters, digits, space, `_ - .`, ≤ 100 chars. |
| `recordLink` | **required** | The Google Drive file link (rules in §9). Written to the LMS **exactly as sent**; only surrounding spaces are removed. |
| `date` | optional | `yyyy-MM-dd`. The session's day. Absent or `null` → today. `""` is refused. |
| `replaceExisting` | optional | Default `false`: an existing record link is left alone. `true` replaces it. |
| `dryRun` | optional | Everything up to filling the link box; Save is not pressed. |
| `headed` | optional | Show the browser while it works (closed afterwards). |
| `startTime` | optional | `HH:mm`, Cairo time. Only to choose between **two sessions of the same group on the same day**. The application never works it out. |
| `profile` | optional | Accepted so an older node that still sends it keeps working. **Ignored** — the dashboard always uses its own `lms-dashboard` profile. |

Any other field is refused (400) rather than ignored — including `link`, `url`, `driveUrl`
(the answer says to use `recordLink`), `file` / `fileName` (the file is never downloaded or uploaded)
and `timeZone` (no longer used). A typo such as `recordlink` is an error.

### Answers

**200 — attached**
```json
{ "success": true, "group": "AST5_DAT1_S1", "date": "2026-09-03",
  "message": "Recording link attached successfully.", "alreadyExists": false }
```

**200 — the session already had a link** (`replaceExisting` false; nothing changed)
```json
{ "success": true, "group": "CAI5_AIS4_S7", "date": "2026-09-01",
  "message": "CAI5_AIS4_S7: the session already has a recording link, so it was left as it is.",
  "alreadyExists": true }
```

**200 — dry run**
```json
{ "success": true, "group": "CAI5_AIS4_S7", "date": "2026-09-01",
  "message": "CAI5_AIS4_S7: the record link box is open and holds the recording's link. Nothing was saved.",
  "alreadyExists": false, "dryRun": true }
```

**400 — invalid request**
```json
{ "success": false, "error": "Invalid request",
  "details": "'recordLink' must be a Google Drive link to one file, like https://drive.google.com/file/d/<file id>/view?usp=sharing." }
```

**401** `{ "success": false, "error": "Unauthorized" }` — missing or wrong key.

**403** `{ "success": false, "error": "Forbidden" }` — the connection's source is not allowed
(not loopback; or, in B3, not in the allow-list). `{ "success": false, "error": "HTTPS required" }` —
a proxy reported that the caller used plain HTTP.

**409** `{ "success": false, "error": "Busy", "message": "…" }` — the dashboard profile stayed busy
(another request, or a dashboard browser left open). Retry in a few minutes.

**500 — the LMS step failed**
```json
{ "success": false, "group": "…", "date": "…", "error": "LMS operation failed",
  "reason": "sessionNotFinished", "message": "The session page for … offers no Add Record Link; it reads \"running\". …" }
```

| `reason` | Meaning | What n8n should do |
|---|---|---|
| `sessionNotFinished` | The session is not *finished* yet, so the LMS offers no Add Record Link. | Retry later. |
| `sessionNotFound` | No session for the group on that date (or two that day and no `startTime`). | Check group / date. |
| `lmsNotSignedIn` | No dashboard sign-in saved in the app. | A person saves it in the app. |
| `lmsFailed` | The dashboard did not respond or the save did not take. | Retry later. |

Other: `404 Not found` (unknown path), `405` (wrong method), `500 Internal error` (unexpected;
details are in the log, never in the answer).

### `GET /health`

`200 {"status":"ok"}`, no key. It proves the API is up and reveals nothing else.

## 9. Google Drive link rules

Accepted — a link to **one Drive file**:

- `https://drive.google.com/file/d/<id>`, optionally followed by `/view`, `/preview` or `/edit`,
  and any query such as `?usp=sharing`
- `https://drive.google.com/open?id=<id>`

`<id>` is 20–100 of `A–Z a–z 0–9 _ -`. Refused, each with its own message:

- empty or missing · longer than 2048 characters · containing spaces or control characters
- local paths: `C:\…`, `\\server\…`, `file://…`
- not a valid absolute URL · not `https`
- any other host (`docs.google.com`, `drive.google.com.evil.example`, …), a non-default port, or
  `user:password@` in the URL
- Drive folders (`/drive/folders/…`) and download links (`/uc?…&export=download`)

The API accepts **only** Drive links. The LMS step itself accepts Drive links **and** Zoom share
links (`https://…zoom.us/…/rec/…`), which is what the terminal's `lms-record-link` still writes.

The application does not check who can open the Drive file. If the file is not shared
("anyone with the link"), students will see *Request access*.

## 10. n8n — HTTP Request node

The field names are exactly `group`, `recordLink`, `date`, `replaceExisting`. From a sheet row
`{ group, fileName, type, date, link }`, `link` goes into **`recordLink`**:

| Setting | Value |
|---|---|
| Method | `POST` |
| URL | Scenario A: `http://127.0.0.1:47821/api/recordings/process` · Scenario B: `https://<tunnel host name>/api/recordings/process` |
| Authentication | **Generic Credential Type → Header Auth**, Name `X-API-Key`, Value = the key. A credential keeps the key out of the workflow JSON. |
| Extra headers (B1 only) | `CF-Access-Client-Id` and `CF-Access-Client-Secret` — keep them in a credential too (e.g. a *Custom Auth* credential holding all three headers instead of Header Auth). |
| Send Body | on · Body Content Type **JSON** · Specify Body **Using JSON** |
| Options → Timeout | `300000` locally; behind Cloudflare the edge stops at 100 s anyway (see B1) |
| Options → Response → Never Error | on, so 409/500 bodies reach the next node |

JSON body:

```json
{
  "group": "{{ $json.group }}",
  "recordLink": "{{ $json.link }}",
  "date": "{{ $json.date }}",
  "replaceExisting": false
}
```

If a value could ever contain a quote, build the body with an expression instead so it is escaped:
`{{ JSON.stringify({ group: $json.group, recordLink: $json.link, date: $json.date, replaceExisting: false }) }}`

Notes on the row values:
- `date` must be `yyyy-MM-dd`. An empty cell becomes `""` and is refused — on purpose, so a missing
  date is visible instead of silently meaning "today". The sheet's date is the recording's **UTC**
  date; for classes held between midnight and about 03:00 Cairo time it is the previous day.
- `group` must be the dashboard's group name (e.g. `CAI5_AIS4_S7`).
- A `_part1` / `_part2` pair produces two rows: the first attaches its link, the second answers
  `alreadyExists: true`. Send `replaceExisting: true` only if the later part should win.

Branch on the answer: `200` → mark done in the Data Table · `409`, `524`, or `reason`
`sessionNotFinished` / `lmsFailed` → retry later · `400` → fix the mapping · `401` → the key in n8n
does not match · `403` → the tunnel/proxy setup (§6) · `lmsNotSignedIn` → needs a person. Test with
`"dryRun": true` first.

## 11. Behaviour you can rely on

- **Zoom is never searched.** Checked live: the requests went sign-in → that day's sessions →
  the session page, with no Zoom page opened.
- **Duplicates are safe.** `replaceExisting: false` never overwrites; a repeat answers
  `alreadyExists: true`.
- **One dashboard operation at a time**, across every process on the PC (the window, scheduled
  meetings, the terminal, the API): the `lms-dashboard` profile is locked by a file, and a dashboard
  browser Chromium already has open counts as busy.
- **A caller hanging up does not stop the work** — a save is never cut in half; a repeat then finds
  the link already there. (This is what makes a tunnel timeout harmless.)

## 12. Limitations

1. **A real Save of a Drive link has not been performed.** Verified live with dry runs on the
   S7 session of 2026-09-01: the LMS opened *Edit Record Link* and took the Drive link into the box;
   Save was deliberately not pressed, because that session already has its link. Whether the LMS
   accepts a Drive URL when saving is proven only the first time it is done for real.
2. **No tunnel was set up from here.** B1 and B2 are standard provider setups that need your
   accounts; what was tested is the API's side of them — requests on loopback carrying a tunnel's
   host name and forwarding headers, over IPv4 and IPv6, with and without the key, and the
   plain-HTTP refusal. B3 was tested up to Windows' refusal without the admin reservation.
3. The dashboard must show the session as **finished** before it offers *Add Record Link*
   (`reason: sessionNotFinished` until then).
4. A group with **two sessions on the same date** needs `startTime` (Cairo time) to tell them apart;
   without it nothing is attached (`sessionNotFound`).
5. The API must be running when n8n calls; with `api-autostart --enable` it starts at sign-in, not
   before anyone signs in, and not while the PC sleeps.
6. The dashboard step automates a third-party page; a redesign of it could break it, as for the
   terminal.
