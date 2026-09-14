import { QUIET_CHROME_SWITCHES, firstLine, firstPage, isTimeout, launchProfile } from "./browser.mjs";
import { log } from "./io.mjs";
import { buildAttendancePlan, crossGroupSuspicion } from "./plan.mjs";
import { driveFileIdOf, isGoogleDriveFileLink, isZoomShareLink, previewLink } from "./recording-links.mjs";

// Drives the DEPI dashboard the way a coordinator does by hand: sign in, open the day's
// sessions, open the group's session, and press one button on that session's own page.
// Ported from the Windows LmsSessionRunner; the steps, waits and refusals are the same.

export const LOGIN_URL = "https://dashboard.depi.eyouthbusiness.com/auth/login";
export const SESSIONS_URL = "https://dashboard.depi.eyouthbusiness.com/group_admin/sessions";
export const DASHBOARD_PROFILE = "lms-dashboard";
const STEP_TIMEOUT = 30_000;
const DIALOG = "[data-slot='dialog-content'][role='dialog']";
const CONTROLS = "button[role='checkbox'], button[role='switch'], input[type='checkbox']";

/** Failure kinds a caller can answer differently to. */
export const LmsFailure = Object.freeze({
  none: "none",
  notSignedIn: "notSignedIn",
  sessionNotFound: "sessionNotFound",
  sessionNotFinished: "sessionNotFinished",
  invalidLink: "invalidLink",
  failed: "failed",
});

const ok = (message, extra = {}) => ({ success: true, message, failure: LmsFailure.none, ...extra });
const fail = (failure, message, extra = {}) => ({ success: false, message, failure, ...extra });

/**
 * Opens the dashboard profile, signs in, opens the session and hands the page to `work`.
 * Every public operation goes through here so each one signs in, retries and cleans up alike.
 */
async function withSession(request, verb, work) {
  const group = String(request.group ?? "").trim();
  if (!group) return fail(LmsFailure.failed, "No group was given.");
  const account = request.credentials;
  if (!account?.email || !account?.password) {
    return fail(LmsFailure.notSignedIn, `No LMS sign-in is saved. Add it in the app before ${verb}.`);
  }

  const date = request.day ?? localIsoDate(new Date());
  const context = await launchProfile(DASHBOARD_PROFILE, {
    headless: !request.headed,
    args: QUIET_CHROME_SWITCHES,
  });
  const state = { step: "opening the sign-in page" };
  try {
    const page = await firstPage(context, STEP_TIMEOUT);
    await signIn(page, account);
    state.step = "opening the session";
    const opened = await openSession(page, group, date, request.startTime ?? null);
    if (!opened.isOpen) {
      return fail(opened.notListed ? LmsFailure.sessionNotFound : LmsFailure.failed, opened.reason, { opened: false });
    }
    return await work(page, { group, date, state });
  } catch (error) {
    // The step name is safe to show; the error text is not, because it can quote a value
    // that was typed into a field.
    log.warn(`[LMS] Failed while ${state.step}: ${error?.name ?? "Error"}.`);
    if (error?.userFacing) return fail(LmsFailure.failed, error.message);
    return fail(LmsFailure.failed, `The dashboard did not respond while ${state.step}.`);
  } finally {
    if (!(request.headed && request.keepBrowserOpen)) {
      await context.close().catch(() => {});
    }
  }
}

/** Presses "Run Session" for the class that is starting. */
export function runSession(request) {
  return withSession(request, "starting a session", async (page, { group, date, state }) => {
    state.step = "pressing Run Session";
    const run = page.getByRole("button", { name: /^\s*Run Session\s*$/i }).first();
    // The session page draws its actions after the row is opened, so the button is waited for.
    if (!(await waitVisible(run, 20_000))) {
      const status = await readStatus(page);
      return fail(
        LmsFailure.failed,
        `The session page for ${group} has no Run Session button${status ? `; it reads "${status}"` : ""}. Nothing was pressed.`,
        { status }
      );
    }
    if (request.dryRun) return ok(`${group}: the session page is open and offers Run Session. Nothing was pressed.`);

    await run.click();
    const toast = await readToast(page);
    await settle(page);
    if (toast) log.success(`[LMS] The dashboard said: ${toast}`);
    log.success(`[LMS] Run Session pressed for ${group} on ${date}.`);

    // The button goes away once the session is running, so it still being there means the
    // dashboard refused the press.
    const stillOffered = await run.isVisible().catch(() => false);
    const finalState = await readStatus(page);
    if (stillOffered) {
      return fail(
        LmsFailure.failed,
        `${group}: Run Session was pressed but the session still offers it${finalState ? ` and reads "${finalState}"` : ""}.`
      );
    }
    return ok(`${group}: the session is now running on the dashboard${finalState ? ` (${finalState})` : ""}.`, {
      status: finalState,
      toast,
    });
  });
}

