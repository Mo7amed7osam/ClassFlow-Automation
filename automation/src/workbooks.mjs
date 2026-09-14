import fs from "node:fs";
import path from "node:path";
import ExcelJS from "exceljs";

// Read-only Excel imports: the DEPI timetable and class rosters. Only the first worksheet is
// read; formulas and error cells are refused rather than trusted, macros are never run.

const MAXIMUM_FILE_BYTES = 20 * 1024 * 1024;
const MAXIMUM_ROWS = 10_000;

/** Every row of the first worksheet as { row, cells: { A: "text", ... } }. */
export async function readFirstSheet(file) {
  if (path.extname(file).toLowerCase() !== ".xlsx") throw userError("Choose an Excel .xlsx file.");
  const size = fs.statSync(file).size;
  if (size > MAXIMUM_FILE_BYTES) throw userError("The workbook is larger than 20 MB.");

  const workbook = new ExcelJS.Workbook();
  await workbook.xlsx.readFile(file);
  const sheet = workbook.worksheets[0];
  if (!sheet) throw userError("The workbook has no worksheet.");

  const rows = [];
  sheet.eachRow({ includeEmpty: false }, (row, rowNumber) => {
    if (rows.length >= MAXIMUM_ROWS) throw userError("The workbook has more than 10,000 rows.");
    const cells = {};
    row.eachCell({ includeEmpty: false }, (cell) => {
      cells[columnLetters(cell.col)] = cellText(cell);
    });
    rows.push({ row: rowNumber, cells });
  });
  return rows;
}

function cellText(cell) {
  const value = cell.value;
  if (value === null || value === undefined) return "";
  if (value instanceof Date) {
    // exceljs hands dates over as UTC midnight of the calendar day the sheet shows.
    return value.toISOString().slice(0, value.getUTCHours() || value.getUTCMinutes() ? 16 : 10).replace("T", " ");
  }
  if (typeof value === "object") {
    if ("formula" in value || "sharedFormula" in value) throw userError("Paste formulas as values before importing.");
    if ("error" in value) throw userError("The workbook contains an Excel error cell.");
    if ("richText" in value) return value.richText.map((part) => part.text).join("").trim();
    if ("text" in value) return String(value.text).trim();
    if ("hyperlink" in value) return String(value.hyperlink).trim();
    return "";
  }
  return String(value).trim();
}

function columnLetters(index) {
  let letters = "";
  let n = index;
  while (n > 0) {
    const remainder = (n - 1) % 26;
    letters = String.fromCharCode(65 + remainder) + letters;
    n = Math.floor((n - 1) / 26);
  }
  return letters;
}

// ---------------------------------------------------------------- DEPI timetable

/**
 * The supplied DEPI timetable: metadata above the header (Round Code), then Session No., Date,
 * Session Type, Content and Slot. Every row comes back with the reason it cannot be imported,
 * so the preview can show what was left out and why.
 */
export function parseTimetable(rows) {
  let groupCode = "";
  for (let index = 0; index < rows.length - 1; index += 1) {
    const column = findColumn(rows[index].cells, ["Round Code"]);
    if (column) {
      groupCode = rows[index + 1].cells[column] ?? "";
      break;
    }
  }

  const header = rows.findIndex((row) => findColumn(row.cells, ["Session No."]) && findColumn(row.cells, ["Session Type"]));
  if (header < 0) throw userError("Expected the DEPI timetable headers: Session No., Date, Session Type, Content and Slot.");
  const column = (names) => {
    const found = findColumn(rows[header].cells, names);
    if (!found) throw userError(`Missing timetable column: ${names[0]}`);
    return found;
  };
  const numberCol = column(["Session No."]);
  const dateCol = column(["Date", "Session Date"]);
  const typeCol = column(["Session Type"]);
  const topicCol = column(["Content", "Topic", "Session Content"]);
  const timeCol = column(["Slot", "Time", "Session Time"]);

  const seen = new Set();
  const result = [];
  for (const row of rows.slice(header + 1)) {
    const field = (col) => (row.cells[col] ?? "").trim();
    if (Object.values(row.cells).every((value) => !String(value).trim())) continue;
    const type = field(typeCol);
    const topic = field(topicCol);
    const sessionNumber = field(numberCol);
    const date = parseDate(field(dateCol));
    const timeRange = field(timeCol);
    const [startText, endText] = timeRange.split(/[-–—]/).map((part) => part?.trim());
    const startTime = parseTime(startText);
    const endTime = parseTime(endText);

    let issue = "";
    if (type.toLowerCase() !== "online") issue = `Excluded: ${type || "unknown session type"}`;
    else if (!date || !startTime || !sessionNumber || !topic) issue = "Invalid/missing date, time, session number or topic";
    else if (seen.has(`${date}|${startTime}`)) issue = "Duplicate date/time in workbook";
    else seen.add(`${date}|${startTime}`);

    result.push({ rowNumber: row.row, sessionNumber, date, type, topic, startTime, endTime, timeRange, issue });
  }
  if (result.length === 0) throw userError("No timetable rows found.");
  return { groupCode, rows: result };
}

