import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import ExcelJS from "exceljs";
import { parseDate, parseRoster, parseTime, parseTimetable, readFirstSheet } from "../src/workbooks.mjs";
import { validateMeetingUrl, webClientUrl } from "../src/zoom-web.mjs";

async function writeWorkbook(build) {
  const workbook = new ExcelJS.Workbook();
  const sheet = workbook.addWorksheet("Sheet1");
  build(sheet);
  const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "zaa-")), "book.xlsx");
  await workbook.xlsx.writeFile(file);
  return file;
}

test("dates and times in the forms the timetable uses", () => {
  assert.equal(parseDate("2026-09-03"), "2026-09-03");
  assert.equal(parseDate("03/09/2026"), "2026-09-03");
  assert.equal(parseDate("46268"), "2026-09-03");
  assert.equal(parseDate("31/02/2026"), null);
  assert.equal(parseTime("7:00 PM"), "19:00");
  assert.equal(parseTime("12:30 am"), "00:30");
  assert.equal(parseTime("19:05"), "19:05");
  assert.equal(parseTime("25:00"), null);
});

test("the DEPI timetable is read with a reason for every row left out", async () => {
  const file = await writeWorkbook((sheet) => {
    sheet.addRow(["Round Code"]);
    sheet.addRow(["CAI5_AIS4_S8"]);
    sheet.addRow([]);
    sheet.addRow(["Session No.", "Date", "Session Type", "Session Content", "Slot"]);
    sheet.addRow([1, new Date(Date.UTC(2026, 8, 3)), "Online", "Intro to Python", "7:00 PM - 10:00 PM"]);
    sheet.addRow([2, new Date(Date.UTC(2026, 8, 5)), "Physical", "Lab", "7:00 PM - 10:00 PM"]);
    sheet.addRow([3, "2026-09-07", "Online", "Loops", "19:00 - 22:00"]);
    sheet.addRow([4, "2026-09-07", "Online", "Loops again", "7:00 PM - 10:00 PM"]);
    sheet.addRow(["", "", "No Session", "", ""]);
  });
  const { groupCode, rows } = parseTimetable(await readFirstSheet(file));
  assert.equal(groupCode, "CAI5_AIS4_S8");
  assert.equal(rows.length, 5);
  assert.deepEqual(rows[0], {
    rowNumber: 5, sessionNumber: "1", date: "2026-09-03", type: "Online", topic: "Intro to Python",
    startTime: "19:00", endTime: "22:00", timeRange: "7:00 PM - 10:00 PM", issue: "",
  });
  assert.equal(rows[1].issue, "Excluded: Physical");
  assert.equal(rows[2].issue, "");
  assert.equal(rows[3].issue, "Duplicate date/time in workbook");
  assert.equal(rows[4].issue, "Excluded: No Session");
});

test("formula cells are refused rather than trusted", async () => {
  const file = await writeWorkbook((sheet) => {
    sheet.addRow(["Order", "Name"]);
    sheet.getCell("A2").value = { formula: "1+1", result: 2 };
    sheet.getCell("B2").value = "Ahmed";
  });
  await assert.rejects(readFirstSheet(file), /formulas/);
});

test("rosters keep their explicit order and optional columns", async () => {
  const file = await writeWorkbook((sheet) => {
    sheet.addRow(["Order", "Full Name", "Aliases", "Email"]);
    sheet.addRow([2, "Mona Samir", "Mona S|منى", ""]);
    sheet.addRow([1, "Ahmed Ali", "", "ahmed@example.com"]);
    sheet.addRow(["x", "Bad Order", "", ""]);
  });
  const { students, problems } = parseRoster(await readFirstSheet(file));
  assert.deepEqual(students.map((student) => student.name), ["Ahmed Ali", "Mona Samir"]);
  assert.deepEqual(students[1].aliases, ["Mona S", "منى"]);
  assert.equal(students[0].email, "ahmed@example.com");
  assert.equal(problems.length, 1);
});

test("meeting links open in the Web Client", () => {
  assert.equal(webClientUrl("https://us06web.zoom.us/j/94698416251?pwd=abc.1", { host: true }), "https://app.zoom.us/wc/94698416251/start?pwd=abc.1");
  assert.equal(webClientUrl("https://zoom.us/j/94698416251", { host: false }), "https://app.zoom.us/wc/94698416251/join");
  assert.throws(() => validateMeetingUrl("https://zoom.evil.com/j/1"), /zoom\.us/);
  assert.throws(() => validateMeetingUrl("http://zoom.us/j/1"), /zoom\.us/);
});