/**
 * Moves a Google Drive recording link onto the one session of a group on a date.
 *
 * Matching is Group Code + Date and nothing else: exactly one session must be listed. None keeps
 * the row pending; more than one is ambiguous and nothing is opened. On the session page the
 * current record link is read first - empty gets the Drive link, the same Drive link is already
 * done, anything else is a conflict that is never overwritten. A write is confirmed by reloading
 * the page and reading the link back.
 */
export async function syncRecordLink(request) {
  const group = String(request.group ?? "").trim();
  const day = String(request.day ?? "").trim();
  const driveUrl = String(request.driveUrl ?? "").trim();
  if (!group || !/^\d{4}-\d{2}-\d{2}$/.test(day)) return fail(LmsFailure.failed, "A group and a yyyy-MM-dd date are required.", { issue: "invalidRequest" });
  if (!isGoogleDriveFileLink(driveUrl)) {
    return fail(LmsFailure.invalidLink, "That is not a Google Drive file link, so nothing was opened.", { issue: "invalidLink" });
  }
  const account = request.credentials;
  if (!account?.email || !account?.password) {
    return fail(LmsFailure.notSignedIn, "No LMS sign-in is saved. Add it in the app first.", { issue: "notSignedIn" });
  }

  const context = await launchProfile(DASHBOARD_PROFILE, { headless: !request.headed, args: QUIET_CHROME_SWITCHES });
  const state = { step: "opening the sign-in page" };
  try {
    const page = await firstPage(context, STEP_TIMEOUT);
    await signIn(page, account);

    state.step = "finding the session";
    let found = { matches: [], listed: [] };
    for (let attempt = 1; attempt <= 2; attempt += 1) {
      await openDaysSessions(page, day);
      found = await listGroupSessions(page, group);
      if (found.matches.length > 0) break;
      if (attempt === 1) await page.waitForTimeout(2500);
    }
    if (found.matches.length === 0) {
      for (const entry of found.listed.slice(0, 12)) log.info(`[LMS] Listed that day: ${entry}`);
      return fail(LmsFailure.sessionNotFound, `No session for ${group} is listed on ${day}. It stays pending and is tried again next sync.`, { issue: "noSession" });
    }
    if (found.matches.length > 1) {
      return fail(LmsFailure.failed, `${found.matches.length} sessions for ${group} are listed on ${day}; the recording is not attached to any of them. Review it by hand.`, {
        issue: "ambiguousSessions",
        sessions: found.matches.map((match) => match.summary),
      });
    }

    state.step = "opening the session";
    if (!(await openSessionPage(page, found.matches[0].link, group))) {
      return fail(LmsFailure.failed, `The session for ${group} on ${day} did not open.`, { issue: "sessionDidNotOpen" });
    }
    const lmsSessionUrl = page.url();
    const status = await readStatus(page);

    state.step = "reading the record link";
    const current = await readRecordLink(page, state);
    // What the session held before anything was done, reported on every answer from here on.
    const before = { currentLink: current.value, recordLinkState: current.state };
    if (current.state === "unavailable") {
      return fail(LmsFailure.sessionNotFinished, `The ${group} session on ${day} offers no record link yet${status ? ` (it reads "${status}")` : ""}. It stays pending until the session is ended.`, {
        issue: "sessionNotEnded",
        lmsSessionUrl,
        status,
        ...before,
      });
    }
    const decision = decideRecordLinkUpdate(current.value, driveUrl, { replaceZoomRecordingLinks: request.replaceZoomRecordingLinks === true });
    log.info(`[LMS] ${group} ${day}: current record link ${current.value ? previewLink(current.value) : "(empty)"} → ${decision}.`);
    if (decision === "same") {
      return ok(`${group} ${day}: the session already carries this Drive link.`, { outcome: "alreadyAttached", lmsSessionUrl, status, ...before });
    }
    if (decision === "conflict") {
      return fail(LmsFailure.failed, `${group} ${day}: the session already has a different record link (${previewLink(current.value)}). It was not overwritten.`, {
        issue: "existingLinkDiffers",
        lmsSessionUrl,
        status,
        ...before,
      });
    }
    const replacing = decision === "replaceZoom";
    if (request.dryRun) {
      const what = replacing ? `the Zoom recording link ${previewLink(current.value)} would be replaced by ${previewLink(driveUrl)}` : `the record link is empty and would get ${previewLink(driveUrl)}`;
      return ok(`${group} ${day}: ${what}. Nothing was saved (rehearse).`, { outcome: replacing ? "wouldReplaceZoom" : "wouldAttach", dryRun: true, lmsSessionUrl, status, ...before });
    }

    state.step = "saving the record link";
    const add = page.getByRole("button", { name: /^\s*(Add|Edit)\s+Record\s+Link\s*$/i }).filter({ visible: true }).first();
    await add.click({ timeout: 10_000 });
    const dialog = page.locator("[role='dialog']").filter({ has: page.locator("input[name='recorded_link'], input[placeholder='Enter session record link']") }).first();
    const field = dialog.locator("input[name='recorded_link'], input[placeholder='Enter session record link']").first();
    if (!(await waitVisible(field, 15_000))) return fail(LmsFailure.failed, `The record link box did not open for ${group} ${day}; nothing was saved.`, { issue: "boxDidNotOpen", lmsSessionUrl, ...before });
    // The box must still hold exactly what was read a moment ago; if someone changed it in between, stop.
    if ((await field.inputValue({ timeout: 5000 })).trim() !== current.value) {
      await page.keyboard.press("Escape");
      return fail(LmsFailure.failed, `${group} ${day}: the record link changed while it was being updated; nothing was saved.`, { issue: "existingLinkDiffers", lmsSessionUrl, ...before });
    }
    await field.fill(driveUrl);
    await dialog.getByRole("button", { name: /^\s*Save\s*$/i }).first().click({ timeout: 10_000 });
    const toast = await readToast(page);
    await settle(page);
    if (toast) log.success(`[LMS] The dashboard said: ${toast}`);
    if (await dialog.isVisible().catch(() => false)) {
      return fail(LmsFailure.failed, `${group} ${day}: Save was pressed but the record link box is still open; it was not saved.`, { issue: "saveNotAccepted", lmsSessionUrl, ...before });
    }

    state.step = "reading the record link back";
    await page.reload({ waitUntil: "networkidle" });
    const after = await readRecordLink(page, state);
    if (decideRecordLinkUpdate(after.value, driveUrl) !== "same") {
      return fail(LmsFailure.failed, `${group} ${day}: after saving, the session reads ${after.value ? previewLink(after.value) : "no link"} instead of the Drive link.`, { issue: "notConfirmed", lmsSessionUrl, ...before });
    }
    return ok(`${group} ${day}: the Drive recording link was saved${replacing ? " in place of the Zoom recording link" : ""} and confirmed after a reload.`, {
      outcome: replacing ? "replacedZoom" : "attached",
      replacedLink: replacing ? current.value : undefined,
      lmsSessionUrl,
      status,
      ...before,
    });
  } catch (error) {
    log.warn(`[LMS] Failed while ${state.step}: ${error?.name ?? "Error"}.`);
    if (error?.userFacing) return fail(LmsFailure.failed, error.message, { issue: "failed" });
    return fail(LmsFailure.failed, `The dashboard did not respond while ${state.step}.`, { issue: "failed" });
  } finally {
    await context.close().catch(() => {});
  }
}

