import { firstPage, isTimeout, launchProfile } from "./browser.mjs";
import { log } from "./io.mjs";
import { isZoomShareLink } from "./recording-links.mjs";

// Reads the shareable link of a group's cloud recording the way it is read by hand: open My
// Recordings in the profile signed in as the account that owns them, find the recording named
// after the group, open it and press "Copy shareable link". Nothing here changes a recording.

const RECORDINGS_URL = "https://zoom.us/recording/";
// A class opens a little before its time and runs about three hours.
const OPENS_BEFORE_MINUTES = 30;
const RUNS_FOR_MINUTES = 210;

export async function readRecordingLink(request) {
  const group = String(request.group ?? "").trim();
  const profile = String(request.profile ?? "").trim();
  if (!group) return { success: false, failure: "failed", message: "No group was given." };
  if (!profile) return { success: false, failure: "failed", message: "No Zoom browser profile was given." };

  const context = await launchProfile(profile, { headless: !request.headed });
  const page = await firstPage(context);
  let step = "opening My Recordings";
  try {
    await page.goto(RECORDINGS_URL, { waitUntil: "domcontentloaded" });
    if (page.url().toLowerCase().includes("/signin")) {
      return {
        success: false,
        failure: "notSignedIn",
        message: `The '${profile}' browser profile is not signed in to Zoom, so the recordings could not be read.`,
      };
    }

    step = "searching the recordings for the group";
    await search(page, group);

    step = "finding the group's recording";
    const entries = shiftEntries(await readEntries(page), request.zoomUtcOffsetMinutes, request.localUtcOffsetMinutes);
    const picked = pickRecording(entries, group, request.day ?? null, request.startTime ?? null);
    if (!picked) {
      const when = request.day ? ` on ${request.day}${request.startTime ? ` around ${request.startTime}` : ""}` : "";
      return {
        success: false,
        failure: "notFound",
        message: entries.length === 0 ? `No cloud recording is listed for ${group}${when}.` : `None of the ${entries.length} listed recordings is ${group}${when}.`,
      };
    }
    log.info(`[RECORDING] Found ${group}'s recording of ${picked.recordedAt ?? "an unknown time"}${picked.duration ? `, ${picked.duration} long` : ""}.`);

    step = "opening the recording";
    await page.goto(picked.detailUrl, { waitUntil: "domcontentloaded" });
    await page.waitForLoadState("networkidle").catch(() => {});

    step = "copying the shareable link";
    // Zoom answers these buttons on the clipboard, not on the page.
    await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: new URL(page.url()).origin });
    await page.evaluate(() => navigator.clipboard.writeText(""));
    const copy = page.locator("[aria-label^='Copy shareable link']").first();
    if (await isVisibleWithin(copy, 15_000)) {
      await copy.evaluate((element) => element.click());
    } else {
      // A recording still processing has no files yet, but it can already be shared: its page
      // offers "share this recording", whose box holds "Copy link". The link works once it is ready.
      const share = page.getByText(/share this recording/i).first();
      if (!(await isVisibleWithin(share, 5_000))) {
        return { success: false, failure: "failed", message: `The recording page for ${group} does not offer a shareable link.` };
      }
      step = "opening the share box";
      await share.click();
      const dialog = page.locator("[role='dialog']").filter({ visible: true }).first();
      const copyLink = dialog.getByRole("button", { name: /^\s*Copy link\s*$/i }).first();
      if (!(await isVisibleWithin(copyLink, 15_000))) {
        return { success: false, failure: "failed", message: `The share box for ${group}'s recording offers no Copy link.` };
      }
      const access = (await dialog.innerText().catch(() => "")).replace(/\s+/g, " ");
      if (!/anyone with the link/i.test(access)) log.warn(`[RECORDING] ${group}: the share box does not say "Anyone with the link"; students may need access.`);
      step = "copying the link from the share box";
      await copyLink.click();
      log.info(`[RECORDING] ${group}: the recording is still processing; its share link was copied from the share box.`);
    }

    const link = await waitForClipboardLink(page);
    if (!link) return { success: false, failure: "failed", message: `The shareable link for ${group} was pressed but nothing was copied.` };

    // The list prints the Zoom account's clock; the link carries the real start in UTC. Checking
    // it means a wrong time zone can make a recording go unfound, but never attach the wrong week.
    const startedAtMs = readShareLinkStart(link);
    const check = startsWithinSession(startedAtMs, request.day ?? null, request.startTime ?? null);
    if (!check.ok) {
      return { success: false, failure: "timeMismatch", message: `${group}: ${check.reason} Nothing was attached.`, recording: picked, startedAtUtc: iso(startedAtMs) };
    }
    return { success: true, message: `${group}: the recording's shareable link was copied.`, shareUrl: link, recording: picked, startedAtUtc: iso(startedAtMs) };
  } catch (error) {
    log.warn(`[RECORDING] Failed while ${step}: ${error?.name ?? "Error"}.`);
    return { success: false, failure: "failed", message: `Zoom did not respond while ${step}.` };
  } finally {
    if (!(request.headed && request.keepBrowserOpen)) await context.close().catch(() => {});
  }
}

