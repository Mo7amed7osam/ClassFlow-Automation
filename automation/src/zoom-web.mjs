import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { firstPage, launchProfile } from "./browser.mjs";
import { emit, log } from "./io.mjs";

// Runs a Zoom Web meeting in an app-owned browser profile and admits the waiting room there.
//
// The admitting is not rewritten: the web extension's own content script - labels, hover
// handling, Participants reopening, attendance capture - is injected into every Zoom frame,
// with a small stand-in for the chrome.* APIs it talks to. Its messages come back here and
// are forwarded to the app as events.

const here = path.dirname(fileURLToPath(import.meta.url));
const EXTENSION_DIR = process.env.ZOOM_AUTO_ADMIT_EXTENSION_DIR ?? path.resolve(here, "..", "..", "web-extension");

const ZOOM_HOST = /(^|\.)zoom\.us$/i;

/** https + a zoom.us host, and the join/start paths a meeting lives under. */
export function validateMeetingUrl(value) {
  let url;
  try {
    url = new URL(String(value ?? ""));
  } catch {
    throw new Error("The meeting link must be an absolute https Zoom link.");
  }
  if (url.protocol !== "https:" || !ZOOM_HOST.test(url.hostname)) throw new Error("The meeting link must be an https link on zoom.us.");
  return url;
}

/**
 * The Web Client address for a meeting link. A /j/ invite is opened in the browser client
 * directly, which skips Zoom's "open the desktop app?" page; the passcode is carried along.
 */
export function webClientUrl(value, { host = false } = {}) {
  const url = validateMeetingUrl(value);
  if (url.pathname.startsWith("/wc/")) return url.href;
  const match = /\/(?:j|s|w)\/(\d{6,15})/.exec(url.pathname);
  if (!match) return url.href;
  const target = new URL(`https://app.zoom.us/wc/${match[1]}/${host ? "start" : "join"}`);
  const pwd = url.searchParams.get("pwd");
  if (pwd) target.searchParams.set("pwd", pwd);
  return target.href;
}

function chromeShim(settings) {
  // Runs inside each Zoom frame before the content script. Everything the script sends is
  // passed to the binding; storage answers with the settings the app chose.
  return `(() => {
    if (!/(^|\\.)zoom\\.us$/i.test(location.hostname)) return;
    const settings = ${JSON.stringify(settings)};
    const listeners = [];
    const runtime = {
      id: "zoom-auto-admit-app",
      lastError: undefined,
      getManifest: () => ({ version: "app" }),
      sendMessage(message, callback) {
        const send = window.__zaaSend;
        if (typeof send !== "function") { callback && callback(null); return; }
        send(message).then((reply) => callback && callback(reply), () => callback && callback(null));
      },
      onMessage: { addListener: (listener) => listeners.push(listener) },
    };
    const storage = {
      local: { get: (defaults, callback) => callback(Object.assign({}, defaults, settings)) },
      onChanged: { addListener: () => {} },
    };
    Object.defineProperty(window, "chrome", { value: Object.assign(window.chrome || {}, { runtime, storage }), configurable: true });
    window.__zaaDispatch = (message) => new Promise((resolve) => {
      let answered = false;
      for (const listener of listeners) {
        const keepOpen = listener(message, {}, (reply) => { answered = true; resolve(reply); });
        if (keepOpen === true) return;
      }
      if (!answered) resolve(null);
    });
  })();`;
}

// One block for all three files: labels.js and meeting-source.js declare constants the content
// script reads, and block-scoped constants are only visible inside the same block.
function guarded(sources) {
  return `if (/(^|\\.)zoom\\.us$/i.test(location.hostname) && !globalThis.__zaaInjected) {\nglobalThis.__zaaInjected = true;\n${sources.join("\n;\n")}\n}`;
}

export function extensionScripts(directory = EXTENSION_DIR) {
  return ["labels.js", "meeting-source.js", "content.js"].map((name) => fs.readFileSync(path.join(directory, name), "utf8"));
}

/** The two init scripts every Zoom frame gets: the chrome.* stand-in, then the extension. */
export function injectionScripts(settings, directory = EXTENSION_DIR) {
  return [chromeShim(settings), guarded(extensionScripts(directory))];
}

/**
 * Opens the meeting and keeps admitting until `stop` resolves. `sessionId` ties the content
 * script's attendance snapshots to the app's attendance session.
 */
export async function runWebMeeting(request, stop) {
  const meetingUrl = webClientUrl(request.meetingUrl, { host: request.startAsHost !== false });
  const profile = request.profile ?? "Default";
  const sessionId = request.sessionId ?? "web-meeting";
  const settings = {
    enabled: request.autoAdmit !== false,
    preferAdmitAll: request.preferAdmitAll !== false,
    debug: false,
    extraAdmitLabels: [],
    attendanceEnabled: Boolean(request.captureAttendance),
    keepParticipantsOpen: true,
  };

  const scripts = extensionScripts();
  const context = await launchProfile(profile, { headless: Boolean(request.headless), args: ["--no-first-run", "--no-default-browser-check"] });
  let admitted = 0;
  try {
    await context.exposeBinding("__zaaSend", async ({ frame }, message) => {
      switch (message?.type) {
        case "attendanceContext":
          return { ok: true, sessionId, meetingKey: null };
        case "admitted":
          admitted += 1;
          emit("admitted", { total: admitted });
          return { ok: true };
        case "admissionAttempt":
          emit("admissionAttempt", { kind: message.kind, names: (message.events ?? []).map((event) => event.name).filter(Boolean) });
          return { ok: true };
        case "admissionConfirmed":
          emit("admissionConfirmed", { name: message.name });
          return { ok: true };
        case "attendanceSnapshot":
          emit("attendanceSnapshot", { names: message.names ?? [], capturedAt: message.capturedAt, frame: frame.url() });
          return { ok: true };
        case "waitingRoomSnapshot":
          emit("waitingRoom", { names: message.names ?? [] });
          return { ok: true };
        default:
          return { ok: true };
      }
    });
    for (const content of [chromeShim(settings), guarded(scripts)]) await context.addInitScript({ content });

    const page = await firstPage(context, 60_000);
    log.info(`[WEB] Opening the meeting in profile '${profile}'${request.headless ? " (headless)" : ""}.`);
    await page.goto(meetingUrl, { waitUntil: "domcontentloaded" });
    emit("opened", { url: page.url().split("?")[0] });

    // A signed-out profile lands on Zoom's sign-in page. Headed, the person signs in and the
    // meeting carries on; headless, it can never finish, so say so and stop.
    if (/\/signin|\/login/i.test(page.url())) {
      if (request.headless) {
        emit("signInRequired", { profile });
        return { success: false, failure: "notSignedIn", message: `The '${profile}' profile is not signed in to Zoom. Open it once with the browser visible and sign in.` };
      }
      log.warn(`[WEB] '${profile}' needs a Zoom sign-in; waiting for it in the browser window.`);
      emit("signInRequired", { profile });
    }

    const closed = new Promise((resolve) => context.on("close", resolve));
    const reason = await Promise.race([stop.then(() => "stopped"), closed.then(() => "browserClosed")]);
    log.info(`[WEB] Meeting ended (${reason}); admitted ${admitted}.`);
    return { success: true, message: `Web meeting ${reason === "stopped" ? "stopped" : "window closed"}.`, admitted, reason };
  } finally {
    await context.close().catch(() => {});
  }
}
