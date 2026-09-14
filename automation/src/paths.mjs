import os from "node:os";
import path from "node:path";

// Kept beside the app's own data so everything the app owns lives in one place.
// The app may pass another root (tests, a second install) through the environment.
export function supportRoot(env = process.env) {
  return (
    env.ZOOM_AUTO_ADMIT_SUPPORT_DIR ||
    path.join(os.homedir(), "Library", "Application Support", "Zoom Auto Admit")
  );
}

export const profilesRoot = (env) => path.join(supportRoot(env), "Profiles");
export const locksRoot = (env) => path.join(supportRoot(env), "Locks");
export const logsRoot = (env) => path.join(supportRoot(env), "Logs");

const SAFE_PROFILE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

export function isValidProfileName(name) {
  return typeof name === "string" && SAFE_PROFILE.test(name.trim());
}

export function profileDirectory(name, env) {
  if (!isValidProfileName(name)) throw new Error(`'${name}' is not a valid browser profile name.`);
  return path.join(profilesRoot(env), name.trim());
}
