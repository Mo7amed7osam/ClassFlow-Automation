# Recording API (for n8n)

A small, authenticated HTTP endpoint on this Windows PC that puts a recording link on a DEPI LMS
session. n8n sends the **Google Drive link** it read from the recordings sheet; the application
opens the matching session on the dashboard and writes that link — exactly as sent — into
*Add / Edit Record Link*. **It never opens or searches Zoom.**

```
Google Sheet ─▶ n8n (loop → memory check → IF new → Data Table insert → HTTP POST)
                                                                          │
      http://127.0.0.1:47821/api/recordings/process   X-API-Key           ▼
                                   Windows app ─▶ DEPI LMS: session → Record Link = the Drive URL
```

The API holds no Google Sheets logic; n8n decides what is new.

---

## 1. How it is built

| | |
|---|---|
| Runs as | `ZoomAutoAdmit.Inspector.exe serve-api` — the same executable the scheduled meetings run. No Windows Service. |
| HTTP server | Windows' built-in http.sys (`HttpListener`). The ASP.NET Core runtime is not installed on this PC, so a Kestrel app would build but not start when Windows launches it. |
| Path of a request | parse & validate → `RecordingLinkProcessor.AttachProvidedLinkAsync` → take the `lms-dashboard` browser profile → `LmsSessionRunner.AttachRecordLinkAsync(group, recordLink, …)` → release. |
| Zoom | Not involved. The API's workflow is built without a Zoom source at all; its slot holds a stand-in that refuses. |
| Terminal | `lms-record-link` is unchanged: it still finds the recording in Zoom and attaches the Zoom link, through the same dashboard step. |
| WPF app | Not changed by this feature. |

## 2. Configure it (once)

In **PowerShell**, as your normal user. This creates a random key, stores it for your Windows user
only and copies it to the clipboard for n8n. It is not printed and not written to any file.

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
[Environment]::SetEnvironmentVariable('ZOOM_AUTO_ADMIT_API_KEY', $key, 'User')
$key | Set-Clipboard
Remove-Variable key, bytes
```

Then **sign out of Windows and back in** (programs already open do not see new user variables).

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `ZOOM_AUTO_ADMIT_API_KEY` | **yes** | — | Sent by n8n as `X-API-Key`. ≥ 20 characters. Without it the API refuses to start. |
| `ZOOM_AUTO_ADMIT_API_PORT` | no | `47821` | Port on `127.0.0.1`, 1024–65535. |
| `ZOOM_AUTO_ADMIT_API_LOCK_WAIT_SECONDS` | no | `120` | How long a request waits for the busy dashboard profile before answering 409. |

(`ZOOM_AUTO_ADMIT_ZOOM_TIMEZONE` is only used by the terminal's `lms-record-link`; the API does not need it.)

## 3. Start it

The executable is in the WPF app's Release folder (the one the scheduled meetings use):

```
E:\zooommmm\zoom-auto-admit claude\Windows\src\ZoomAutoAdmit.WindowsUI\bin\Release\net8.0-windows10.0.19041.0\ZoomAutoAdmit.Inspector.exe
```

- **By hand** (a console with the log; Ctrl+C stops it): `ZoomAutoAdmit.Inspector.exe serve-api`
- **At every sign-in** (no admin rights; one file in your Startup folder):
  `ZoomAutoAdmit.Inspector.exe api-autostart --enable` · `api-autostart` shows the state ·
  `api-autostart --disable` removes it. It runs `serve-api --background`, which hides its window.
- **Stop a background one:** Task Manager → *ZoomAutoAdmit.Inspector* → End task.
  (Scheduled meetings also run as *ZoomAutoAdmit.Inspector* — check the command line.)

Only one API can hold the port; a second start says so and exits. After moving or rebuilding the
project elsewhere, run `api-autostart --enable` again from the new location.

**Log:** `%LOCALAPPDATA%\ZoomAutoAdmit\Logs\recording-api.log` (rolls over at 5 MB): each request's
group, date and flags, the link as a short preview (`drive.google.com/file/d/1A1bzB...`), each
dashboard step, each answer's status and duration. **Never** the API key, headers, passwords,
cookies, browser profile contents, the full recording link, or any AI key.

## 4. The contract

### `POST /api/recordings/process`

| Header | Value |
|---|---|
| `X-API-Key` | the configured key |
| `Content-Type` | `application/json` |

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
| `recordLink` | **required** | The Google Drive file link (rules in §5). Written to the LMS **exactly as sent**; only surrounding spaces are removed. |
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

Other: `403 Forbidden` (connection not from this PC), `404 Not found` (unknown path),
`405` (wrong method), `500 Internal error` (unexpected; details are in the log, never in the answer).

### `GET /health`

`200 {"status":"ok"}`, no key. It proves the API is up, reveals nothing, and answers only this PC.

## 5. Google Drive link rules

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

## 6. Check it

In Windows PowerShell `curl` is an alias for `Invoke-WebRequest`; type `curl.exe`, or use the
PowerShell lines.

```bash
curl.exe http://127.0.0.1:47821/health
```

A dry run, reading the key from your user settings so it is never typed or shown:

```powershell
$headers = @{ 'X-API-Key' = [Environment]::GetEnvironmentVariable('ZOOM_AUTO_ADMIT_API_KEY', 'User') }
$body = @{ group = 'CAI5_AIS4_S7'; recordLink = 'https://drive.google.com/file/d/<id>/view?usp=sharing'; date = '2026-09-01'; dryRun = $true } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:47821/api/recordings/process -Headers $headers -ContentType 'application/json' -Body $body
```

(Windows PowerShell turns a 4xx/5xx answer into an error; the answer's JSON is in the error text.)

## 7. n8n — HTTP Request node

The field names are exactly `group`, `recordLink`, `date`, `replaceExisting`. From a sheet row
`{ group, fileName, type, date, link }`, `link` goes into **`recordLink`**:

| Setting | Value |
|---|---|
| Method | `POST` |
| URL | `http://127.0.0.1:47821/api/recordings/process` |
| Authentication | **Generic Credential Type → Header Auth**, Name `X-API-Key`, Value = the key. (Or *None* plus a header `X-API-Key` under *Send Headers*; a credential keeps the key out of the workflow JSON.) |
| Send Body | on · Body Content Type **JSON** · Specify Body **Using JSON** |
| Options → Timeout | `300000` (a run takes ~15–40 s; longer if it waits for the dashboard profile) |
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