/**
 * empty → "add"; the same Drive file (share variants of one link count as the same) → "same";
 * anything else → "conflict".
 */
export function decideRecordLinkUpdate(currentValue, driveUrl, { replaceZoomRecordingLinks = false } = {}) {
  const current = String(currentValue ?? "").trim();
  if (!current) return "add";
  if (current === driveUrl.trim()) return "same";
  const currentId = driveFileIdOf(current);
  if (currentId && currentId === driveFileIdOf(driveUrl.trim())) return "same";
  // Only a Zoom cloud recording share link may be treated as temporary, and only when allowed.
  if (replaceZoomRecordingLinks && isZoomShareLink(current)) return "replaceZoom";
  return "conflict";
}

/**
 * The listed sessions of one group, once each. The dashboard renders every session twice (a wide
 * and a narrow layout), so rows are keyed by their link, or by their text without spacing.
 * The group must appear as a whole code: CAI5_IND1_G1 never matches CAI5_IND1_G10.
 */
export function rowMentionsGroup(text, group) {
  const code = group.trim().toLowerCase().replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  if (!code) return false;
  return new RegExp(`(^|[^a-z0-9_])${code}($|[^a-z0-9_])`, "i").test(text);
}

export function distinctGroupRows(rows, group) {
  const seen = new Set();
  const matches = [];
  for (const row of rows) {
    if (!rowMentionsGroup(row.text, group)) continue;
    const key = row.href || row.text.replace(/\s+/g, "").toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    matches.push(row);
  }
  return matches;
}

