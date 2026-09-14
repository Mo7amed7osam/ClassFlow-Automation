import assert from "node:assert/strict";
import test from "node:test";
import {
  arrivedOverPlainHttp,
  describeOutcome,
  isClientAllowed,
  isPrivate,
  keyMatches,
  parseHost,
  parseRequestBody,
  resolveOptions,
  startApiServer,
} from "../src/api-server.mjs";
import { RecordingStatus } from "../src/recordings.mjs";

const KEY = "k".repeat(32);
const DRIVE = "https://drive.google.com/file/d/1A1bzBcdEfGhIjKlMnOpQrStUv/view?usp=sharing";

test("settings refuse anything that widens who can connect", () => {
  assert.throws(() => resolveOptions({}), /No API key/);
  assert.throws(() => resolveOptions({ apiKey: "short" }), /too short/);
  assert.throws(() => resolveOptions({ apiKey: KEY, port: 80 }), /port/);
  assert.throws(() => parseHost("0.0.0.0"), /every address/);
  assert.throws(() => parseHost("example.com"), /IP address/);
  assert.throws(() => parseHost("8.8.8.8"), /not a private address/);
  assert.throws(() => parseHost("10.9.9.9", () => ["10.0.0.2"]), /not an address of this Mac/);
  assert.equal(parseHost("127.0.0.1"), null);
  assert.equal(parseHost("10.0.0.2", () => ["10.0.0.2"]), "10.0.0.2");
  assert.throws(() => resolveOptions({ apiKey: KEY, host: "10.0.0.2" }, () => ["10.0.0.2"]), /allowed clients/);
  assert.throws(() => resolveOptions({ apiKey: KEY, host: "10.0.0.2", allowedClients: "0.0.0.0/0" }, () => ["10.0.0.2"]), /everyone/);
  assert.equal(isPrivate("100.100.1.1"), true);
  assert.equal(isPrivate("fd7a:115c:a1e0::1"), true);
});

test("loopback serves only loopback; a private address only its allowed clients", () => {
  const loopback = resolveOptions({ apiKey: KEY });
  assert.equal(isClientAllowed(loopback, "127.0.0.1"), true);
  assert.equal(isClientAllowed(loopback, "::ffff:127.0.0.1"), true);
  assert.equal(isClientAllowed(loopback, "10.0.0.5"), false);
  const lan = resolveOptions({ apiKey: KEY, host: "10.0.0.2", allowedClients: "100.64.0.0/10, 10.0.0.9" }, () => ["10.0.0.2"]);
  assert.equal(isClientAllowed(lan, "100.70.1.2"), true);
  assert.equal(isClientAllowed(lan, "10.0.0.9"), true);
  assert.equal(isClientAllowed(lan, "10.0.0.10"), false);
  assert.equal(keyMatches(loopback, KEY), true);
  assert.equal(keyMatches(loopback, "wrong"), false);
});

test("a proxy that says plain HTTP is refused", () => {
  assert.equal(arrivedOverPlainHttp("http", undefined), true);
  assert.equal(arrivedOverPlainHttp("https, http", undefined), false);
  assert.equal(arrivedOverPlainHttp(undefined, 'for=1.2.3.4;proto="http"'), true);
  assert.equal(arrivedOverPlainHttp(undefined, undefined), false);
});

test("request bodies are parsed strictly", () => {
  assert.deepEqual(parseRequestBody(JSON.stringify({ group: "AST5_DAT1_S1", recordLink: DRIVE, date: "2026-09-03" })).request, {
    group: "AST5_DAT1_S1",
    recordLink: DRIVE,
    day: "2026-09-03",
    startTime: null,
    replaceExisting: false,
    dryRun: false,
    headed: false,
  });
  assert.match(parseRequestBody(JSON.stringify({ group: "G", url: DRIVE })).error, /recordLink/);
  assert.match(parseRequestBody(JSON.stringify({ group: "G", recordLink: DRIVE, date: "2026-02-30" })).error, /yyyy-MM-dd/);
  assert.match(parseRequestBody(JSON.stringify({ group: "G", recordLink: DRIVE, date: "" })).error, /empty/);
  assert.match(parseRequestBody(JSON.stringify({ group: "G", recordLink: "https://zoom.us/rec/share/x" })).error, /Google Drive/);
  assert.match(parseRequestBody("[]").error, /JSON object/);
});

test("outcomes map to the documented status codes", () => {
  const base = { group: "G", date: "2026-09-03", success: true };
  assert.equal(describeOutcome({ ...base, status: RecordingStatus.attached })[0], 200);
  assert.equal(describeOutcome({ ...base, status: RecordingStatus.alreadyExists })[1].alreadyExists, true);
  assert.equal(describeOutcome({ ...base, success: false, status: RecordingStatus.recordingNotFound })[0], 404);
  assert.equal(describeOutcome({ ...base, success: false, status: RecordingStatus.busy })[0], 409);
  assert.equal(describeOutcome({ ...base, success: false, status: RecordingStatus.lmsFailed, reason: "lmsNotSignedIn" })[1].reason, "lmsNotSignedIn");
});

test("the server authenticates before reading the body", async (t) => {
  const port = 47000 + Math.floor(Math.random() * 800);
  const calls = [];
  const server = await startApiServer(resolveOptions({ apiKey: KEY, port }), async (request) => {
    calls.push(request);
    return { group: request.group, date: request.day ?? "2026-09-03", success: true, status: RecordingStatus.attached };
  });
  t.after(() => server.close());
  const url = `http://127.0.0.1:${port}`;

  assert.equal((await fetch(`${url}/health`)).status, 200);
  assert.equal((await fetch(`${url}/api/recordings/process`, { method: "POST", body: "{}" })).status, 401);
  const bad = await fetch(`${url}/api/recordings/process`, { method: "POST", headers: { "X-API-Key": KEY }, body: "{}" });
  assert.equal(bad.status, 400);
  const good = await fetch(`${url}/api/recordings/process`, {
    method: "POST",
    headers: { "X-API-Key": KEY, "Content-Type": "application/json" },
    body: JSON.stringify({ group: "G1", recordLink: DRIVE }),
  });
  assert.equal(good.status, 200);
  assert.equal((await good.json()).message, "Recording link attached successfully.");
  assert.equal(calls.length, 1);
  const plain = await fetch(`${url}/api/recordings/process`, { method: "POST", headers: { "X-API-Key": KEY, "X-Forwarded-Proto": "http" }, body: "{}" });
  assert.equal(plain.status, 403);
});
