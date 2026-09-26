# Phase 2 cloud-parity audit

Audited on `linux` after `d790bc6`. **PASS** requires a verified deployed path;
**PARTIAL** means code exists but an important lifecycle or live verification is
missing; **MISSING** means there is no cloud path; **BLOCKED** requires a real
external account, OAuth configuration, or a safe test class.

| Capability | Status | Evidence / gap |
|---|---|---|
| Zoom web automation, mic/camera, waiting room and auto-admit | PARTIAL | `CloudWorker` starts Chromium and uses `WebAutoAdmitEngine`; no real Zoom account run on this VPS has been evidenced. |
| Persistent isolated Zoom profiles and account selection | PARTIAL | Profiles are per account and locks exist; two configured accounts still need their first verified server sign-in. |
| Meeting scheduling and Run Session | PARTIAL | Cairo scheduler creates idempotent class/LMS jobs; the scheduled G1/G2 classes have not been migrated and tested as live plans. |
| Participants and attendance snapshots | PARTIAL | Joined-list snapshots persist server-side, with match/alias/ignore/AI code; cadence is currently 10 minutes, not the required 15, and post-admit/watchdog evidence is incomplete. |
| LMS attendance, correction and completion | PARTIAL | Separate LMS stages exist; successful operation against a real DEPI session has not been proven. |
| Co-host | PARTIAL | A web co-host assigner exists; it has not been validated against Zoom Web on Linux. |
| Zoom recording discovery | PARTIAL | Group/date/time-window logic exists and avoids the Zoom API; retry-until-08:00 is not yet represented as durable occurrence state. |
| Google Sheets read-only sync | PARTIAL | OAuth, encrypted refresh token, 08:00 Cairo and a durable source-row ledger are deployed. Google OAuth credentials and a real sheet connection are not configured. |
| Drive link replacement on LMS | MISSING | Sheet rows currently create pending recordings only; they do not prove one completed LMS session and replace that session's Zoom link. |
| Persistent ClassOccurrence lifecycle | MISSING | `ClassPlan`, jobs and attendance rows exist, but there is no single durable occurrence with the requested state machine. |
| Persistent queues / recovery | PARTIAL | Jobs, retries, browser profiles and journals persist; recording retry and all occurrence-level recovery are not yet durable. |
| Pre-flight, notifications, operations dashboard and guide | PARTIAL | Worker preflight and basic dashboard/activity exist; the requested occurrence-focused operations view, notification history and built-in guide are absent. |
| Restart and VPS recovery | PARTIAL | Device tokens, profile state, journals and job retries persist; no controlled worker/VPS restart during a live class has been tested. |

## Fundamental cloud limits

Desktop Accessibility, Windows Task Scheduler and WPF do not move to Linux. The
cloud replacement is Zoom Web through Chromium. Remaining unknowns are Zoom MFA/CAPTCHA,
Zoom Web's waiting-room/co-host UI, and a real DEPI LMS session. They remain **BLOCKED**
until a controlled live test proves them; a successful build is not sufficient.

## Confirmed meeting-end policy

Cloud follows the existing safe auto-end rule: it monitors the meeting after the class window
and only uses Zoom's `End for all` action when the room is idle or the confirmed co-host has
left with most attendees. The scheduled LMS end time still drives LMS finalization independently;
it does not wait for Zoom to close.