Branch on the answer: `200` → mark done in the Data Table · `409`, or `reason` `sessionNotFinished`
/ `lmsFailed` → retry later · `400` → fix the mapping · `401` → the key in n8n does not match ·
`lmsNotSignedIn` → needs a person. Test with `"dryRun": true` first.

## 8. Where n8n runs matters

`127.0.0.1` means **the machine the request is sent from**. The URL reaches this PC only when
**n8n itself runs on this PC**.

| n8n runs… | Works? |
|---|---|
| On this PC, installed natively (`npx n8n`, n8n desktop) | Yes. |
| In Docker on this PC | Not as is: requests arrive from the Docker network, not loopback, with a `host.docker.internal` Host header, and the API refuses both by design. Install n8n natively, or put a local reverse proxy/tunnel on this PC in front of it. (Not tested here.) |
| Another machine / n8n Cloud | Not directly — its `localhost` is its own machine. Use a tunnel that runs **on this PC** and forwards to `http://127.0.0.1:47821` (Tailscale with the n8n host on the same tailnet, or Cloudflare Tunnel with Cloudflare Access), and have it send the Host header `127.0.0.1:47821` (Cloudflare: `originRequest.httpHostHeader`) or http.sys answers *Bad Request – Invalid Hostname*. Keep the API key on top. **Never** open the port on your router. |

## 9. Behaviour you can rely on

- **Zoom is never searched.** Checked live: the requests went sign-in → that day's sessions →
  the session page, with no Zoom page opened.
- **Duplicates are safe.** `replaceExisting: false` never overwrites; a repeat answers
  `alreadyExists: true`.
- **One dashboard operation at a time**, across every process on the PC (the window, scheduled
  meetings, the terminal, the API): the `lms-dashboard` profile is locked by a file, and a dashboard
  browser Chromium already has open counts as busy.
- **A caller hanging up does not stop the work** — a save is never cut in half; a repeat then finds
  the link already there.

## 10. Limitations

1. **A real Save of a Drive link has not been performed.** Verified live with dry runs on the
   S7 session of 2026-09-01: the LMS opened *Edit Record Link* and took the Drive link into the box;
   Save was deliberately not pressed, because that session already has its link. Whether the LMS
   accepts a Drive URL when saving is proven only the first time it is done for real.
2. The dashboard must show the session as **finished** before it offers *Add Record Link*
   (`reason: sessionNotFinished` until then).
3. A group with **two sessions on the same date** needs `startTime` (Cairo time) to tell them apart;
   without it nothing is attached (`sessionNotFound`).
4. The API must be running when n8n calls; with `api-autostart --enable` it starts at sign-in, not
   before anyone signs in.
5. The dashboard step automates a third-party page; a redesign of it could break it, as for the
   terminal.
