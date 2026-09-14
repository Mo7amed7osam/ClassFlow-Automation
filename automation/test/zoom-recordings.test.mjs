import assert from "node:assert/strict";
import test from "node:test";
import { classifyLink, driveFileIdOf, previewLink } from "../src/recording-links.mjs";

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

import { decideZoomLinkUpdate, isConfirmLabel, isEndSessionLabel } from "../src/lms.mjs";

test("End and its confirmation are recognised, cancel never is", () => {
  assert.equal(isEndSessionLabel("End Session"), true);
  assert.equal(isEndSessionLabel(" end "), true);
  assert.equal(isEndSessionLabel("Run Session"), false);
  assert.equal(isEndSessionLabel("End Meeting for All"), false);
  assert.equal(isEndSessionLabel("Complete Session"), true, "the DEPI dashboard's own label");
  assert.equal(isEndSessionLabel("Cancel Session"), false, "cancelling a session is never pressed");
  for (const label of ["Complete Session", "Complete", "End Session", "Confirm", "Yes", "Yes, end session", "OK"]) assert.equal(isConfirmLabel(label), true, label);
  for (const label of ["Cancel", "Cancel Session", "No", "Close", "Don't end"]) assert.equal(isConfirmLabel(label), false, label);
});

test("a Zoom link only fills an empty record link", () => {
  const zoom = "https://us06web.zoom.us/rec/share/abc?startTime=1725000000000";
  assert.equal(decideZoomLinkUpdate("", zoom), "add");
  assert.equal(decideZoomLinkUpdate(zoom, zoom), "same");
  assert.equal(decideZoomLinkUpdate(DRIVE, zoom), "keep", "a Drive link is never replaced by Zoom");
  assert.equal(decideZoomLinkUpdate("https://us06web.zoom.us/rec/share/other", zoom), "keep");
});
