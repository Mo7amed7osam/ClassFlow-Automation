import fs from "node:fs";
import path from "node:path";
import { locksRoot, profilesRoot } from "./paths.mjs";

/**
 * One operation per browser profile, across every process on this Mac.
 *
 * Chromium cannot open one profile folder twice, and two automations driving the same
 * signed-in profile at once can leave it signed out. The app, the recording API and a
 * terminal run are separate processes, so the lock is a file created exclusively, holding
 * the owner's pid. A lock whose pid is gone is a leftover from a crash and is taken over.
 *
 * It also refuses a profile Chromium already holds open: Chromium keeps a SingletonLock
 * symlink ("host-pid") in the profile folder while it runs.
 */
export async function acquireProfileLock(profileName, { waitMs = 120_000, env = process.env, signal } = {}) {
  const directory = locksRoot(env);
  fs.mkdirSync(directory, { recursive: true });
  const file = path.join(directory, `profile-${profileName.trim().toLowerCase()}.lock`);
  const deadline = Date.now() + Math.max(0, waitMs);

  for (;;) {
    if (signal?.aborted) throw new Error("Cancelled while waiting for the browser profile.");
    if (tryCreate(file) && !isHeldByBrowser(profileName, env)) {
      return { release: () => removeQuietly(file) };
    }
    // Only the lock this process just created may be removed here; a live owner's never.
    if (readOwner(file) === process.pid) removeQuietly(file);
    if (Date.now() >= deadline) return null;
    await new Promise((resolve) => setTimeout(resolve, 400));
  }
}

function tryCreate(file) {
  try {
    fs.writeFileSync(file, String(process.pid), { flag: "wx" });
    return true;
  } catch (error) {
    if (error.code !== "EEXIST") throw error;
    const owner = readOwner(file);
    if (owner !== null && owner !== process.pid && !isAlive(owner)) {
      removeQuietly(file);
      return tryCreate(file);
    }
    return false;
  }
}

function readOwner(file) {
  try {
    const pid = Number.parseInt(fs.readFileSync(file, "utf8"), 10);
    return Number.isFinite(pid) ? pid : null;
  } catch {
    return null;
  }
}

export function isAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === "EPERM";
  }
}

/** Whether a running Chromium has this profile folder open right now. */
export function isHeldByBrowser(profileName, env = process.env) {
  const singleton = path.join(profilesRoot(env), profileName.trim(), "SingletonLock");
  let target;
  try {
    target = fs.readlinkSync(singleton);
  } catch {
    return false;
  }
  const pid = Number.parseInt(String(target).split("-").pop(), 10);
  return Number.isFinite(pid) && isAlive(pid);
}

function removeQuietly(file) {
  try {
    fs.unlinkSync(file);
  } catch {
    // Already gone.
  }
}