async function listGroupSessions(page, group) {
  const rows = page.locator("table tbody tr");
  try {
    await rows.first().waitFor({ state: "attached", timeout: 15_000 });
  } catch (error) {
    if (isTimeout(error)) return { matches: [], listed: [] };
    throw error;
  }
  const collected = [];
  for (const row of await rows.all()) {
    let text;
    try {
      text = await row.innerText({ timeout: 3000 });
    } catch {
      continue;
    }
    const link = row.locator("td:first-child a, td:first-child button").first();
    if ((await link.count()) === 0) continue;
    const href = await link.getAttribute("href").catch(() => null);
    collected.push({ text, href, link, summary: summarise(text) });
  }
  return { matches: distinctGroupRows(collected, group), listed: [...new Set(collected.map((row) => row.summary))] };
}

/** The session's current record link: unavailable (not ended), empty, or its value. */
async function readRecordLink(page, state = {}) {
  state.step = "waiting for the record link button";
  // Only a visible button counts: the page keeps hidden copies of its controls for narrow layouts.
  const button = page.getByRole("button", { name: /^\s*(Add|Edit)\s+Record\s+Link\s*$/i }).filter({ visible: true }).first();
  if (!(await waitVisible(button, 20_000))) return { state: "unavailable", value: "" };
  const label = (await button.innerText({ timeout: 5000 })).trim();
  if (/^add/i.test(label)) return { state: "empty", value: "" };

  state.step = "opening the record link box to read it";
  await button.click({ timeout: 10_000 });
  const dialog = page.locator("[role='dialog']").filter({ has: page.locator("input[name='recorded_link'], input[placeholder='Enter session record link']") }).first();
  const field = dialog.locator("input[name='recorded_link'], input[placeholder='Enter session record link']").first();
  if (!(await waitVisible(field, 15_000))) throw userFacing("The record link box did not open, so the current link could not be read.");
  state.step = "reading the current record link";
  const value = (await field.inputValue({ timeout: 5000 })).trim();
  state.step = "closing the record link box without saving";
  // Close without saving: Escape first, then the dialog's own Close button if it is still open.
  await page.keyboard.press("Escape");
  await page.waitForTimeout(600);
  if (await dialog.isVisible().catch(() => false)) {
    const close = dialog.getByRole("button", { name: /^\s*(Close|Cancel)\s*$/i }).first();
    if ((await close.count()) > 0) await close.click({ timeout: 5000 });
    await page.waitForTimeout(600);
  }
  if (await dialog.isVisible().catch(() => false)) throw userFacing("The record link box would not close without saving; nothing was changed.");
  return { state: value ? "filled" : "empty", value };
}

/**
 * Takes the session's attendance. Every row is decided before anything is ticked, and if
 * any row cannot be ticked nothing is submitted: a half-filled sheet reads as a class where
 * half the students were absent.
 */
export function takeAttendance(request) {
  const present = Array.isArray(request.present) ? request.present : [];
  return withSession(request, "taking attendance", async (page, { group, state }) => {
    state.step = "opening Take Session Attendance";
    const take = page.getByRole("button", { name: /Take\s+Session\s+Attendance/i }).first();
    if (!(await waitVisible(take, 20_000))) {
      const status = await readStatus(page);
      log.info(`[LMS] The session page offers: ${await listActions(page)}`);
      // Only View details proves the attendance was taken. Without it the session is simply not
      // ready (not running yet, or a slow page) and the step must be tried again, not dropped.
      const details = page.getByRole("button", { name: /^\s*View\s+details\s*$/i }).filter({ visible: true }).first();
      const alreadyTaken = (await details.count().catch(() => 0)) > 0;
      return fail(
        LmsFailure.failed,
        `The session page for ${group} offers no Take Session Attendance${status ? `; it reads "${status}"` : ""}. ` +
          (alreadyTaken ? "Attendance was already taken." : "It will be tried again."),
        { alreadyTaken }
      );
    }
    await take.click();

    state.step = "reading the attendance list";
    const dialog = page.locator(DIALOG).first();
    if (!(await waitVisible(dialog, 20_000))) return fail(LmsFailure.failed, `The attendance list did not open for ${group}.`);
    const rows = await readAttendanceRows(dialog);
    if (rows.length === 0) {
      const shown = (await dialog.innerText().catch(() => "")).replace(/\s*\n\s*/g, " | ");
      log.info(`[LMS] The dialog reads: ${shown.length > 600 ? `${shown.slice(0, 600)}...` : shown}`);
      return fail(LmsFailure.failed, `The attendance list for ${group} opened but listed no students.`);
    }

    const plan = buildAttendancePlan(rows.map((row) => row.studentName), present, Boolean(request.everyone));
    log.info(`[LMS] ${group}: ${plan.summary}.`);
    const suspicious = crossGroupSuspicion(plan, present.length);
    if (suspicious) return fail(LmsFailure.failed, `${group}: ${suspicious}`, { plan, crossGroup: true });
    if (request.dryRun) return ok(`${group}: ${plan.summary}. Nothing was ticked.`, { plan, dryRun: true });

    state.step = "ticking the attendance list";
    for (let index = 0; index < rows.length; index += 1) {
      const row = rows[index];
      const wanted = plan.marks[index].joined;
      if (row.singleControl) {
        // One switch per student: set it to the wanted state rather than toggling blindly.
        if (!row.joined) return fail(LmsFailure.failed, `${group}: ${row.studentName} has no box to tick, so nothing was submitted.`, { plan });
        if ((await isOn(row.joined)) !== wanted) {
          await row.joined.scrollIntoViewIfNeeded();
          await row.joined.click();
        }
        continue;
      }
      const box = wanted ? row.joined : row.notJoined;
      if (!box) return fail(LmsFailure.failed, `${group}: ${row.studentName} has no box to tick, so nothing was submitted.`, { plan });
      // Clicking a box that is already ticked would untick it.
      if (!(await isOn(box))) {
        await box.scrollIntoViewIfNeeded();
        await box.click();
      }
    }

    state.step = "submitting the attendance";
    await page.getByRole("button", { name: /^\s*Take\s+Attendance\s*$/i }).last().click();
    const toast = await readToast(page);
    await settle(page);
    if (toast) log.success(`[LMS] The dashboard said: ${toast}`);
    if (await dialog.isVisible().catch(() => false)) {
      return fail(LmsFailure.failed, `${group}: attendance was filled in but the dashboard did not accept it.`, { plan });
    }
    return ok(`${group}: attendance taken - ${plan.summary}.`, { plan });
  });
}

