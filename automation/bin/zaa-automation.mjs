#!/usr/bin/env node
// Entry point the macOS app runs. One command per process; the request is JSON on stdin,
// progress and the result are JSON lines on stdout (see src/io.mjs).
//
//   lms-verify-sign-in        { credentials }
//   lms-run-session           { credentials, group, day?, startTime?, headed?, dryRun? }
//   lms-take-attendance       { credentials, group, present[], everyone?, day?, startTime?, dryRun? }
//   lms-correct-attendance    { credentials, group, present[], everyone?, day?, startTime?, dryRun? }
//   lms-end-session           { credentials, group, day, startTime?, dryRun? }   (at the class's end time)
//   zoom-recording-to-lms     { credentials, group, day, startTime?, profile, zoomUtcOffsetMinutes?, localUtcOffsetMinutes?, dryRun? }
//   lms-check-session         { credentials, group, day }   (health check: sign in and count listed sessions, read-only)
//   lms-sync-record-link      { credentials, group, day, driveUrl, dryRun? }   (Google Sheet → LMS)
//   web-meeting               { meetingUrl, profile, headless?, sessionId?, captureAttendance? }        (runs until stdin closes)
//   read-timetable            { path }
//   read-roster               { path }
//   open-profile              { profile, url? }   (a visible browser to sign in once; runs until closed)
//   version                   {}

import process from "node:process";
import { acquireProfileLock, withDashboardLock } from "../src/lock.mjs";
import { firstLine } from "../src/browser.mjs";
import { log, readFirstLine, readRequest, result } from "../src/io.mjs";
import { attachZoomLink, checkSession, correctAttendance, endSession, runSession, syncRecordLink, takeAttendance, verifySignIn } from "../src/lms.mjs";
import { parseRoster, parseTimetable, readFirstSheet } from "../src/workbooks.mjs";

const command = process.argv[2];

const lockWait = (request) => (typeof request.lockWaitSeconds === "number" ? request.lockWaitSeconds * 1000 : 120_000);

const oneShot = {
  "version": async () => ({ success: true, node: process.version, helper: "0.1.0" }),
  "lms-verify-sign-in": (request) => withDashboardLock(lockWait(request), () => verifySignIn(request)),
  "lms-run-session": (request) => withDashboardLock(lockWait(request), () => runSession(request)),
  "lms-take-attendance": (request) => withDashboardLock(lockWait(request), () => takeAttendance(request)),
  "lms-correct-attendance": (request) => withDashboardLock(lockWait(request), () => correctAttendance(request)),
  "lms-end-session": (request) => withDashboardLock(lockWait(request), () => endSession(request)),
  "zoom-recording-to-lms": (request) => zoomRecordingToLms(request),
  "lms-check-session": (request) => withDashboardLock(lockWait(request), () => checkSession(request)),
  "lms-sync-record-link": (request) => withDashboardLock(lockWait(request), () => syncRecordLink(request)),
  "read-timetable": async (request) => ({ success: true, ...parseTimetable(await readFirstSheet(request.path)) }),
  "read-roster": async (request) => ({ success: true, ...parseRoster(await readFirstSheet(request.path)) }),
};

/**
 * The class's Zoom cloud recording onto its session: the share link is read in the account's
 * Zoom browser profile, then written on the dashboard. One profile is held at a time.
 */
async function zoomRecordingToLms(request) {
  const { readRecordingLink } = await import("../src/zoom-recordings.mjs");
  const profile = String(request.profile ?? "").trim();
  if (!profile) return { success: false, failure: "failed", issue: "noProfile", message: "No Zoom browser profile is set for this class's account." };
  const lock = await acquireProfileLock(profile, { waitMs: lockWait(request) });
  if (!lock) return { success: false, failure: "busy", issue: "busy", message: `The '${profile}' browser profile is open elsewhere; the recording is tried again later.` };
  let recording;
  try {
    recording = await readRecordingLink(request);
  } finally {
    lock.release();
  }
  if (!recording.success || !recording.shareUrl) {
    const issue = { notFound: "recordingNotFound", timeMismatch: "recordingNotFound", notSignedIn: "zoomNotSignedIn" }[recording.failure] ?? "zoomFailed";
    return { ...recording, success: false, issue };
  }
  const written = await withDashboardLock(lockWait(request), () => attachZoomLink({ ...request, zoomUrl: recording.shareUrl }));
  return { ...written, zoomUrl: recording.shareUrl, recordingStartedAtUtc: recording.startedAtUtc ?? null };
}

/** Resolves when the app closes stdin or sends {"type":"stop"}, or on SIGTERM/SIGINT. */
function stopSignal() {
  return new Promise((resolve) => {
    let buffer = "";
    process.stdin.on("data", (chunk) => {
      buffer += chunk.toString("utf8");
      if (/"type"\s*:\s*"stop"/.test(buffer)) resolve();
    });
    process.stdin.on("end", resolve);
    process.stdin.on("close", resolve);
    process.once("SIGTERM", resolve);
    process.once("SIGINT", resolve);
  });
}

async function main() {
  if (!command) {
    result({ success: false, message: "No command was given." });
    process.exitCode = 64;
    return;
  }

  if (command === "web-meeting") {
    const { runWebMeeting } = await import("../src/zoom-web.mjs");
    const request = await readFirstLine();
    result(await runWebMeeting(request, stopSignal()));
    return;
  }

  if (command === "open-profile") {
    const { launchProfile, firstPage } = await import("../src/browser.mjs");
    const request = await readFirstLine();
    const context = await launchProfile(request.profile, { headless: false });
    const page = await firstPage(context);
    if (request.url) await page.goto(request.url, { waitUntil: "domcontentloaded" }).catch(() => {});
    log.info(`[PROFILE] '${request.profile}' is open. Close the window when you are done.`);
    const closed = new Promise((resolve) => context.on("close", resolve));
    await Promise.race([closed, stopSignal()]);
    await context.close().catch(() => {});
    result({ success: true, message: `Profile '${request.profile}' closed.` });
    return;
  }

  const handler = oneShot[command];
  if (!handler) {
    result({ success: false, message: `Unknown command '${command}'.` });
    process.exitCode = 64;
    return;
  }
  const request = await readRequest();
  const body = await handler(request);
  // The credentials never travel back out, whatever a handler returned.
  delete body.credentials;
  result(body);
  if (body.success === false) process.exitCode = 2;
}

main().catch((error) => {
  // Only the first line: deeper lines of a Playwright error can quote typed values.
  result({ success: false, message: error?.userFacing ? error.message : `The helper failed: ${firstLine(error)}` });
  process.exitCode = 1;
});
