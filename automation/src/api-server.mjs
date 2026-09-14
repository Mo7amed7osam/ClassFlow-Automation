import crypto from "node:crypto";
import http from "node:http";
import net from "node:net";
import os from "node:os";
import { log } from "./io.mjs";
import { isValidProfileName } from "./paths.mjs";
import { MAXIMUM_LINK_LENGTH, isGoogleDriveFileLink, previewLink } from "./recording-links.mjs";
import { RecordingStatus } from "./recordings.mjs";

// A small HTTP endpoint on this Mac that puts a recording link on a DEPI dashboard session.
//
//   GET  /health                   {"status":"ok"} - no key
//   POST /api/recordings/process   X-API-Key required; { group, recordLink, date?, startTime?, replaceExisting? }
//
// The link is the Google Drive link n8n read from the recordings sheet, written exactly as
// sent. Loopback by default, so it is reached from elsewhere only through a tunnel on this
// Mac. No CORS headers: the callers are servers, not browsers.

export const PROCESS_PATH = "/api/recordings/process";
export const HEALTH_PATH = "/health";
export const DEFAULT_PORT = 47821;
export const MINIMUM_KEY_LENGTH = 20;
const MAXIMUM_BODY_BYTES = 16 * 1024;

const hash = (key) => crypto.createHash("sha256").update(key, "utf8").digest();

/**
 * Validates the listening settings. Anything that would widen who can reach the API is refused:
 * every-address binds, host names, public addresses, addresses this Mac does not have.
 */
export function resolveOptions(settings = {}, localAddresses = interfaceAddresses) {
  const key = String(settings.apiKey ?? "").trim();
  if (!key) throw new Error("No API key is set. The recording API will not run without one.");
  if (key.length < MINIMUM_KEY_LENGTH) throw new Error(`The API key is too short. Use at least ${MINIMUM_KEY_LENGTH} random characters.`);

  const port = settings.port === undefined || settings.port === null ? DEFAULT_PORT : Number(settings.port);
  if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error("The port must be a number between 1024 and 65535.");

  const lockWaitSeconds = settings.lockWaitSeconds ?? 120;
  if (!Number.isInteger(lockWaitSeconds) || lockWaitSeconds < 0 || lockWaitSeconds > 1800) {
    throw new Error("The lock wait must be a number of seconds between 0 and 1800.");
  }

  const privateAddress = parseHost(settings.host, localAddresses);
  let allowedClients = [];
  if (privateAddress) {
    allowedClients = parseAllowedClients(settings.allowedClients);
    if (allowedClients.length === 0) {
      throw new Error("A network address is set, so the allowed clients must list who may connect (for example 100.64.0.0/10 for a Tailscale tailnet).");
    }
  }
  return { keyHash: hash(key), port, lockWaitMs: lockWaitSeconds * 1000, privateAddress, allowedClients };
}

export function parseHost(value, localAddresses = interfaceAddresses) {
  const host = String(value ?? "").trim();
  if (host === "" || host.toLowerCase() === "localhost") return null;
  if (["*", "+", "0.0.0.0", "::", "[::]"].includes(host)) {
    throw new Error(`Host ${host} would listen on every address of this Mac. Keep 127.0.0.1 and put a tunnel in front, or name one private address.`);
  }
  const literal = host.startsWith("[") && host.endsWith("]") ? host.slice(1, -1) : host;
  if (net.isIP(literal) === 0) throw new Error("The host must be an IP address (or localhost). Host names are refused.");
  const address = unmap(literal);
  if (isLoopback(address)) return null;
  if (!isPrivate(address)) throw new Error(`Host ${host} is not a private address. The API is never put on a public address.`);
  if (!localAddresses().map(unmap).includes(address)) throw new Error(`Host ${host} is not an address of this Mac.`);
  return address;
}

export function parseAllowedClients(value) {
  const list = new net.BlockList();
  const entries = String(value ?? "").split(/[,; ]+/).filter(Boolean);
  const parsed = [];
  for (const entry of entries) {
    const [address, prefixText] = entry.split("/");
    const family = net.isIP(address);
    if (family === 0) throw new Error(`Allowed clients: '${entry}' is not an address or a range like 10.0.0.0/24.`);
    const type = family === 4 ? "ipv4" : "ipv6";
    if (prefixText === undefined) {
      list.addAddress(unmap(address), type);
    } else {
      const prefix = Number(prefixText);
      if (!Number.isInteger(prefix) || prefix < 0 || prefix > (family === 4 ? 32 : 128)) {
        throw new Error(`Allowed clients: '${entry}' is not an address or a range like 10.0.0.0/24.`);
      }
      if (prefix === 0) throw new Error(`Allowed clients: '${entry}' allows everyone, which is refused.`);
      list.addSubnet(address, prefix, type);
    }
    parsed.push(entry);
  }
  return parsed.length === 0 ? [] : [{ list, entries: parsed }];
}