/**
 * Corrects an attendance that was already taken - the late joiner. Only rows that disagree
 * are flipped; the page is then reloaded and read again, and that fresh read is the proof.
 */
export function correctAttendance(request) {
  const present = Array.isArray(request.present) ? request.present : [];
  return withSession(request, "correcting attendance", async (page, { group, state }) => {
    state.step = "opening the attendance details";
    const before = await openDetails(page, group);
    if (!before.rows) return fail(LmsFailure.failed, before.reason, { notTakenYet: before.notTakenYet });

    const plan = buildAttendancePlan(before.rows.map((row) => row.studentName), present, Boolean(request.everyone));
    const suspicious = crossGroupSuspicion(plan, present.length);
    if (suspicious) return fail(LmsFailure.failed, `${group}: ${suspicious}`, { plan, crossGroup: true });
    const changes = [];
    for (let index = 0; index < before.rows.length; index += 1) {
      const now = await isOn(before.rows[index].joined);
      const wanted = plan.marks[index].joined;
      if (now !== wanted) changes.push({ index, name: before.rows[index].studentName, to: wanted });
    }
    if (changes.length === 0) return ok(`${group}: the dashboard already matches - nothing to change.`, { plan, changes });

    const listed = changes.map((change) => `${change.name} → ${change.to ? "Joined" : "Not-joined"}`).join(", ");
    log.info(`[LMS] ${group}: ${changes.length} row(s) differ - ${listed}`);
    if (request.dryRun) {
      return ok(`${group}: ${changes.length} row(s) would change (${listed}). Nothing was touched.`, { plan, changes, dryRun: true });
    }

    state.step = "flipping the rows that differ";
    for (const change of changes) {
      const toggle = before.rows[change.index].joined;
      if (!toggle) return fail(LmsFailure.failed, `${group}: ${change.name} has no switch to flip.`, { plan });
      await toggle.scrollIntoViewIfNeeded();
      await toggle.click();
      // Each flip saves on its own, so they are given a moment rather than fired at once.
      await page.waitForTimeout(600);
    }

    state.step = "reading the dashboard back";
    await page.reload({ waitUntil: "networkidle" });
    const after = await openDetails(page, group);
    if (!after.rows) return fail(LmsFailure.failed, `${group}: the rows were flipped but ${after.reason}`, { plan });
    // Checked by name, not by position: a reloaded list may come back in another order.
    const wantedByName = new Map(plan.marks.map((mark) => [mark.studentName.toLowerCase(), mark.joined]));
    const stubborn = [];
    for (const row of after.rows) {
      const wanted = wantedByName.get(row.studentName.toLowerCase());
      if (wanted !== undefined && (await isOn(row.joined)) !== wanted) stubborn.push(row.studentName);
    }
    if (stubborn.length > 0) {
      return fail(LmsFailure.failed, `${group}: after reloading, ${stubborn.length} row(s) still disagree (${stubborn.join(", ")}).`, { plan });
    }
    return ok(`${group}: ${changes.length} row(s) changed and confirmed after a reload (${listed}).`, { plan, changes });
  });
}

