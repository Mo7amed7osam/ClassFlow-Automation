import assert from "node:assert/strict";
import test from "node:test";
import { chooseSessionRow, decideRecordLinkUpdate, distinctGroupRows, groupControlPairs, minutesApart, readRowTime } from "../src/lms.mjs";
import { normalizeName } from "../src/normalize.mjs";
import { buildAttendancePlan, crossGroupSuspicion } from "../src/plan.mjs";

test("normalization matches the app's Swift rules", () => {
  assert.equal(normalizeName("  Mohamed   AHMED (Host)"), "mohamed ahmed");
  assert.equal(normalizeName("أحمد_علي"), "احمد علي");
  assert.equal(normalizeName("مُحَمَّد"), "محمد");
  assert.equal(normalizeName("فاطمة"), "فاطمه");
  assert.equal(normalizeName("Sara.O'Neil"), "sara oneil");
});

test("the plan marks every dashboard row and reports who it does not list", () => {
  const plan = buildAttendancePlan(["Ahmed Ali", "Mona Samir", "Omar Adel"], ["ahmed  ali", "Omar Adel", "Stranger Name"]);
  assert.deepEqual(plan.marks.map((mark) => mark.joined), [true, false, true]);
  assert.deepEqual(plan.notOnTheDashboard, ["Stranger Name"]);
  assert.equal(plan.joinedCount, 2);
  assert.match(plan.summary, /2 joined, 1 not-joined, 1 the dashboard does not list/);
});

test("an empty present list never means everyone came", () => {
  const plan = buildAttendancePlan(["A B", "C D"], []);
  assert.deepEqual(plan.marks.map((mark) => mark.joined), [false, false]);
  const all = buildAttendancePlan(["A B", "C D"], [], true);
  assert.deepEqual(all.marks.map((mark) => mark.joined), [true, true]);
});

test("the row time is read just after the date", () => {
  assert.equal(readRowTime("CAI5_AIS4_S7\n2026-09-0219:00\nRunning"), "19:00");
  assert.equal(readRowTime("Intro 2026-09-02 7:05 extra 10:00"), "07:05");
  assert.equal(readRowTime("no time here"), null);
  assert.equal(minutesApart("19:00", "18:45"), 15);
});

test("a session row is chosen by time, and ambiguity opens nothing", () => {
  const link = {};
  assert.equal(chooseSessionRow([{ link, time: "19:00" }], "G", null).index, 0);
  assert.equal(chooseSessionRow([{ link, time: "19:00" }, { link, time: "19:00" }], "G", null).index, 0);
  assert.equal(chooseSessionRow([{ link, time: "10:00" }, { link, time: "19:00" }], "G", null).index, null);
  assert.equal(chooseSessionRow([{ link, time: "10:00" }, { link, time: "19:00" }], "G", "18:50").index, 1);
  assert.equal(chooseSessionRow([{ link, time: "10:00" }, { link, time: "19:00" }], "G", "14:00").index, null);
  assert.equal(chooseSessionRow([{ link, time: "10:00" }, { link, time: "12:00" }], "G", "11:00").index, null);
});

test("consecutive controls beside one name are one student", () => {
  const rows = groupControlPairs([
    { name: "Ahmed", index: 0 },
    { name: "Ahmed", index: 1 },
    { name: "Mona", index: 2 },
    { name: "Mona", index: 3 },
    { name: "", index: 4 },
  ]);
  assert.deepEqual(rows, [
    { name: "Ahmed", joinedIndex: 0, notJoinedIndex: 1 },
    { name: "Mona", joinedIndex: 2, notJoinedIndex: 3 },
  ]);
  assert.deepEqual(groupControlPairs([{ name: "Solo", index: 7 }]), [{ name: "Solo", joinedIndex: 7, notJoinedIndex: 7 }]);
});

