// Which links may be written on a session as its recording. Anything the dashboard shows
// students as "the recording" must be one of these; everything else is refused before a
// browser is even opened.

export const MAXIMUM_LINK_LENGTH = 2048;
const DRIVE_FILE_PATH = /^\/file\/d\/([A-Za-z0-9_-]{20,100})(?:\/(?:view|preview|edit))?\/?$/;
const DRIVE_FILE_ID = /^[A-Za-z0-9_-]{20,100}$/;

function parseUrl(value) {
  try {
    return new URL(value);
  } catch {
    return null;
  }
}

/** A Zoom cloud recording share link, as copied from My Recordings. */
export function isZoomShareLink(value) {
  if (typeof value !== "string" || value.trim().length === 0) return false;
  const url = parseUrl(value.trim());
  return (
    url !== null &&
    url.protocol === "https:" &&
    url.hostname.toLowerCase().endsWith("zoom.us") &&
    url.pathname.toLowerCase().includes("/rec/")
  );
}

/**
 * The Drive file id a link points at, or null. Only a link to one file is accepted:
 *   https://drive.google.com/file/d/{id}[/view|/preview|/edit][?...]
 *   https://drive.google.com/open?id={id}
 */
export function driveFileIdOf(value) {
  if (typeof value !== "string" || value.length === 0 || value.length > MAXIMUM_LINK_LENGTH) return null;
  if (/[\s\p{Cc}]/u.test(value)) return null;
  const url = parseUrl(value);
  if (!url || url.protocol !== "https:" || url.hostname.toLowerCase() !== "drive.google.com") return null;
  if (url.port !== "" || url.username !== "" || url.password !== "") return null;
  const file = DRIVE_FILE_PATH.exec(url.pathname);
  if (file) return file[1];
  if (url.pathname === "/open") {
    const id = url.searchParams.get("id");
    if (id && DRIVE_FILE_ID.test(id)) return id;
  }
  return null;
}

export const isGoogleDriveFileLink = (value) => driveFileIdOf(value) !== null;

export function classifyLink(value) {
  if (isGoogleDriveFileLink(value)) return "googleDrive";
  if (isZoomShareLink(value)) return "zoomShare";
  return "none";
}

export const isAttachable = (value) => classifyLink(value) !== "none";

/** Enough of a link to recognise it in a log, not enough to open it. */
export function previewLink(value) {
  if (!value) return "(none)";
  const id = driveFileIdOf(value);
  if (id) return `drive.google.com/file/d/${id.slice(0, 6)}...`;
  return value.length <= 40 ? value : `${value.slice(0, 40)}...`;
}
