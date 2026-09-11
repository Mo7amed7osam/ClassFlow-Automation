# Recording API (for n8n)

A small, authenticated HTTP endpoint on this Windows PC that runs the **existing** recording
workflow — find the group's Zoom cloud recording, copy its share link, attach it to the matching
DEPI LMS session — exactly as `lms-record-link` does in the terminal.

```
n8n ──POST──▶ http://127.0.0.1:47821/api/recordings/process   (X-API-Key)
                 │
                 ▼  RecordingLinkProcessor   ◀── the same object lms-record-link uses
                 ├─ Zoom:  My Recordings → pick by group/date/time → copy share link
                 │         (the link's own startTime is checked against the session)
                 └─ LMS:   open the session → Add/Edit Record Link → Save
```

> **Read "Limitations" before connecting n8n.** In the recordings checked on 2026-09-11,
> Zoom **no longer had** any recording that was already listed in the Google Sheet — the
> recordings are removed from Zoom when they are moved to Drive. A request made *because* a new
> row appeared in the sheet will therefore usually answer **404 Recording not found**.

---

## 1. Architecture (why it is built this way)

| Choice | Reason |
|---|---|
| Runs as `ZoomAutoAdmit.Inspector.exe serve-api` | The same executable the scheduled meetings already run. No new program, no Windows Service, easy to start, stop and read. |
| Built-in Windows HTTP server (`HttpListener` / http.sys) | **The ASP.NET Core runtime is not installed** in `C:\Program Files\dotnet` on this PC, so a Kestrel/Minimal API app would build here but fail to start when Windows launches it. http.sys is part of the runtime already installed and binds `127.0.0.1` without administrator rights. |
| Not inside the WPF window | The API keeps working when the window is closed, and the window is untouched. |
| One shared workflow (`RecordingLinkProcessor`) | `lms-record-link` and the API call the same object — no second copy of the Zoom or LMS logic. |

The WPF app is not changed by this feature.

## 2. Configure it (once)