async function search(page, group) {
  const box = page.locator("input[aria-label='Search by name or meeting ID']").first();
  try {
    await box.waitFor({ state: "visible", timeout: 20_000 });
    await box.fill(group);
    await box.press("Enter");
    await page.waitForLoadState("networkidle").catch(() => {});
  } catch (error) {
    if (!isTimeout(error)) throw error;
    log.info("[RECORDING] The recordings page offered no search box; reading the list as it is.");
  }
  await page.waitForTimeout(2500);
}

async function readEntries(page) {
  const seen = new Set();
  const entries = [];
  for (const link of await page.locator("a[href*='/recording/detail']").all()) {
    const href = (await link.getAttribute("href")) ?? "";
    if (!href) continue;
    const absolute = new URL(href, page.url()).href;
    const text = (await link.innerText()).trim();
    if (seen.has(`${absolute}|${text}`)) continue;
    seen.add(`${absolute}|${text}`);
    entries.push({ topic: text, detailUrl: absolute, recordedAt: readRecordedAt(text), duration: readDuration(text) });
  }
  return entries;
}

async function waitForClipboardLink(page) {
  for (let attempt = 0; attempt < 12; attempt += 1) {
    let clipboard = "";
    try {
      clipboard = await page.evaluate(() => navigator.clipboard.readText());
    } catch {
      clipboard = "";
    }
    if (isZoomShareLink(clipboard)) return clipboard.trim();
    await page.waitForTimeout(500);
  }
  return "";
}

// ---------------------------------------------------------------- pure helpers (tested)

const MONTHS = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

/** "Sep 1, 2026 08:58 AM" → "2026-09-01T08:58", as printed (the Zoom account's clock). */
export function readRecordedAt(rowText) {
  const match = /([A-Z][a-z]{2,8})\s+(\d{1,2}),\s*(\d{4})\s+(\d{1,2}):(\d{2})\s*(AM|PM)/i.exec(String(rowText ?? "").replace(/\s+/g, " "));
  if (!match) return null;
  const month = MONTHS.indexOf(match[1].slice(0, 3).toLowerCase());
  if (month < 0) return null;
  let hours = Number(match[4]) % 12;
  if (match[6].toUpperCase() === "PM") hours += 12;
  const pad = (value) => String(value).padStart(2, "0");
  return `${match[3]}-${pad(month + 1)}-${pad(match[2])}T${pad(hours)}:${match[5]}`;
}

/** "03:29:13". Three parts on purpose: the row also carries a clock time like "08:58 AM". */
export function readDuration(rowText) {
  const match = /\b(\d{1,2}):([0-5]\d):([0-5]\d)\b/.exec(String(rowText ?? "").replace(/\s+/g, " "));
  return match ? `${match[1].padStart(2, "0")}:${match[2]}:${match[3]}` : null;
}

const durationSeconds = (duration) => {
  if (!duration) return 0;
  const [h, m, s] = duration.split(":").map(Number);
  return h * 3600 + m * 60 + s;
};

const minutesOf = (hhmm) => {
  const [h, m] = hhmm.split(":").map(Number);
  return h * 60 + m;
};