/** yyyy-MM-dd from an ISO date, an Excel serial, or dd/MM/yyyy text. */
export function parseDate(text) {
  const value = String(text ?? "").trim();
  if (!value) return null;
  let match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  if (match) return validDate(Number(match[1]), Number(match[2]), Number(match[3]));
  if (/^\d+(\.0+)?$/.test(value)) {
    const serial = Number(value);
    if (serial < 1 || serial > 2_950_000) return null;
    // Excel's 1900 system counts from 1899-12-30 once its fictional 1900-02-29 is accounted for.
    const date = new Date(Date.UTC(1899, 11, 30) + serial * 86_400_000);
    return date.toISOString().slice(0, 10);
  }
  match = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(value);
  if (match) return validDate(Number(match[3]), Number(match[2]), Number(match[1]));
  return null;
}

function validDate(year, month, day) {
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) return null;
  return date.toISOString().slice(0, 10);
}

/** HH:mm from "7:00 PM", "07:00 pm", "19:00". */
export function parseTime(text) {
  const value = String(text ?? "").trim();
  const match = /^(\d{1,2}):(\d{2})\s*(AM|PM)?$/i.exec(value);
  if (!match) return null;
  let hours = Number(match[1]);
  const minutes = Number(match[2]);
  if (minutes > 59) return null;
  if (match[3]) {
    if (hours < 1 || hours > 12) return null;
    hours = (hours % 12) + (match[3].toUpperCase() === "PM" ? 12 : 0);
  } else if (hours > 23) {
    return null;
  }
  return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}`;
}

// ---------------------------------------------------------------- roster

/**
 * A roster sheet with Order and Name (or FullName) headers; StudentId, Aliases ("|"-separated)
 * and Email are optional. Rows keep their explicit numeric order, never alphabetical.
 */
export function parseRoster(rows) {
  const header = rows.findIndex((row) => findColumn(row.cells, ["Name", "FullName", "Full Name", "Official Name", "Student Name"]));
  if (header < 0) throw userError("Expected a Name (or FullName) column.");
  const cells = rows[header].cells;
  const nameCol = findColumn(cells, ["Name", "FullName", "Full Name", "Official Name", "Student Name"]);
  const orderCol = findColumn(cells, ["Order", "#", "No", "No."]);
  const idCol = findColumn(cells, ["StudentId", "Student ID", "ID"]);
  const aliasCol = findColumn(cells, ["Aliases", "Alias"]);
  const emailCol = findColumn(cells, ["Email", "E-mail"]);

  const students = [];
  const problems = [];
  for (const row of rows.slice(header + 1)) {
    const name = (row.cells[nameCol] ?? "").trim();
    if (!name) continue;
    const orderText = orderCol ? (row.cells[orderCol] ?? "").trim() : "";
    const order = orderText ? Number(orderText) : null;
    if (orderText && (!Number.isInteger(order) || order <= 0)) {
      problems.push(`Row ${row.row}: order "${orderText}" is not a positive whole number.`);
      continue;
    }
    students.push({
      rowNumber: row.row,
      order,
      name,
      externalId: idCol ? (row.cells[idCol] ?? "").trim() || null : null,
      aliases: aliasCol ? (row.cells[aliasCol] ?? "").split("|").map((alias) => alias.trim()).filter(Boolean) : [],
      email: emailCol ? (row.cells[emailCol] ?? "").trim() || null : null,
    });
  }
  const ordered = students.some((student) => student.order !== null)
    ? [...students].sort((left, right) => (left.order ?? Number.MAX_SAFE_INTEGER) - (right.order ?? Number.MAX_SAFE_INTEGER) || left.rowNumber - right.rowNumber)
    : students;
  return { students: ordered, problems };
}

function findColumn(cells, names) {
  const wanted = names.map((name) => name.toLowerCase().replace(/[\s_-]+/g, ""));
  for (const [column, value] of Object.entries(cells)) {
    if (wanted.includes(String(value).toLowerCase().replace(/[\s_-]+/g, ""))) return column;
  }
  return null;
}

function userError(message) {
  const error = new Error(message);
  error.userFacing = true;
  return error;
}