Run in **PowerShell** as your normal user. This creates a random key, stores it for your Windows
user only, and copies it to the clipboard so you can paste it into n8n. It is not printed and not
saved to any file.

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
[Environment]::SetEnvironmentVariable('ZOOM_AUTO_ADMIT_API_KEY', $key, 'User')
$key | Set-Clipboard
Remove-Variable key, bytes
```

Also set the Zoom account's time zone (see §8 for why):

```powershell
[Environment]::SetEnvironmentVariable('ZOOM_AUTO_ADMIT_ZOOM_TIMEZONE', 'Pacific Standard Time', 'User')
```

Then **sign out of Windows and back in** (programs that are already open do not see new user
variables).

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `ZOOM_AUTO_ADMIT_API_KEY` | **yes** | — | The key n8n sends in `X-API-Key`. At least 20 characters. Without it the API refuses to start. |
| `ZOOM_AUTO_ADMIT_API_PORT` | no | `47821` | Port on `127.0.0.1`. 1024–65535. |
| `ZOOM_AUTO_ADMIT_ZOOM_TIMEZONE` | recommended | none | The time zone of the Zoom **profile** (zoom.us → Profile → Time Zone), as a Windows id (`Pacific Standard Time`) or IANA id (`America/Los_Angeles`). |
| `ZOOM_AUTO_ADMIT_API_LOCK_WAIT_SECONDS` | no | `120` | How long a request waits for a busy browser profile before answering 409. |

To change the key later, run the first block again, restart the API, and update n8n.

## 3. Start it

The executable is the one in the WPF app's Release folder (the one the scheduled meetings use):

```
E:\zooommmm\zoom-auto-admit claude\Windows\src\ZoomAutoAdmit.WindowsUI\bin\Release\net8.0-windows10.0.19041.0\ZoomAutoAdmit.Inspector.exe
```

- **By hand** (shows a console with the log; Ctrl+C stops it):
  `ZoomAutoAdmit.Inspector.exe serve-api`
- **At every sign-in** (no admin rights; one file in your Startup folder):
  `ZoomAutoAdmit.Inspector.exe api-autostart --enable`
  - `api-autostart` alone shows whether it is on and whether the key is set.
  - `api-autostart --disable` removes it. A running API keeps running until stopped.
  - It starts `serve-api --background`, which hides its own console window.
- **Stop a background one:** Task Manager → *ZoomAutoAdmit.Inspector* → End task, or
  `Get-Process ZoomAutoAdmit.Inspector | Stop-Process`. (Scheduled meetings also run as
  `ZoomAutoAdmit.Inspector` — check the command line if one is running.)

Only one API can hold the port; a second start says so and exits. If you move or rebuild the
project somewhere else, run `api-autostart --enable` again from the new location.

Log: `%LOCALAPPDATA%\ZoomAutoAdmit\Logs\recording-api.log` (rolls over at 5 MB). It records each
request's group, date, time, profile and flags, each stage, and each answer's status and duration.
It **never** records the API key, headers, passwords, cookies, browser profile contents, share
links in full, or any AI key.

## 4. Check it with curl

In **Windows PowerShell** `curl` is an alias for `Invoke-WebRequest`, so either type `curl.exe`
or use the PowerShell lines below.

```bash
curl.exe http://127.0.0.1:47821/health
```
→ `{"status":"ok"}` (no key needed — see §6).

```bash
curl.exe -i -X POST http://127.0.0.1:47821/api/recordings/process -H "Content-Type: application/json" -d "{\"group\":\"CAI5_AIS4_S7\"}"
```
→ `401 Unauthorized` (no key).

A **dry run** goes all the way to the LMS record-link box and stops before Save. From PowerShell,
reading the key from your user settings so it is never typed or shown:

```powershell
$headers = @{ 'X-API-Key' = [Environment]::GetEnvironmentVariable('ZOOM_AUTO_ADMIT_API_KEY', 'User') }
$body = @{ group = 'CAI5_AIS4_S7'; date = '2026-09-11'; startTime = '14:13'; dryRun = $true } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:47821/api/recordings/process -Headers $headers -ContentType 'application/json' -Body $body
```

(Windows PowerShell turns a 4xx/5xx answer into an error; the answer's JSON is in the error text.)

## 5. The contract

### `POST /api/recordings/process`

Headers: `X-API-Key: <key>`, `Content-Type: application/json`. Body ≤ 16 KB.

```json
{
  "group": "CAI5_AIS4_S7",
  "date": "2026-09-11",
  "startTime": "14:13",
  "timeZone": "local",
  "profile": "default",
  "headed": false,
  "dryRun": false,
  "replaceExisting": false
}
```

| Field | Rules |
|---|---|
| `group` | Required. Trimmed. Letters, digits, space, `_ - .`, ≤ 100 chars. |
| `date` | Optional, `yyyy-MM-dd`. Absent or `null` → today (this PC's date). `""` is refused. |
| `startTime` | Optional, 24-hour `HH:mm`. Absent or `null` → any recording of that day (one per day is picked; with several, the longest is taken). `""` is refused. |
| `timeZone` | Optional, `"local"` (default) or `"utc"`. **Addition to the requested contract** — see §8. With `"utc"`, both `date` and `startTime` are required and are converted to this PC's time (Egypt, including daylight saving) before anything else. |
| `profile` | Optional. Absent, `null` or `"default"` → **the account's own web profile** as configured in the app (`CAI5_AIS4_S7 → s7`, `CAI5_AIS4_S8 → s8`); only if an account has none, a profile named after the group. Any other value names a profile folder. |
| `headed` | Optional bool. Shows the browsers. The API always closes them afterwards. |
| `dryRun` | Optional bool. Everything except the final Save. |
| `replaceExisting` | Optional bool, default `false`. An existing LMS record link is **never** overwritten unless this is `true`. |

Refused with **400**: any unknown field (a typo such as `starttime` is an error, not ignored),
and any of `recordLink`, `file`, `fileName`, `driveUrl`, `googleDriveUrl`, `link` — the
application finds the Zoom recording itself.

### Answers

| HTTP | When | Body |
|---|---|---|
| **200** | Attached | `{"success":true,"message":"Recording link attached successfully.","group":"…","date":"…","startTime":"…","alreadyExists":false,"recordingStartedAtUtc":"…","recordingDuration":"03:29:13"}` |
| **200** | The session already had a link (and `replaceExisting` was false) | `{"success":true,"message":"…already has a recording link…","alreadyExists":true,…}` |
| **200** | `dryRun` | `{"success":true,"dryRun":true,"message":"…Nothing was saved.",…}` |
| **400** | Bad request | `{"success":false,"error":"Invalid request","details":"…"}` |
| **401** | Missing or wrong key | `{"success":false,"error":"Unauthorized"}` |
| **404** | No matching Zoom recording | `{"success":false,"error":"Recording not found","group","date","startTime","reason":"notFound" \| "timeMismatch","message"}` |
| **409** | A browser profile stayed busy | `{"success":false,"error":"Busy","message"}` — retry in a few minutes. |
| **500** | Zoom step failed | `{"success":false,"error":"Zoom operation failed","reason":"zoomNotSignedIn" \| "zoomFailed","message"}` |
| **500** | LMS step failed | `{"success":false,"error":"LMS operation failed","reason":"sessionNotFinished" \| "sessionNotFound" \| "lmsNotSignedIn" \| "lmsFailed","message"}` |
| 403 | Connection not from this PC | `{"success":false,"error":"Forbidden"}` |

`reason` is an addition so n8n can decide what to do: `sessionNotFinished` means the LMS does
not offer *Add Record Link* yet — retry later. Messages never contain stack traces, keys, cookies
or file paths.

### `GET /health`

`200 {"status":"ok"}`. No key: it only proves the API is up, reveals nothing, and is reachable
from this PC only (every request from another address gets 403).

## 6. Security

- Listens on `127.0.0.1` / `localhost` only, and additionally refuses any connection whose remote
  address is not loopback (http.sys routes by Host header, so the bound address alone is not
  trusted).
- The key is compared in constant time against its SHA-256; the text of the key is not kept.
- The key is checked before the body is read, so a caller without it learns nothing.
- **Never** forward the port on your router or bind it to `0.0.0.0`.

## 7. Behaviour you can rely on

- **Same logic as the terminal.** `lms-record-link` now goes through the same
  `RecordingLinkProcessor`. Its flags and exit codes are unchanged (0 done, 1 bad arguments,
  2 recording not found / Zoom failed, 3 LMS failed; new: 4 busy). One change: without
  `--profile` it now uses the account's configured web profile (`s7`) instead of creating a
  separate copy named after the group.
- **One operation per browser profile, across processes.** The Zoom profile is held while the
  link is read, then released; the dashboard profile (`lms-dashboard`) is held while it is
  written. They are never held together, so two requests cannot deadlock. A request waits up to
  `ZOOM_AUTO_ADMIT_API_LOCK_WAIT_SECONDS`, then gets 409. A profile that Chromium already has
  open — a live Web meeting, a browser left open by `--headed` — counts as busy (Chromium's
  `lockfile` in the profile folder is checked), so the API never starts a second browser on it.
- **Duplicates are safe.** With `replaceExisting: false` a second request for the same session
  answers 200 `alreadyExists: true` and changes nothing.
- **A caller hanging up does not stop the work.** A save is never cut in half; the next request
  simply finds the link already there. Set n8n's timeout generously (§9).

## 8. Time zones — measured, not assumed

For the S7 recording of 2026-09-01, four sources were compared:

| Source | Value |
|---|---|
| Drive file name | `CAI5_AIS4_S7_2026-09-01_1558.mp4` → **15:58** |
| `startTime=` in the Zoom share link | `1788278291000` → **2026-09-01 15:58:11 UTC** |
| Zoom *My Recordings* list | **`Sep 1, 2026 08:58 AM`** |
| The class in the schedule | **19:00** Cairo |

So:
- **The Drive file name is in UTC.** 15:58 UTC is **18:58 in Cairo** — two minutes before the class.
- **Zoom's web list shows the Zoom profile's time zone**, which was UTC−7 in September (US
  Pacific). Confirmed again on 2026-09-11: Zoom listed `04:13 AM`, converted with
  `ZOOM_AUTO_ADMIT_ZOOM_TIMEZONE=Pacific Standard Time` to **14:13** Cairo, and matched.
- **The LMS and this app use Cairo time.**

Two safeguards follow:
1. Set `ZOOM_AUTO_ADMIT_ZOOM_TIMEZONE` so the list's times are converted before a recording is
   picked. (Check it on zoom.us → Profile. If it is Arizona rather than Pacific, use
   `US Mountain Standard Time` — the two differ in winter.)
2. Whatever the setting, **the copied share link's own `startTime` is checked** against the
   requested session (same Cairo day; from 30 minutes before to 3½ hours after the requested
   time). If it does not belong, nothing is attached and the answer is 404 `timeMismatch`. A wrong
   setting can therefore make a recording go unfound; it cannot attach the wrong week's video.

## 9. n8n — HTTP Request node

### Derive the fields from the file name (Code node, before the request)

The file names seen in the sheet are `GROUP_yyyy-MM-dd_HHmm.mp4`, sometimes with `_part1`/`_part2`.
The time is **UTC**, so it is sent with `"timeZone": "utc"` and the app converts it.

```javascript
// Input items look like: { group, fileName, type, date, link }
const pattern = /^(.+)_(\d{4}-\d{2}-\d{2})_(\d{2})(\d{2})(?:_part\d+)?\.mp4$/i;

