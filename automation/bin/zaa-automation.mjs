#!/usr/bin/env node
// Entry point the macOS app runs. One command per process; the request is JSON on stdin,
// progress and the result are JSON lines on stdout (see src/io.mjs).
//
//   lms-verify-sign-in        { credentials }
//   lms-run-session           { credentials, group, day?, startTime?, headed?, dryRun? }
//   lms-take-attendance       { credentials, group, present[], everyone?, day?, startTime?, dryRun? }
//   lms-correct-attendance    { credentials, group, present[], everyone?, day?, startTime?, dryRun? }
//   lms-attach-record-link    { credentials, group, recordLink, day?, startTime?, replaceExisting?, dryRun? }
//   recording-from-zoom       { credentials, group, profile, day?, startTime?, zoomUtcOffsetMinutes?, ... }
//   serve-api                 { credentials, apiKey, port?, host?, allowedClients?, lockWaitSeconds? }   (runs until stdin closes)
//   web-meeting               { meetingUrl, profile, headless?, sessionId?, captureAttendance? }        (runs until stdin closes)
//   read-timetable            { path }
//   read-roster               { path }
//   open-profile              { profile, url? }   (a visible browser to sign in once; runs until closed)
//   version                   {}

import process from "node:process";
import { attachProvidedLink, processFromZoom, withDashboardLock } from "../src/recordings.mjs";
import { firstLine } from "../src/browser.mjs";
import { log, readFirstLine, readRequest, result } from "../src/io.mjs";
import { attachRecordLink, correctAttendance, runSession, takeAttendance, verifySignIn } from "../src/lms.mjs";
import { parseRoster, parseTimetable, readFirstSheet } from "../src/workbooks.mjs";

const command = process.argv[2];

const lockWait = (request) => (typeof request.lockWaitSeconds === "number" ? request.lockWaitSeconds * 1000 : 120_000);

const oneShot = {
  "version": async () => ({ success: true, node: process.version, helper: "0.1.0" }),
  "lms-verify-sign-in": (request) => withDashboardLock(lockWait(request), () => verifySignIn(request)),
  "lms-run-session": (request) => withDashboardLock(lockWait(request), () => runSession(request)),
  "lms-take-attendance": (request) => withDashboardLock(lockWait(request), () => takeAttendance(request)),
  "lms-correct-attendance": (request) => withDashboardLock(lockWait(request), () => correctAttendance(request)),
  "lms-attach-record-link": (request) => withDashboardLock(lockWait(request), () => attachRecordLink(request)),
  "recording-attach-link": (request) => attachProvidedLink({ ...request, lockWaitMs: lockWait(request) }),
  "recording-from-zoom": (request) => processFromZoom({ ...request, lockWaitMs: lockWait(request) }),
  "read-timetable": async (request) => ({ success: true, ...parseTimetable(await readFirstSheet(request.path)) }),
  "read-roster": async (request) => ({ success: true, ...parseRoster(await readFirstSheet(request.path)) }),
};

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

  if (command === "serve-api") {
    const { resolveOptions, startApiServer } = await import("../src/api-server.mjs");
    const request = await readFirstLine();
    const options = resolveOptions(request);
    const credentials = request.credentials;
    const server = await startApiServer(options, (apiRequest) => attachProvidedLink({ ...apiRequest, credentials }));
    await stopSignal();
    await server.close();
    result({ success: true, message: "Recording API stopped." });
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