test("another group's register is refused before anything is sent", () => {
  const dashboard = ["Ahmed Ali", "Mona Samir", "Omar Adel", "Sara Hany"];
  const own = buildAttendancePlan(dashboard, ["Ahmed Ali", "Omar Adel"]);
  assert.equal(crossGroupSuspicion(own, 2), null);
  const oneStranger = buildAttendancePlan(dashboard, ["Ahmed Ali", "Omar Adel", "Late Visitor"]);
  assert.equal(crossGroupSuspicion(oneStranger, 3), null, "a single unknown name is not another group");
  const otherGroup = buildAttendancePlan(dashboard, ["Hanan X", "Amal Y", "Ahmed Ali"]);
  assert.match(crossGroupSuspicion(otherGroup, 3), /another group's register/);
  assert.equal(crossGroupSuspicion(buildAttendancePlan(dashboard, []), 0), null);
});

test("sessions are matched by whole group code and counted once", () => {
  const rows = [
    { text: "Week 9 - Session 3 U unknown 2026-09-14 17:00 CAI5_IND1_G1 second yth CAI Live Coach Pending Join Session", href: null },
    { text: "Week 9 - Session 3Uunknown2026-09-1417:00CAI5_IND1_G1secondythCAIlivecoachpendingJoin Session", href: null },
    { text: "Week 9 - Session 3 2026-09-14 17:00 CAI5_IND1_G10 Pending", href: null },
    { text: "Week 9 - Session 3 2026-09-14 17:00 CAI5_IND1_G2 Pending", href: null },
  ];
  assert.equal(distinctGroupRows(rows, "CAI5_IND1_G1").length, 1, "the two layouts of one session are one session; G10 is not G1");
  assert.equal(distinctGroupRows(rows, "CAI5_IND1_G3").length, 0);
  const two = [
    { text: "Session A 2026-09-14 10:00 CAI5_IND1_G1", href: "/group_admin/sessions/a" },
    { text: "Session B 2026-09-14 17:00 CAI5_IND1_G1", href: "/group_admin/sessions/b" },
    { text: "Session B 2026-09-14 17:00 CAI5_IND1_G1", href: "/group_admin/sessions/b" },
  ];
  assert.equal(distinctGroupRows(two, "CAI5_IND1_G1").length, 2, "two real sessions stay two - ambiguous");
});

test("the current record link decides add, same or conflict", () => {
  const drive = "https://drive.google.com/file/d/1A1bzBcdEfGhIjKlMnOpQrStUv/view?usp=sharing";
  assert.equal(decideRecordLinkUpdate("", drive), "add");
  assert.equal(decideRecordLinkUpdate("  ", drive), "add");
  assert.equal(decideRecordLinkUpdate(drive, drive), "same");
  assert.equal(decideRecordLinkUpdate("https://drive.google.com/file/d/1A1bzBcdEfGhIjKlMnOpQrStUv/view", drive), "same", "the same file shared differently");
  assert.equal(decideRecordLinkUpdate("https://drive.google.com/file/d/ZZZbzBcdEfGhIjKlMnOpQrStUv/view", drive), "conflict");
  assert.equal(decideRecordLinkUpdate("https://us06web.zoom.us/rec/share/abc", drive), "conflict", "a Zoom link someone put there is never overwritten");
});

test("a Zoom recording link is replaced only when that is switched on", () => {
  const drive = "https://drive.google.com/file/d/1A1bzBcdEfGhIjKlMnOpQrStUv/view";
  const zoom = "https://zoom.us/rec/share/AzzuFagFqb4oihGc7Zp";
  assert.equal(decideRecordLinkUpdate(zoom, drive), "conflict");
  assert.equal(decideRecordLinkUpdate(zoom, drive, { replaceZoomRecordingLinks: true }), "replaceZoom");
  assert.equal(decideRecordLinkUpdate("https://youtube.com/watch?v=x", drive, { replaceZoomRecordingLinks: true }), "conflict", "only Zoom recording links");
  assert.equal(decideRecordLinkUpdate("http://zoom.us/rec/share/x", drive, { replaceZoomRecordingLinks: true }), "conflict", "https only");
});
