import fs from "node:fs";
import { chromium } from "playwright";
import { log } from "./io.mjs";
import { profileDirectory } from "./paths.mjs";

// Signing in makes Chrome offer to save the password. That bubble floats over the page and
// eats the clicks meant for the filter and the table, so the offer is turned off outright.
export const QUIET_CHROME_SWITCHES = [
  "--disable-features=PasswordLeakDetection,PasswordLeakDetectionEnabled,AutofillServerCommunication",
  "--disable-save-password-bubble",
  "--disable-password-generation",
  "--password-store=basic",
  "--no-default-browser-check",
  "--no-first-run",
];

/**
 * Opens a persistent browser profile under the app's Profiles folder.
 *
 * Google Chrome is used when it is installed, so nothing has to be downloaded; otherwise
 * Playwright's own Chromium, which `npm run install-browser` fetches once.
 */
export async function launchProfile(profileName, { headless = true, args = [], env = process.env, permissions } = {}) {
  const directory = profileDirectory(profileName, env);
  fs.mkdirSync(directory, { recursive: true });

  const options = {
    headless,
    acceptDownloads: false,
    args,
    viewport: headless ? { width: 1440, height: 900 } : null,
    permissions,
  };

  const preferred = env.ZOOM_AUTO_ADMIT_BROWSER_CHANNEL ?? "chrome";
  if (preferred && preferred !== "chromium") {
    try {
      return await chromium.launchPersistentContext(directory, { ...options, channel: preferred });
    } catch (error) {
      log.info(`Browser channel '${preferred}' is not available (${firstLine(error)}); using Playwright Chromium.`);
    }
  }
  return chromium.launchPersistentContext(directory, options);
}

export async function firstPage(context, timeoutMs = 30_000) {
  const page = context.pages()[0] ?? (await context.newPage());
  page.setDefaultTimeout(timeoutMs);
  return page;
}

export function firstLine(error) {
  return String(error?.message ?? error).split("\n")[0];
}

/** True for Playwright's timeout, which callers treat as "not there" rather than a failure. */
export function isTimeout(error) {
  return error?.name === "TimeoutError";
}
