import { log } from "./io.mjs";
import { DASHBOARD_PROFILE, attachRecordLink, localIsoDate } from "./lms.mjs";
import { acquireProfileLock } from "./lock.mjs";
import { isValidProfileName } from "./paths.mjs";
import { classifyLink, previewLink } from "./recording-links.mjs";
import { readRecordingLink } from "./zoom-recordings.mjs";

// The one path from "this session's recording" to the link on the dashboard. Each browser
// profile is held only while it is used - the Zoom one while the link is read, then the
// dashboard one while it is written - never both, so two requests cannot deadlock.

export const RecordingStatus = Object.freeze({
  attached: "attached",
  alreadyExists: "alreadyExists",
  dryRun: "dryRun",
  recordingNotFound: "recordingNotFound",
  busy: "busy",
  zoomFailed: "zoomFailed",
  lmsFailed: "lmsFailed",
});

const isSuccess = (status) => [RecordingStatus.attached, RecordingStatus.alreadyExists, RecordingStatus.dryRun].includes(status);

const LMS_REASONS = {
  notSignedIn: "lmsNotSignedIn",
  sessionNotFound: "sessionNotFound",
  sessionNotFinished: "sessionNotFinished",
  invalidLink: "invalidLink",
};

/** Finds the session's recording in Zoom, then attaches its share link. */
export async function processFromZoom(request, deps = defaultDeps) {
  const group = String(request.group ?? "").trim();
  const date = request.day ?? localIsoDate(new Date());
  const profile = request.profile && request.profile !== "default" ? request.profile.trim() : group;
  const base = { group, date, startTime: request.startTime ?? null, profile };
  const outcome = (status, message, extra = {}) => ({ ...base, status, success: isSuccess(status), message, ...extra });
  if (!group) return outcome(RecordingStatus.lmsFailed, "No group was given.", { reason: "invalidRequest" });
  if (!isValidProfileName(profile)) return outcome(RecordingStatus.zoomFailed, `'${profile}' is not a valid browser profile name.`, { reason: "zoomFailed" });

  log.info(`[RECORDINGS] ${group} ${date} ${request.startTime ?? "any time"}: profile '${profile}'${request.dryRun ? ", dry run" : ""}.`);
  const zoomLock = await deps.acquire(profile, request.lockWaitMs);
  if (!zoomLock) {
    return outcome(RecordingStatus.busy, `The '${profile}' browser profile is in use by another operation or an open browser. Try again shortly.`);
  }
  let recording;
  try {
    recording = await deps.readRecording({ ...request, group, profile, day: date });
  } catch (error) {
    log.warn(`[RECORDINGS] ${group}: reading Zoom failed (${error?.name ?? "Error"}).`);
    return outcome(RecordingStatus.zoomFailed, "Zoom could not be read.", { reason: "zoomFailed" });
  } finally {
    zoomLock.release();
  }

  if (!recording.success || !recording.shareUrl) {
    log.info(`[RECORDINGS] ${group}: ${recording.message}`);
    if (recording.failure === "notFound") return outcome(RecordingStatus.recordingNotFound, recording.message, { reason: "notFound" });
    if (recording.failure === "timeMismatch") {
      return outcome(RecordingStatus.recordingNotFound, recording.message, { reason: "timeMismatch", recordingStartedAtUtc: recording.startedAtUtc });
    }
    if (recording.failure === "notSignedIn") return outcome(RecordingStatus.zoomFailed, recording.message, { reason: "zoomNotSignedIn" });
    return outcome(RecordingStatus.zoomFailed, recording.message, { reason: "zoomFailed" });
  }

  const found = {
    ...base,
    recordingStartedAtUtc: recording.startedAtUtc,
    recordingDuration: recording.recording?.duration ?? null,
    shareLinkPreview: previewLink(recording.shareUrl),
  };
  return writeToDashboard(found, recording.shareUrl, request, deps);
}

/** Attaches a link the caller already has (the HTTP API). Zoom is never opened. */
export async function attachProvidedLink(request, deps = defaultDeps) {
  const group = String(request.group ?? "").trim();
  const date = request.day ?? localIsoDate(new Date());
  const link = String(request.recordLink ?? "");
  const found = { group, date, startTime: request.startTime ?? null, profile: DASHBOARD_PROFILE, shareLinkPreview: previewLink(link) };
  const kind = classifyLink(link);
  log.info(`[RECORDINGS] ${group} ${date}: attaching a given ${kind === "googleDrive" ? "Google Drive" : kind === "zoomShare" ? "Zoom" : "unrecognised"} link (${found.shareLinkPreview})${request.dryRun ? ", dry run" : ""}.`);
  if (kind === "none") {
    return { ...found, status: RecordingStatus.lmsFailed, success: false, message: "That is not a Zoom recording link or a Google Drive file link, so nothing was saved.", reason: "invalidLink" };
  }
  return writeToDashboard(found, link, { ...request, keepBrowserOpen: false }, deps);
}

async function writeToDashboard(found, link, request, deps) {
  const group = found.group;
  const dashboard = await deps.acquire(DASHBOARD_PROFILE, request.lockWaitMs);
  if (!dashboard) {
    return { ...found, status: RecordingStatus.busy, success: false, message: "The dashboard browser profile is in use by another operation or an open browser. Try again shortly." };
  }
  let attached;
  try {
    attached = await deps.attach({ ...request, group, recordLink: link, day: found.date });
  } catch (error) {
    log.warn(`[RECORDINGS] ${group}: the dashboard failed (${error?.name ?? "Error"}).`);
    return { ...found, status: RecordingStatus.lmsFailed, success: false, message: "The dashboard could not be reached.", reason: "lmsFailed" };
  } finally {
    dashboard.release();
  }
  if (!attached.success) {
    log.info(`[RECORDINGS] ${group}: attaching failed - ${attached.message}`);
    return { ...found, status: RecordingStatus.lmsFailed, success: false, message: attached.message, reason: LMS_REASONS[attached.failure] ?? "lmsFailed" };
  }
  const status = attached.alreadyExists ? RecordingStatus.alreadyExists : request.dryRun ? RecordingStatus.dryRun : RecordingStatus.attached;
  log.success(`[RECORDINGS] ${group}: ${status} - ${attached.message}`);
  return { ...found, status, success: true, message: attached.message };
}

export const defaultDeps = {
  acquire: (profile, waitMs) => acquireProfileLock(profile, { waitMs: waitMs ?? 120_000 }),
  readRecording: readRecordingLink,
  attach: attachRecordLink,
};

/** The dashboard profile is shared by every writer; LMS steps take it the same way. */
export async function withDashboardLock(waitMs, work) {
  const lock = await acquireProfileLock(DASHBOARD_PROFILE, { waitMs: waitMs ?? 120_000 });
  if (!lock) {
    return { success: false, failure: "busy", message: "The dashboard browser profile is in use by another operation or an open browser. Try again shortly." };
  }
  try {
    return await work();
  } finally {
    lock.release();
  }
}
