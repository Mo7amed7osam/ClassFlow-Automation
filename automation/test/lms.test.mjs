import assert from "node:assert/strict";
import test from "node:test";
import { chooseSessionRow, groupControlPairs, minutesApart, readRowTime } from "../src/lms.mjs";
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