export function isPrivate(address) {
  if (net.isIPv4(address)) {
    const [a, b] = address.split(".").map(Number);
    return a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || (a === 100 && b >= 64 && b <= 127);
  }
  if (net.isIPv6(address)) return (Number.parseInt(address.split(":")[0] || "0", 16) & 0xfe00) === 0xfc00;
  return false;
}

const isLoopback = (address) => address === "::1" || (net.isIPv4(address) && address.startsWith("127."));
const unmap = (address) => (address.toLowerCase().startsWith("::ffff:") && net.isIPv4(address.slice(7)) ? address.slice(7) : address);

function interfaceAddresses() {
  return Object.values(os.networkInterfaces()).flat().filter(Boolean).map((entry) => entry.address);
}

export function isClientAllowed(options, remoteAddress) {
  const remote = unmap(String(remoteAddress ?? ""));
  if (!options.privateAddress) return isLoopback(remote);
  const family = net.isIPv4(remote) ? "ipv4" : "ipv6";
  return options.allowedClients.some((entry) => entry.list.check(remote, family));
}

export function keyMatches(options, presented) {
  if (!presented) return false;
  return crypto.timingSafeEqual(hash(String(presented).trim()), options.keyHash);
}

/** Whether a proxy in front says the caller used plain HTTP. */
export function arrivedOverPlainHttp(forwardedProto, forwarded) {
  const first = String(forwardedProto ?? "").split(",")[0].trim().toLowerCase();
  if (first === "http") return true;
  const element = String(forwarded ?? "").split(",")[0];
  return element
    .split(";")
    .map((pair) => pair.trim().toLowerCase())
    .some((pair) => pair.startsWith("proto=") && pair.slice(6).replace(/["\s]/g, "") === "http");
}

const ALLOWED_FIELDS = ["group", "recordLink", "date", "startTime", "replaceExisting", "profile", "dryRun", "headed"];
const LINK_ALIASES = ["link", "url", "driveurl", "googledriveurl", "recordingurl", "recordinglink", "sharelink"];
const SAFE_GROUP = /^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$/;

/** Strict request parsing: an unknown field is refused rather than ignored. */
export function parseRequestBody(body) {
  let root;
  try {
    root = JSON.parse(body);
  } catch {
    return { error: "The body must be a JSON object." };
  }
  if (root === null || typeof root !== "object" || Array.isArray(root)) return { error: "The body must be a JSON object." };

  for (const name of Object.keys(root)) {
    if (ALLOWED_FIELDS.includes(name)) continue;
    if (LINK_ALIASES.includes(name.toLowerCase())) return { error: `'${name}' is not a known field: send the recording's Google Drive link as 'recordLink'.` };
    return { error: `'${name}' is not a known field. Allowed: ${ALLOWED_FIELDS.join(", ")}.` };
  }

  const string = (name) => {
    const value = root[name];
    if (value === undefined || value === null) return { value: null };
    if (typeof value !== "string") return { error: `'${name}' must be a string.` };
    if (value.trim().length === 0 && name !== "group" && name !== "recordLink") {
      return { error: `'${name}' is empty. Leave it out, or send null, to use the default.` };
    }
    return { value };
  };
  const bool = (name) => {
    const value = root[name];
    if (value === undefined || value === null) return { value: false };
    if (typeof value !== "boolean") return { error: `'${name}' must be true or false.` };
    return { value };
  };

  const group = string("group");
  if (group.error) return group;
  const groupName = group.value?.trim();
  if (!groupName) return { error: "'group' is required." };
  if (!SAFE_GROUP.test(groupName)) return { error: "'group' may contain only letters, digits, spaces, '_', '-' and '.', up to 100 characters." };

  const link = string("recordLink");
  if (link.error) return link;
  const recordLink = link.value?.trim();
  if (!recordLink) return { error: "'recordLink' is required: the recording's Google Drive link." };
  const linkError = checkDriveLink(recordLink);
  if (linkError) return { error: linkError };

  const date = string("date");
  if (date.error) return date;
  if (date.value !== null && !isRealDate(date.value.trim())) return { error: "'date' must be a date in the form yyyy-MM-dd." };

  const time = string("startTime");
  if (time.error) return time;
  if (time.value !== null && !/^([01]\d|2[0-3]):[0-5]\d$/.test(time.value.trim())) return { error: "'startTime' must be a 24-hour local time in the form HH:mm." };

  const profile = string("profile");
  if (profile.error) return profile;
  if (profile.value !== null && profile.value.trim().toLowerCase() !== "default" && !isValidProfileName(profile.value)) {
    return { error: "'profile' must be \"default\" or a browser profile name (letters, digits, '.', '_', '-')." };
  }

  const flags = {};
  for (const name of ["replaceExisting", "dryRun", "headed"]) {
    const parsed = bool(name);
    if (parsed.error) return parsed;
    flags[name] = parsed.value;
  }

  return {
    request: {
      group: groupName,
      recordLink,
      day: date.value?.trim() ?? null,
      startTime: time.value?.trim() ?? null,
      ...flags,
    },
  };
}

function isRealDate(text) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(text)) return false;
  const parsed = new Date(`${text}T00:00:00Z`);
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().slice(0, 10) === text;
}

export function checkDriveLink(link) {
  if (link.length > MAXIMUM_LINK_LENGTH) return `'recordLink' is longer than ${MAXIMUM_LINK_LENGTH} characters.`;
  if (/[\s\p{Cc}]/u.test(link)) return "'recordLink' contains spaces or control characters.";
  if (/^file:/i.test(link) || link.startsWith("/") || link.startsWith("~")) return "'recordLink' is a file path, not a link. Send the recording's Google Drive link.";
  let url;
  try {
    url = new URL(link);
  } catch {
    return "'recordLink' is not a valid URL.";
  }
  if (url.protocol !== "https:") return "'recordLink' must use https.";
  if (!isGoogleDriveFileLink(link)) return "'recordLink' must be a Google Drive link to one file, like https://drive.google.com/file/d/<file id>/view?usp=sharing.";
  return null;
}

/** The HTTP answer for an outcome, kept apart from the server so the contract is testable. */
export function describeOutcome(outcome) {
  const body = { success: outcome.success, group: outcome.group, date: outcome.date };
  if (outcome.startTime) body.startTime = outcome.startTime;
  if (outcome.recordingStartedAtUtc) body.recordingStartedAtUtc = outcome.recordingStartedAtUtc;
  if (outcome.recordingDuration) body.recordingDuration = outcome.recordingDuration;
  switch (outcome.status) {
    case RecordingStatus.attached:
      return [200, { ...body, message: "Recording link attached successfully.", alreadyExists: false }];
    case RecordingStatus.alreadyExists:
      return [200, { ...body, message: outcome.message, alreadyExists: true }];
    case RecordingStatus.dryRun:
      return [200, { ...body, message: outcome.message, alreadyExists: false, dryRun: true }];
    case RecordingStatus.recordingNotFound:
      return [404, { ...body, error: "Recording not found", reason: outcome.reason, message: outcome.message }];
    case RecordingStatus.busy:
      return [409, { ...body, error: "Busy", message: outcome.message }];
    case RecordingStatus.zoomFailed:
      return [500, { ...body, error: "Zoom operation failed", reason: outcome.reason, message: outcome.message }];
    default:
      return [500, { ...body, error: "LMS operation failed", reason: outcome.reason, message: outcome.message }];
  }
}

/**
 * Starts listening. `processor(request)` does the work; requests are handled one at a time per
 * profile by the profile locks it takes. Resolves with { close } once every listener is up.
 */
export async function startApiServer(options, processor) {
  const inFlight = new Set();
  const handler = (req, res) => {
    const task = handle(req, res, options, processor).catch(() => {});
    inFlight.add(task);
    task.finally(() => inFlight.delete(task));
  };

  const hosts = options.privateAddress ? [options.privateAddress] : ["127.0.0.1", "::1"];
  const servers = [];
  for (const host of hosts) {
    const server = http.createServer(handler);
    server.requestTimeout = 0;
    await new Promise((resolve, reject) => {
      server.once("error", (error) => {
        // A Mac with IPv6 switched off has no ::1; loopback over IPv4 still serves.
        if (host === "::1" && (error.code === "EADDRNOTAVAIL" || error.code === "EAFNOSUPPORT")) resolve();
        else reject(error);
      });
      server.listen(options.port, host, () => {
        servers.push(server);
        resolve();
      });
    });
  }
  const where = options.privateAddress
    ? `http://${options.privateAddress}:${options.port} (private address; clients allowed: ${options.allowedClients.flatMap((entry) => entry.entries).join(", ")})`
    : `http://127.0.0.1:${options.port} and http://[::1]:${options.port} (loopback only)`;
  log.success(`[API] Recording API started on ${where}.`);

  return {
    async close() {
      await Promise.all(servers.map((server) => new Promise((resolve) => server.close(resolve))));
      if (inFlight.size > 0) {
        log.info(`[API] Waiting for ${inFlight.size} request(s) to finish before stopping.`);
        await Promise.race([Promise.all(inFlight), new Promise((resolve) => setTimeout(resolve, 90_000))]);
      }
      log.info("[API] Recording API stopped.");
    },
  };
}

async function handle(req, res, options, processor) {
  const started = Date.now();
  const path = (new URL(req.url ?? "/", "http://localhost").pathname.replace(/\/+$/, "") || "/");
  const method = req.method ?? "GET";
  let status = 500;
  try {
    if (!isClientAllowed(options, req.socket.remoteAddress)) {
      status = send(res, 403, { success: false, error: "Forbidden" });
      return;
    }
    if (arrivedOverPlainHttp(req.headers["x-forwarded-proto"], req.headers.forwarded)) {
      status = send(res, 403, { success: false, error: "HTTPS required" });
      return;
    }
    if (path.toLowerCase() === HEALTH_PATH) {
      status = method === "GET" || method === "HEAD" ? send(res, 200, { status: "ok" }) : send(res, 405, { success: false, error: "Method not allowed" }, { Allow: "GET" });
      return;
    }
    if (path.toLowerCase() !== PROCESS_PATH) {
      status = send(res, 404, { success: false, error: "Not found" });
      return;
    }
    if (method !== "POST") {
      status = send(res, 405, { success: false, error: "Method not allowed" }, { Allow: "POST" });
      return;
    }
    // The key before the body: an unauthenticated caller learns nothing about the format.
    if (!keyMatches(options, req.headers["x-api-key"])) {
      status = send(res, 401, { success: false, error: "Unauthorized" });
      return;
    }
    const body = await readBody(req);
    if (body === null) {
      status = send(res, 400, { success: false, error: "Invalid request", details: `The body must be JSON of at most ${MAXIMUM_BODY_BYTES / 1024} KB.` });
      return;
    }
    const parsed = parseRequestBody(body);
    if (parsed.error) {
      status = send(res, 400, { success: false, error: "Invalid request", details: parsed.error });
      return;
    }
    const request = parsed.request;
    log.info(`[API] Request: group=${request.group} date=${request.day ?? "(today)"} recordLink=${previewLink(request.recordLink)}${request.startTime ? ` startTime=${request.startTime}` : ""} replaceExisting=${request.replaceExisting} dryRun=${request.dryRun}`);
    const outcome = await processor({ ...request, lockWaitMs: options.lockWaitMs });
    const [code, response] = describeOutcome(outcome);
    status = send(res, code, response);
  } catch (error) {
    log.warn(`[API] Unexpected ${error?.name ?? "Error"} while handling ${method} ${path}.`);
    if (!res.headersSent) status = send(res, 500, { success: false, error: "Internal error" });
  } finally {
    const forwarded = String(req.headers["cf-connecting-ip"] ?? req.headers["x-forwarded-for"] ?? "").split(",")[0].trim().replace(/[^0-9a-fA-F.:]/g, "").slice(0, 45);
    const from = unmap(String(req.socket.remoteAddress ?? "?"));
    log.info(`[API] ${method} ${path} -> ${status} in ${Date.now() - started} ms (from ${from}${forwarded && forwarded !== from ? ` via ${forwarded}` : ""})`);
  }
}

function readBody(req) {
  return new Promise((resolve) => {
    const declared = Number(req.headers["content-length"] ?? 0);
    if (declared > MAXIMUM_BODY_BYTES) {
      req.resume();
      resolve(null);
      return;
    }
    const chunks = [];
    let size = 0;
    let tooBig = false;
    req.on("data", (chunk) => {
      size += chunk.length;
      if (size > MAXIMUM_BODY_BYTES) tooBig = true;
      else chunks.push(chunk);
    });
    req.on("end", () => resolve(tooBig ? null : Buffer.concat(chunks).toString("utf8")));
    req.on("error", () => resolve(null));
  });
}

function send(res, status, body, headers = {}) {
  const payload = Buffer.from(JSON.stringify(body), "utf8");
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": payload.length,
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
    ...headers,
  });
  res.end(payload);
  return status;
}
