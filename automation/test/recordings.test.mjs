import assert from "node:assert/strict";
import test from "node:test";
import { classifyLink, driveFileIdOf, previewLink } from "../src/recording-links.mjs";
import { RecordingStatus, attachProvidedLink, processFromZoom } from "../src/recordings.mjs";
import { pickRecording, readDuration, readRecordedAt, readShareLinkStart, shiftEntries, startsWithinSession } from "../src/zoom-recordings.mjs";

const DRIVE = "https://drive.google.com/file/d/1A1bzBcdEfGhIjKlMnOpQrStUv/view?usp=sharing";

test("only Zoom share links and single Drive files are attachable", () => {
  assert.equal(classifyLink(DRIVE), "googleDrive");
  assert.equal(classifyLink("https://drive.google.com/open?id=1A1bzBcdEfGhIjKlMnOpQrStUv"), "googleDrive");
  assert.equal(classifyLink("https://us06web.zoom.us/rec/share/abc?startTime=1725000000000"), "zoomShare");
  assert.equal(classifyLink("https://drive.google.com/drive/folders/1A1bzBcdEfGhIjKlMnOpQrStUv"), "none");
  assert.equal(classifyLink("http://drive.google.com/file/d/1A1bzBcdEfGhIjKlMnOpQrStUv/view"), "none");
  assert.equal(classifyLink("https://drive.google.com:444/file/d/1A1bzBcdEfGhIjKlMnOpQrStUv/view"), "none");
  assert.equal(classifyLink("https://drive.google.com/uc?id=1A1bzBcdEfGhIjKlMnOpQrStUv"), "none");
  assert.equal(driveFileIdOf(`${DRIVE} `), null);
  assert.equal(previewLink(DRIVE), "drive.google.com/file/d/1A1bzB...");
});

test("recording rows are parsed from the list text", () => {
  const row = "CAI5_AIS4_S7\nSep 1, 2026 08:58 AM\n03:29:13\n1.2 GB";
  assert.equal(readRecordedAt(row), "2026-09-01T08:58");
  assert.equal(readRecordedAt("Sep 1, 2026 12:05 PM"), "2026-09-01T12:05");
  assert.equal(readRecordedAt("Sep 1, 2026 12:05 AM"), "2026-09-01T00:05");
  assert.equal(readDuration(row), "03:29:13");
  assert.equal(readDuration("08:58 AM"), null);
});

test("the longest recording inside the session's hours wins, never another day", () => {
  const entries = [
    { topic: "CAI5_AIS4_S7", recordedAt: "2026-09-01T18:55", duration: "00:04:10", detailUrl: "short" },
    { topic: "CAI5_AIS4_S7", recordedAt: "2026-09-01T19:02", duration: "02:58:00", detailUrl: "long" },
    { topic: "CAI5_AIS4_S7", recordedAt: "2026-09-01T09:00", duration: "03:10:00", detailUrl: "morning" },
    { topic: "CAI5_AIS4_S7", recordedAt: "2026-08-25T19:00", duration: "03:00:00", detailUrl: "last week" },
  ];
  assert.equal(pickRecording(entries, "CAI5_AIS4_S7", "2026-09-01", "19:00").detailUrl, "long");
  assert.equal(pickRecording(entries, "CAI5_AIS4_S7", "2026-09-01", "09:00").detailUrl, "morning");
  assert.equal(pickRecording(entries, "CAI5_AIS4_S7", "2026-09-02", "19:00"), null);
  assert.equal(pickRecording(entries, "CAI5_AIS4_S7", "2026-09-01", "14:00"), null);
});

test("list times move from the Zoom account's zone to the Mac's", () => {
  const shifted = shiftEntries([{ recordedAt: "2026-09-01T09:00" }], -420, 180);
  assert.equal(shifted[0].recordedAt, "2026-09-01T19:00");
});

test("the share link's own start guards against the wrong week", () => {
  const start = readShareLinkStart("https://zoom.us/rec/share/x?startTime=1725210000000");
  assert.equal(start, 1725210000000);
  assert.equal(readShareLinkStart("https://zoom.us/rec/share/x?startTime=1725210000"), 1725210000000);
  const local = new Date(start);
  const pad = (value) => String(value).padStart(2, "0");
  const day = `${local.getFullYear()}-${pad(local.getMonth() + 1)}-${pad(local.getDate())}`;
  assert.equal(startsWithinSession(start, day, null).ok, true);
  assert.equal(startsWithinSession(start, "1999-01-01", null).ok, false);
});

function fakeDeps(overrides = {}) {
  const taken = [];
  return {
    taken,
    acquire: async (profile) => {
      taken.push(profile);
      return { release: () => taken.push(`released:${profile}`) };
    },
    readRecording: async () => ({ success: true, shareUrl: "https://zoom.us/rec/share/x", recording: { duration: "02:00:00" }, startedAtUtc: null }),
    attach: async () => ({ success: true, message: "saved" }),
    ...overrides,
  };
}

test("a Zoom recording is read, then written, holding one profile at a time", async () => {
  const deps = fakeDeps();
  const outcome = await processFromZoom({ group: "G1", day: "2026-09-01", profile: "s7" }, deps);
  assert.equal(outcome.status, RecordingStatus.attached);
  assert.deepEqual(deps.taken, ["s7", "released:s7", "lms-dashboard", "released:lms-dashboard"]);
});

test("a busy profile answers busy and never opens the dashboard", async () => {
  const deps = fakeDeps({ acquire: async () => null });
  const outcome = await processFromZoom({ group: "G1", profile: "s7" }, deps);
  assert.equal(outcome.status, RecordingStatus.busy);
});

test("dashboard failures carry a machine-readable reason", async () => {
  const deps = fakeDeps({ attach: async () => ({ success: false, failure: "sessionNotFinished", message: "not finished" }) });
  const outcome = await attachProvidedLink({ group: "G1", recordLink: DRIVE }, deps);
  assert.equal(outcome.status, RecordingStatus.lmsFailed);
  assert.equal(outcome.reason, "sessionNotFinished");
  const invalid = await attachProvidedLink({ group: "G1", recordLink: "https://example.com/x" }, deps);
  assert.equal(invalid.reason, "invalidLink");
});