function isWithinSession(recordedHHmm, sessionHHmm) {
  const offset = minutesOf(recordedHHmm) - minutesOf(sessionHHmm);
  return offset >= -OPENS_BEFORE_MINUTES && offset <= RUNS_FOR_MINUTES;
}

/**
 * Moves each printed time from the Zoom account's clock to this Mac's. Offsets are minutes
 * east of UTC; without a Zoom offset the entries are returned untouched.
 */
export function shiftEntries(entries, zoomUtcOffsetMinutes, localUtcOffsetMinutes) {
  if (typeof zoomUtcOffsetMinutes !== "number" || typeof localUtcOffsetMinutes !== "number") return entries;
  const delta = localUtcOffsetMinutes - zoomUtcOffsetMinutes;
  if (delta === 0) return entries;
  return entries.map((entry) => {
    if (!entry.recordedAt) return entry;
    const shifted = new Date(`${entry.recordedAt}:00Z`);
    shifted.setUTCMinutes(shifted.getUTCMinutes() + delta);
    return { ...entry, recordedAt: shifted.toISOString().slice(0, 16) };
  });
}

/**
 * The recording of this group's session: an exact name first, then one containing the group;
 * the day (never a different one) and the session's hours pick it out, and of several inside
 * those hours the longest wins, because a restarted class leaves a short leftover behind.
 */
export function pickRecording(entries, group, day = null, startTime = null) {
  const wanted = String(group ?? "").trim().toLowerCase();
  if (!wanted) return null;
  const exact = entries.filter((entry) => entry.topic.trim().toLowerCase() === wanted);
  const loose = entries.filter((entry) => entry.topic.toLowerCase().includes(wanted));

  for (const named of [exact, loose]) {
    let candidates = named;
    if (day) candidates = candidates.filter((entry) => entry.recordedAt && entry.recordedAt.slice(0, 10) === day);
    if (candidates.length === 0) continue;
    if (candidates.length === 1) return candidates[0];
    if (startTime) {
      const inSession = candidates.filter((entry) => entry.recordedAt && isWithinSession(entry.recordedAt.slice(11, 16), startTime));
      if (inSession.length === 0) continue;
      return longest(inSession);
    }
    return longest(candidates);
  }
  return null;
}

function longest(entries) {
  return [...entries].sort((left, right) => {
    const byLength = durationSeconds(right.duration) - durationSeconds(left.duration);
    if (byLength !== 0) return byLength;
    return String(left.recordedAt ?? "9999").localeCompare(String(right.recordedAt ?? "9999"));
  })[0];
}

/** The real start Zoom puts in every share link (Unix ms or s, UTC), or null. */
export function readShareLinkStart(shareUrl) {
  const match = /[?&]startTime=(\d{10,13})(?:&|$)/.exec(String(shareUrl ?? ""));
  if (!match) return null;
  const value = Number(match[1]);
  return match[1].length >= 13 ? value : value * 1000;
}

/** Whether a recording that started at `startedAtMs` belongs to the session, in local time. */
export function startsWithinSession(startedAtMs, day, startTime) {
  if (startedAtMs === null || (!day && !startTime)) return { ok: true };
  const local = new Date(startedAtMs);
  const pad = (value) => String(value).padStart(2, "0");
  const localDay = `${local.getFullYear()}-${pad(local.getMonth() + 1)}-${pad(local.getDate())}`;
  const localTime = `${pad(local.getHours())}:${pad(local.getMinutes())}`;
  if (day && localDay !== day) {
    return { ok: false, reason: `the recording Zoom handed over started ${localDay} ${localTime} local time, not on ${day}.` };
  }
  if (startTime && !isWithinSession(localTime, startTime)) {
    return { ok: false, reason: `the recording Zoom handed over started ${localDay} ${localTime} local time, outside the ${startTime} session.` };
  }
  return { ok: true };
}

const iso = (ms) => (ms === null ? null : new Date(ms).toISOString());

async function isVisibleWithin(locator, timeout) {
  try {
    await locator.waitFor({ state: "visible", timeout });
    return true;
  } catch (error) {
    if (isTimeout(error)) return false;
    throw error;
  }
}
