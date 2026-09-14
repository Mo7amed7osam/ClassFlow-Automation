import { normalizeName } from "./normalize.mjs";

/**
 * What an attendance upload is about to write, decided before anything is pressed.
 *
 * The dashboard's own list is the one that counts: a student it does not list cannot be
 * marked, and a student it lists who was never seen is Not-joined rather than left blank,
 * because a blank row is not an answer.
 *
 * `everyone` marks every listed student Joined. It is its own flag so that an empty list of
 * names can never be mistaken for "everybody came".
 */
export function buildAttendancePlan(studentsOnTheDashboard, present, everyone = false) {
  if (!Array.isArray(studentsOnTheDashboard)) throw new TypeError("studentsOnTheDashboard must be an array");
  if (!Array.isArray(present)) throw new TypeError("present must be an array");

  if (everyone) {
    return withSummary({
      marks: studentsOnTheDashboard.map((studentName) => ({ studentName, joined: true })),
      notOnTheDashboard: [],
    });
  }

  const joined = new Set(present.map(normalizeName).filter((name) => name.length > 0));
  const claimed = new Set();
  const marks = studentsOnTheDashboard.map((studentName) => {
    const normalized = normalizeName(studentName);
    const wasSeen = joined.has(normalized);
    if (wasSeen) claimed.add(normalized);
    return { studentName, joined: wasSeen };
  });

  const seen = new Set();
  const notOnTheDashboard = [];
  for (const name of present) {
    const normalized = normalizeName(name);
    if (normalized.length === 0 || claimed.has(normalized) || seen.has(normalized)) continue;
    seen.add(normalized);
    notOnTheDashboard.push(name);
  }
  return withSummary({ marks, notOnTheDashboard });
}

/**
 * A roster from another group looks like this: most of the names sent are not on the session at
 * all. Writing it would mark the session's real attendees Not-joined, so it is refused outright.
 * Half is generous: a class's own register normally matches every present name.
 */
export function crossGroupSuspicion(plan, presentCount) {
  if (presentCount === 0) return null;
  const missing = plan.notOnTheDashboard.length;
  if (missing * 2 < presentCount) return null;
  return `${missing} of the ${presentCount} present names are not on this dashboard session - this looks like another group's register. Nothing was sent.`;
}

function withSummary(plan) {
  const joinedCount = plan.marks.filter((mark) => mark.joined).length;
  const notJoinedCount = plan.marks.length - joinedCount;
  const missing = plan.notOnTheDashboard;
  let summary = `${joinedCount} joined, ${notJoinedCount} not-joined`;
  if (missing.length > 0) {
    summary += `, ${missing.length} the dashboard does not list (${missing.slice(0, 5).join(", ")}${missing.length > 5 ? ", ..." : ""})`;
  }
  return { ...plan, joinedCount, notJoinedCount, summary };
}