return $input.all().map(({ json }) => {
  const match = pattern.exec(json.fileName ?? '');
  if (!match) throw new Error(`Unrecognised recording file name: ${json.fileName}`);
  const [, fileGroup, date, hh, mm] = match;
  const group = (json.group ?? fileGroup).trim();
  if (group !== fileGroup) throw new Error(`Group "${group}" does not match file "${json.fileName}"`);
  // json.link (the Drive URL) is deliberately NOT passed on.
  return { json: { group, date, startTime: `${hh}:${mm}`, timeZone: 'utc' } };
});
```

A `_part1` / `_part2` pair has the same time: the first request attaches the longest recording of
that session, the second answers `alreadyExists: true`.

### HTTP Request node

| Setting | Value |
|---|---|
| Method | `POST` |
| URL | `http://127.0.0.1:47821/api/recordings/process` |
| Authentication | **Generic Credential Type → Header Auth**, Name `X-API-Key`, Value = the key. (Or Authentication *None* and a header `X-API-Key` under *Send Headers* — but a credential keeps the key out of the workflow JSON.) |
| Send Body | on · Body Content Type **JSON** · Specify Body **Using JSON** |
| Options → Timeout | `300000` (a run takes ~30–75 s, longer if it waits for a busy profile) |
| Options → Response → Never Error | on, so 404/409/500 reach the next node to be branched on |