/**
 * Health check: signs in and counts the group's sessions listed on a day. Read-only - no session
 * page is opened and nothing is pressed.
 */
export async function checkSession(request) {
  const group = String(request.group ?? "").trim();
  const day = String(request.day ?? "").trim();
  const account = request.credentials;
  if (!account?.email || !account?.password) return fail(LmsFailure.notSignedIn, "No LMS sign-in is saved.", { signedIn: false });
  if (!group || !/^\d{4}-\d{2}-\d{2}$/.test(day)) return fail(LmsFailure.failed, "A group and a yyyy-MM-dd date are required.", { signedIn: false });
  const context = await launchProfile(DASHBOARD_PROFILE, { headless: true, args: QUIET_CHROME_SWITCHES });
  let signedIn = false;
  try {
    const page = await firstPage(context, STEP_TIMEOUT);
    await signIn(page, account);
    await openDaysSessions(page, day);
    signedIn = true;
    const found = await listGroupSessions(page, group);
    return ok(`${found.matches.length} session(s) for ${group} are listed on ${day}.`, { signedIn, sessions: found.matches.length });
  } catch (error) {
    const message = error?.userFacing ? error.message : `The dashboard did not answer (${error?.name ?? "Error"}).`;
    return fail(signedIn ? LmsFailure.failed : LmsFailure.notSignedIn, message, { signedIn });
  } finally {
    await context.close().catch(() => {});
  }
}

/** Signs in with the saved account, or accepts a profile whose sign-in is still valid. */
export async function verifySignIn(request) {
  const account = request.credentials;
  if (!account?.email || !account?.password) return fail(LmsFailure.notSignedIn, "No LMS sign-in was given.");
  const context = await launchProfile(DASHBOARD_PROFILE, { headless: !request.headed, args: QUIET_CHROME_SWITCHES });
  try {
    const page = await firstPage(context, STEP_TIMEOUT);
    await signIn(page, account);
    return ok(`Signed in to the dashboard as ${account.email}.`);
  } catch (error) {
    return fail(LmsFailure.notSignedIn, error?.userFacing ? error.message : `The dashboard sign-in did not complete (${error?.name ?? "Error"}).`);
  } finally {
    await context.close().catch(() => {});
  }
}

// ---------------------------------------------------------------- page steps

async function signIn(page, account) {
  await page.goto(LOGIN_URL, { waitUntil: "domcontentloaded" });
  log.info(`[LMS] Sign-in page opened: ${page.url()}`);
  // The dashboard draws its form after the page loads, so the field has to be waited for.
  const email = page.locator("input[type='email']").first();
  if (!(await waitVisible(email, 20_000))) {
    log.info("[LMS] No sign-in form; the saved session is still valid.");
    return;
  }
  await email.fill(account.email);
  await page.locator("#password, input[type='password']").first().fill(account.password);
  await page.getByRole("button", { name: /log ?in|sign ?in/i }).first().click();
  try {
    await page.waitForURL((url) => !url.href.toLowerCase().includes("/auth/login"), { timeout: 30_000 });
  } catch (error) {
    if (isTimeout(error)) throw userFacing("The dashboard stayed on the sign-in page; check the email and password.");
    throw error;
  }
  await page.waitForLoadState("networkidle").catch(() => {});
  log.info(`[LMS] Signed in; the dashboard is at ${page.url()}`);
}

/**
 * The list, the row and the session's page, tried three times. Every step is the dashboard
 * rendering after a request, and a slow render has been seen to leave the row unclicked.
 */
async function openSession(page, group, date, startTime) {
  const attempts = 3;
  let reason = `The ${group} session could not be opened.`;
  let notListed = false;
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    try {
      await openDaysSessions(page, date);
      const found = await findSessionRow(page, group, startTime);
      if (!found.link) {
        reason = found.reason;
        notListed = true;
      } else if (await openSessionPage(page, found.link, group)) {
        return { isOpen: true };
      } else {
        reason = `The row for ${group} did not open its session page.`;
        notListed = false;
      }
    } catch (error) {
      if (error?.userFacing) throw error;
      reason = `The session list did not answer (${error?.name ?? "Error"}).`;
      notListed = false;
    }
    if (attempt < attempts) {
      log.warn(`[LMS] ${reason} Trying again (${attempt + 1} of ${attempts})...`);
      await page.waitForTimeout(2500);
    }
  }
  return { isOpen: false, reason, notListed };
}

/**
 * The dashboard keeps its filter in the address, not in the form, so the day is asked for
 * through the address directly.
 */