JSON body:

```json
{
  "group": "{{ $json.group }}",
  "date": "{{ $json.date }}",
  "startTime": "{{ $json.startTime }}",
  "timeZone": "{{ $json.timeZone }}",
  "profile": "default",
  "headed": false,
  "dryRun": false,
  "replaceExisting": false
}
```

Branch on the answer: `200` → record as done in the Data Table · `409` or reason
`sessionNotFinished` → retry later · `404` → see Limitations · `zoomNotSignedIn` /
`lmsNotSignedIn` → needs a person · `401` → the key in n8n does not match.

Test with `"dryRun": true` first.

## 10. Where n8n runs matters

`127.0.0.1` means **the machine the request is sent from**. The URL above only reaches this PC
when **n8n itself runs on this PC**.

| n8n runs… | Works? |
|---|---|
| On this PC, installed natively (`npx n8n`, n8n desktop) | Yes, as documented above. |
| In Docker on this PC | Not as is. Requests arrive from the Docker network, not loopback, with a `host.docker.internal` Host header; the API refuses both by design. Install n8n natively instead, or put a local reverse proxy/tunnel on this PC in front of it. (Not tested here.) |
| On another machine / n8n Cloud | Not directly — its `localhost` is its own machine. Use a tunnel that runs **on this PC** and forwards to `http://127.0.0.1:47821`: e.g. **Tailscale** (put the n8n host on the same tailnet) or **Cloudflare Tunnel** with Cloudflare Access in front. Configure the tunnel to send the Host header `127.0.0.1:47821` (Cloudflare: `originRequest.httpHostHeader`), or http.sys answers *Bad Request – Invalid Hostname*. Keep the API key on top of the tunnel's own protection. **Never** open the port on your router. |

## 11. Limitations (verified on 2026-09-11)

1. **Recordings disappear from Zoom when they reach Drive.** On 2026-09-11 Zoom listed only one
   S7 recording — that day's, still processing. The 1, 4, 6 and 8 September recordings were already
   in the sheet and gone from Zoom. A live request for 1 September 18:58 answered
   `404 Recording not found` in 27 s. Because this API is triggered *by* a new sheet row, and it may
   not take a Drive link, most real requests will be 404. Making the Drive link usable would need a
   separate, explicitly validated path — not built, pending your decision.
2. **A recording still processing can fail to open.** Today's recording was matched correctly but
   its page timed out while opening (500 `zoomFailed`); it is expected to work once Zoom finishes.
3. **Restarted classes.** Sending the second file's own time can be more than 30 minutes after the
   LMS session's time; if the group has more than one session that day, the LMS row is then not
   matched (500 `sessionNotFound`). The first file's request has already attached the longest
   recording in that case.
4. **The API must be running** when n8n calls; with `api-autostart --enable` it starts at sign-in,
   not before anyone signs in to Windows.
5. The Zoom and LMS steps are browser automation of third-party pages; a redesign of either can
   break them, as it could for the terminal.