async function openDaysSessions(page, date) {
  const url = `${SESSIONS_URL}?date_from=${date}&date_to=${date}`;
  await page.goto(url, { waitUntil: "domcontentloaded" });
  await page.waitForLoadState("networkidle").catch(() => {});
  if (page.url().toLowerCase().includes("/auth/login")) {
    throw userFacing("The dashboard asked to sign in again before the session list opened.");
  }
  await page.waitForTimeout(800);
  log.info(`[LMS] Sessions for ${date} requested: ${page.url()}`);
}

async function openSessionPage(page, link, group) {
  const listUrl = page.url();
  await link.click();
  try {
    await page.waitForURL((url) => url.href !== listUrl, { timeout: 20_000 });
  } catch (error) {
    if (isTimeout(error)) return false;
    throw error;
  }
  await page.waitForLoadState("networkidle").catch(() => {});
  log.info(`[LMS] Session page open for ${group}: ${page.url()}`);
  return true;
}

async function findSessionRow(page, group, startTime) {
  const rows = page.locator("table tbody tr");
  try {
    await rows.first().waitFor({ state: "attached", timeout: 15_000 });
  } catch (error) {
    if (isTimeout(error)) return { link: null, reason: "The filtered session list came back empty." };
    throw error;
  }

  const matches = [];
  const listed = [];
  let rowsSeen = 0;
  for (const row of await rows.all()) {
    let text;
    try {
      text = await row.innerText({ timeout: 3000 });
    } catch {
      continue;
    }
    rowsSeen += 1;
    listed.push(summarise(text));
    // A whole code only: CAI5_IND1_G1 must never open a CAI5_IND1_G10 session.
    if (!rowMentionsGroup(text, group)) continue;
    const link = row.locator("td:first-child a, td:first-child button").first();
    if ((await link.count()) === 0) continue;
    matches.push({ link, time: readRowTime(text) });
  }

  const choice = chooseSessionRow(matches, group, startTime);
  if (choice.index === null && matches.length === 0) {
    for (const entry of [...new Set(listed)].slice(0, 12)) log.info(`[LMS] Listed that day: ${entry}`);
    return { link: null, reason: `No session for ${group} is listed for that day on the dashboard (${rowsSeen} session(s) shown after filtering).` };
  }
  if (choice.index === null) return { link: null, reason: choice.reason };
  if (choice.reason) log.info(`[LMS] ${choice.reason}`);
  return { link: matches[choice.index].link, reason: "" };
}

/**
 * Which listed row is this class. Rows that agree on the time are one session rendered twice;
 * otherwise the closest listed time within half an hour wins, and anything still ambiguous
 * opens nothing.
 */
export function chooseSessionRow(matches, group, startTime) {
  if (matches.length === 0) return { index: null, reason: `No session for ${group} is listed for that day.` };
  if (matches.length === 1) return { index: 0, reason: "" };
  const distinct = new Set(matches.map((match) => match.time ?? "none"));
  if (distinct.size === 1) return { index: 0, reason: "" };
  if (!startTime) {
    return { index: null, reason: `${matches.length} sessions for ${group} are listed that day and no scheduled time was given, so none was opened.` };
  }
  const closest = matches
    .map((match, index) => ({ index, minutes: match.time ? minutesApart(match.time, startTime) : null }))
    .filter((entry) => entry.minutes !== null)
    .sort((left, right) => left.minutes - right.minutes);
  if (closest.length === 0) return { index: null, reason: `${matches.length} sessions for ${group} are listed that day and none shows a time.` };
  if (closest[0].minutes > 30) {
    return { index: null, reason: `The nearest listed session for ${group} is ${closest[0].minutes} minutes from ${startTime}; none was opened.` };
  }
  if (closest.length > 1 && closest[1].minutes === closest[0].minutes) {
    return { index: null, reason: `Two sessions for ${group} share the same time; none was opened.` };
  }
  return { index: closest[0].index, reason: `Matched the ${startTime} session for ${group}.` };
}

/** The HH:mm printed just after the row's date ("2026-09-02" then "19:00"). */
export function readRowTime(rowText) {
  const afterDate = /\d{4}-\d{2}-\d{2}[^\d]{0,3}([01]?\d|2[0-3]):([0-5]\d)/.exec(rowText);
  const match = afterDate ?? /([01]?\d|2[0-3]):([0-5]\d)/.exec(rowText);
  if (!match) return null;
  return `${match[1].padStart(2, "0")}:${match[2]}`;
}

export function minutesApart(left, right) {
  const toMinutes = (value) => {
    const [hours, minutes] = value.split(":").map(Number);
    return hours * 60 + minutes;
  };
  return Math.abs(toMinutes(left) - toMinutes(right));
}

async function openDetails(page, group) {
  const details = page.getByRole("button", { name: /^\s*View\s+details\s*$/i }).first();
  if (!(await waitVisible(details, 20_000))) {
    return { rows: null, notTakenYet: true, reason: `the session page for ${group} offers no View details, so its attendance was never taken.` };
  }
  await details.click();
  const dialog = page.locator(DIALOG).first();
  if (!(await waitVisible(dialog, 20_000))) return { rows: null, reason: `the attendance details did not open for ${group}.` };
  const rows = await readAttendanceRows(dialog);
  return rows.length === 0 ? { rows: null, reason: `the attendance details for ${group} listed no students.` } : { rows };
}

async function isOn(toggle) {
  if (!toggle) return false;
  const state = await toggle.getAttribute("aria-checked");
  if (state !== null) return String(state).toLowerCase() === "true";
  // A native checkbox carries no aria-checked.
  return toggle.isChecked().catch(() => false);
}

/**
 * Pairs each control in the dialog with the name beside it. One dialog has two checkboxes per
 * student, the other a single switch, so controls are found first and each is asked which
 * name it sits next to.
 */
async function readAttendanceRows(dialog) {
  try {
    await dialog.locator(CONTROLS).first().waitFor({ state: "visible", timeout: 25_000 });
  } catch (error) {
    if (isTimeout(error)) return [];
    throw error;
  }
  const pairs = await dialog.evaluate((root, selector) => {
    const controls = Array.from(root.querySelectorAll(selector));
    return controls.map((control, index) => {
      let node = control;
      let name = "";
      while (node && node !== root && !name) {
        let previous = node.previousElementSibling;
        while (previous && !name) {
          const text = (previous.innerText || previous.textContent || "").trim();
          if (text && !previous.querySelector(selector)) name = text;
          previous = previous.previousElementSibling;
        }
        node = node.parentElement;
      }
      return { name: name.replace(/\s+/g, " ").trim(), index };
    });
  }, CONTROLS);

  const controls = dialog.locator(CONTROLS);
  return groupControlPairs(pairs).map((row) => ({
    studentName: row.name,
    joined: controls.nth(row.joinedIndex),
    notJoined: controls.nth(row.notJoinedIndex),
    singleControl: row.joinedIndex === row.notJoinedIndex,
  }));
}

/** Consecutive controls beside the same name are one student with more than one box. */
export function groupControlPairs(pairs) {
  const rows = [];
  for (let index = 0; index < pairs.length; ) {
    const name = pairs[index].name;
    let span = 1;
    while (index + span < pairs.length && pairs[index + span].name === name) span += 1;
    if (name.length > 0) {
      rows.push({
        name,
        joinedIndex: pairs[index].index,
        notJoinedIndex: span > 1 ? pairs[index + 1].index : pairs[index].index,
      });
    }
    index += span;
  }
  return rows;
}

async function listActions(page) {
  try {
    const names = [];
    for (const action of (await page.locator("button, [role='button'], a").all()).slice(0, 80)) {
      if (!(await action.isVisible())) continue;
      const text = (await action.innerText()).trim().replace(/\s+/g, " ");
      if (text.length === 0 || text.length > 40) continue;
      if (!names.some((name) => name.toLowerCase() === text.toLowerCase())) names.push(text);
      if (names.length >= 25) break;
    }
    return names.length === 0 ? "(nothing clickable)" : names.join(" | ");
  } catch {
    return "(could not be read)";
  }
}

async function readToast(page) {
  const toast = page.locator("[data-sonner-toast], [role='status'], [role='alert'], .toast, .Toastify__toast").first();
  try {
    await toast.waitFor({ state: "visible", timeout: 6000 });
    return (await toast.innerText({ timeout: 2000 })).replace(/[\r\n]+/g, " ").trim();
  } catch {
    return "";
  }
}

async function readStatus(page) {
  try {
    const status = page.getByText(/^\s*(pending|running|finished|cancelled)\s*$/i).filter({ visible: true }).first();
    return (await status.count()) > 0 ? (await status.innerText()).trim() : "";
  } catch {
    return "";
  }
}

async function settle(page) {
  await page.waitForLoadState("networkidle").catch(() => {});
  await page.waitForTimeout(1500);
}

async function waitVisible(locator, timeout) {
  try {
    await locator.waitFor({ state: "visible", timeout });
    return true;
  } catch (error) {
    if (isTimeout(error)) return false;
    throw error;
  }
}

function summarise(rowText) {
  return rowText
    .split(/[\n\r\t]+/)
    .map((part) => part.trim())
    .filter(Boolean)
    .slice(0, 4)
    .join(" | ");
}

function userFacing(message) {
  const error = new Error(message);
  error.userFacing = true;
  return error;
}

export function localIsoDate(date) {
  const pad = (value) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

export { firstLine };
