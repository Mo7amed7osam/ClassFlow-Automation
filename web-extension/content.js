// Zoom Auto Admit (Web) — content script.
//
// Watches the Zoom Web Client DOM for waiting-room admit controls and presses
// them. Zoom ships obfuscated class names, so matching is driven by the visible
// label (aria-label / text / title) instead of any structural selector.

const SETTINGS = {
  enabled: true,
  preferAdmitAll: true,
  debug: false,
  extraAdmitLabels: [],
  attendanceEnabled: false,
  keepParticipantsOpen: true
};

const CLICK_COOLDOWN_MS = 3000;
const SCAN_DEBOUNCE_MS = 300;
const SCAN_INTERVAL_MS = 2000;
// Zoom only shows a waiting person's Admit button while their row is hovered.
const HOVER_INTERVAL_MS = 1000;
const HOVER_SETTLE_MS = 150;
const MAX_HOVER_TARGETS = 30;
const OPEN_WAITING_ROOM_COOLDOWN_MS = 20000;
// Zoom closes the Participants panel when someone starts sharing their screen.
const REOPEN_PARTICIPANTS_COOLDOWN_MS = 5000;
const REOPEN_UNCONFIRMED_COOLDOWN_MS = 30000;
const REOPEN_PARTICIPANTS_BACKOFF_MS = 300000;
const MAX_REOPEN_MISSES = 3;
const CONTEXT_MAX_AGE_MS = 30000;
const CONTEXT_WAIT_MS = 1500;
const WAITING_SNAPSHOT_HEARTBEAT_MS = 4000;

const BUTTON_SELECTOR = 'button, [role="button"], input[type="button"]';
const NAME_SELECTOR = '[class*="participants-item__display-name"], [class*="participant-item__display-name"], [class*="participant-name"], [data-testid*="participant-name"], [aria-label*="participant name" i]';
const WAITING_NAME_SELECTOR = NAME_SELECTOR + ', [class*="display-name"], [class*="displayName"], [class*="displayname"], [class*="waiting"][class*="name"], [data-testid*="waiting"][data-testid*="name"]';
const WAITING_SECTION_SELECTOR = '[class*="waiting-room"], [class*="waitingRoom"], [class*="waiting-list"], [data-testid*="waiting"]';
const ROW_SELECTOR = '[role="listitem"], [role="option"], [role="row"], li, [class*="participants-li"], [class*="participants-item"], [class*="participant-item"]';
// Section titles such as "Waiting Room (3)" — the only stable marker Zoom gives.
const WAITING_TITLE_RE = /^(?:waiting room|waiting|غرفة الانتظار|في غرفة الانتظار|قاعة الانتظار|قائمة الانتظار|sala de espera|salle d'attente|warteraum)(?:\s*\(\s*\d+\s*\))?$/;
const SECTION_TITLE_RE = /^(?:in the meeting|in meeting|participants|في الاجتماع|المشاركون|en la reunión|dans la réunion|in der besprechung)|\p{L}.*\(\s*\d+\s*\)$/u;
// Zoom's toolbar button: aria-label "open the participants list pane,3 particpants"
// while closed and "close the participants list pane…" while open.
const PARTICIPANTS_OPEN_RE = /^open the participants/;
const PARTICIPANTS_CLOSE_RE = /^close the participants/;
const PARTICIPANTS_BUTTON_RE = /^(?:\d+\s*)?(?:participants|manage participants|المشاركون|المشاركين|إدارة المشاركين|participantes|teilnehmer)(?:\s*\(?\d+\)?)?$/;
const PARTICIPANTS_PANEL_SELECTOR = '[class*="participants-wrapper"], [class*="participants-section-container"], [class*="participants-ul"], [class*="participants-item__display-name"], [class*="participant-item__display-name"], [role="dialog"][aria-label*="participants" i], [role="complementary"][aria-label*="participants" i]';

const lastClickedAt = new WeakMap();
const pendingAdmissions = new Map();
const cachedButtonNames = new WeakMap();
const automaticClicks = new WeakSet();
const preparedClicks = new WeakMap();
let observer = null;
let stopped = false;
let scanning = false;
let scanQueued = false;
let lastHoverAt = 0;
let lastOpenedWaitingRoomAt = 0;
let lastReopenAt = 0;
let reopenPending = false;
let reopenMisses = 0;
let lastWaitingSnapshot = { key: "", at: 0 };
let lastAttendanceFingerprint = "";
let attendanceContext = null;
let contextCheckedAt = 0;
let contextRequest = null;
// Tells the service worker this frame already runs a live copy, so an update
// doesn't inject a second one.
globalThis.zoomAutoAdmitRunning = true;

function extensionAlive() {
  try { return Boolean(chrome.runtime?.id); } catch { return false; }
}

// Once the extension is reloaded or removed, this copy of the script can no
// longer reach it and every chrome.* call throws "Extension context
// invalidated". Stop quietly; refreshing the Zoom tab loads the new copy.
function shutdown() {
  if (stopped) return;
  stopped = true;
  observer?.disconnect();
  clock.stop();
}

// Chrome throttles timers in background and minimized tabs, down to one
// wake-up a minute. A worker's timers are not throttled, so it paces scans
// and short waits; plain page timers remain the fallback.
const clock = (() => {
  let worker = null;
  let nextId = 0;
  const waiting = new Map();
  function release() {
    worker?.terminate();
    worker = null;
    for (const done of waiting.values()) done();
    waiting.clear();
  }
  try {
    const source = "onmessage=e=>setTimeout(()=>postMessage(e.data),e.data.ms)";
    worker = new Worker(URL.createObjectURL(new Blob([source], { type: "text/javascript" })));
    worker.onmessage = event => {
      const done = waiting.get(event.data.id);
      waiting.delete(event.data.id);
      done?.();
    };
    worker.onerror = release;
  } catch {
    worker = null;
  }
  function sleep(ms) {
    return new Promise(resolve => {
      if (!worker) { setTimeout(resolve, ms); return; }
      const id = ++nextId;
      // Backstop in case the page's security policy silently blocks the worker.
      const backstop = setTimeout(() => { waiting.delete(id); resolve(); }, ms + 3000);
      waiting.set(id, () => { clearTimeout(backstop); resolve(); });
      worker.postMessage({ id, ms });
    });
  }
  function every(ms, callback) {
    (async () => {
      while (!stopped) {
        await sleep(ms);
        if (!stopped) callback();
      }
    })();
  }
  return { sleep, every, stop: release };
})();

function send(message, callback) {
  if (stopped || !extensionAlive()) { shutdown(); callback?.(null); return; }
  try {
    chrome.runtime.sendMessage(message, reply => {
      const failed = chrome.runtime.lastError;
      callback?.(failed ? null : reply);
    });
  } catch {
    shutdown();
    callback?.(null);
  }
}

function refreshAttendanceContext() {
  if (contextRequest) return contextRequest;
  contextRequest = new Promise(resolve => {
    send({ type: "attendanceContext", url: location.href }, reply => {
      // Keep the last known meeting if the service worker was unreachable.
      if (!reply) { resolve(attendanceContext); return; }
      const next = reply.ok && reply.sessionId ? reply : null;
      if (next?.sessionId !== attendanceContext?.sessionId) { pendingAdmissions.clear(); lastAttendanceFingerprint = ""; }
      attendanceContext = next;
      contextCheckedAt = Date.now();
      resolve(next);
    });
  }).finally(() => { contextRequest = null; });
  return contextRequest;
}

function ensureAttendanceContext() {
  if (contextCheckedAt && Date.now() - contextCheckedAt < CONTEXT_MAX_AGE_MS) return Promise.resolve(attendanceContext);
  return refreshAttendanceContext();
}

function waitingNames() {
  // Zoom can hide individual Admit buttons until hover, even with the list open.
  const names = admitCandidates().filter(button => classify(button) === "admit").map(waitingName).filter(Boolean);
  for (const section of document.querySelectorAll(WAITING_SECTION_SELECTOR)) {
    if (!isVisible(section)) continue;
    for (const node of section.querySelectorAll(WAITING_NAME_SELECTOR)) {
      const name = readName(node);
      if (isVisible(node) && name && name.length <= 120) names.push(name);
    }
  }
  return [...new Set(names)];
}

function scanWaitingRoom() {
  if (!attendanceContext?.sessionId) return;
  const names = waitingNames();
  for (const button of admitCandidates()) {
    const kind = classify(button);
    if (!kind) continue;
    const direct = kind === 'admitAll' ? names : [waitingName(button)].filter(Boolean);
    if (direct.length) cachedButtonNames.set(button, { names: direct, at: Date.now(), sessionId: attendanceContext.sessionId });
  }
  // Only report changes (plus a heartbeat); every report rewrites storage.
  const key = attendanceContext.sessionId + "\n" + [...names].sort().join("\n");
  const fresh = Date.now() - lastWaitingSnapshot.at < WAITING_SNAPSHOT_HEARTBEAT_MS;
  if (key === lastWaitingSnapshot.key && (fresh || !names.length)) return;
  lastWaitingSnapshot = { key, at: Date.now() };
  send({
    type: "waitingRoomSnapshot",
    sessionId: attendanceContext.sessionId,
    names,
    url: location.href
  });
}

function readName(node) {
  return cleanParticipantName(node.getAttribute("title") || node.textContent || node.getAttribute("aria-label"));
}

function waitingName(button) {
  // Ascend only as far as a single participant's controls, never the whole list.
  let row = button.parentElement;
  for (let depth = 0; row && depth < 8; depth++, row = row.parentElement) {
    const controls = [...row.querySelectorAll(BUTTON_SELECTOR)];
    if (controls.filter(b => classify(b) === "admit").length > 1 || controls.some(b => classify(b) === "admitAll")) return null;
    const names = [...new Set([...row.querySelectorAll(WAITING_NAME_SELECTOR)].filter(isVisible).map(readName).filter(Boolean))];
    if (names.length > 1) return null;
    if (names.length === 1) return names[0];
    // Some Zoom waiting rows use plain text rather than participant-name classes.
    // Remove the controls from a detached copy; never alter the Zoom page.
    if (isVisible(row) && controls.includes(button) && row.cloneNode) {
      const copy = row.cloneNode(true);
      copy.querySelectorAll('button, [role="button"], input, svg, img, [aria-hidden="true"]').forEach(node => node.remove());
      const text = cleanParticipantName(copy.textContent);
      if (text && text.length <= 120 && !/waiting room|participants|غرفة الانتظار/i.test(text)) return text;
    }
  }
  return null;
}

function admissionNames(button) {
  const kind = classify(button);
  if (!kind) return [];
  const direct = kind === 'admitAll' ? waitingNames() : [waitingName(button)].filter(Boolean);
  if (direct.length) return direct;
  const cached = cachedButtonNames.get(button);
  if (cached?.sessionId === attendanceContext?.sessionId && Date.now() - cached.at < 3000) return cached.names;
  // A notification's Admit button may be outside the participant row.
  const current = waitingNames();
  if (kind === 'admit' && current.length === 1) return current;
  return [];
}

function trackAdmitClick(button, names = admissionNames(button)) {
  if (!classify(button) && !names.length) return;
  // Never assign an unreadable click to an unrelated, recently seen person.
  lastAttendanceFingerprint = "";
  const events = [];
  for (const name of names) {
    if (!attendanceContext?.sessionId) break;
    const key = name.toLocaleLowerCase();
    if (pendingAdmissions.get(key)?.leftWaiting) pendingAdmissions.delete(key);
    if (!pendingAdmissions.has(key)) pendingAdmissions.set(key, { name, eventId: crypto.randomUUID(), at: Date.now(), sessionId: attendanceContext.sessionId });
    events.push(pendingAdmissions.get(key));
  }
  if (attendanceContext?.sessionId) send({ type: "admissionAttempt", sessionId: attendanceContext.sessionId, events, clickId: crypto.randomUUID(), kind: classify(button), at: Date.now(), url: location.href });
  send({ type: "admitted" });
}

function confirmAdmissions() {
  if (!pendingAdmissions.size) return;
  const waiting = new Set(waitingNames().map(n => n.toLocaleLowerCase()));
  const present = new Set(participantNames().map(n => n.toLocaleLowerCase()));
  for (const [key, event] of pendingAdmissions) {
    if (!waiting.has(key)) event.leftWaiting = true;
    if (Date.now() - event.at > 120000) { pendingAdmissions.delete(key); continue; }
    if (waiting.has(key) || !present.has(key) || event.sending) continue;
    event.sending = true;
    send({ type: "admissionConfirmed", sessionId: event.sessionId, url: location.href, name: event.name, eventId: event.eventId }, reply => {
      if (reply?.ok) { if (pendingAdmissions.get(key) === event) pendingAdmissions.delete(key); }
      else event.sending = false;
    });
  }
}

document.addEventListener("pointerdown", event => {
  if (stopped) return;
  const button = event.target.closest?.(BUTTON_SELECTOR);
  if (button && classify(button)) preparedClicks.set(button, { names: admissionNames(button), at: Date.now() });
}, true);
document.addEventListener("click", event => {
  if (stopped) return;
  const button = event.target.closest?.(BUTTON_SELECTOR);
  if (button && !automaticClicks.has(button)) {
    const prepared = preparedClicks.get(button); preparedClicks.delete(button);
    trackAdmitClick(button, prepared && Date.now() - prepared.at < 5000 ? prepared.names : admissionNames(button));
  }
}, true);

function normalize(value) {
  return (value || "").replace(/\s+/g, " ").trim().toLowerCase();
}

function labelsOf(element) {
  return [
    element.getAttribute("aria-label"),
    element.getAttribute("title"),
    element.textContent
  ]
    .map(normalize)
    .filter(Boolean);
}

function matchesAny(labels, candidates) {
  return labels.some((label) => candidates.includes(label));
}

function isEnabled(element) {
  return !element.disabled && element.getAttribute("aria-disabled") !== "true";
}

function isVisible(element) {
  if (!isEnabled(element)) return false;
  if (element.getAttribute("aria-hidden") === "true") return false;
  return element.getClientRects().length > 0;
}

function isCoolingDown(element) {
  const previous = lastClickedAt.get(element);
  return previous !== undefined && Date.now() - previous < CLICK_COOLDOWN_MS;
}

function candidateButtons() {
  return Array.from(document.querySelectorAll(BUTTON_SELECTOR)).filter(isVisible);
}

// Admit buttons Zoom hides with CSS until hover are still in the page, and
// element.click() works on them, so they must not be filtered out by layout.
function admitCandidates() {
  return Array.from(document.querySelectorAll(BUTTON_SELECTOR)).filter(isEnabled);
}

function classify(element) {
  const labels = labelsOf(element);
  if (matchesAny(labels, ZAA_LABELS.blocked)) return null;
  if (matchesAny(labels, ZAA_LABELS.admitAll)) return "admitAll";
  const admitLabels = ZAA_LABELS.admit.concat(SETTINGS.extraAdmitLabels);
  if (matchesAny(labels, admitLabels)) return "admit";
  return null;
}

function press(element, kind) {
  lastClickedAt.set(element, Date.now());
  trackAdmitClick(element);
  automaticClicks.add(element);
  try { element.click(); } finally { automaticClicks.delete(element); }
  // The counter lives in the service worker: this page runs one content script
  // per frame, and each would otherwise keep its own count.
  if (SETTINGS.debug) {
    console.log("[zoom-auto-admit] pressed", kind, labelsOf(element)[0], element);
  }
}

function pressAdmitControls() {
  const admitAll = [];
  const admit = [];
  for (const button of admitCandidates()) {
    if (isCoolingDown(button)) continue;
    const kind = classify(button);
    if (kind === "admitAll") admitAll.push(button);
    else if (kind === "admit") admit.push(button);
  }

  if (SETTINGS.preferAdmitAll && admitAll.length > 0) {
    press(admitAll[0], "admitAll");
    return true;
  }
  for (const button of admit) {
    press(button, "admit");
  }
  return admit.length > 0;
}

function innermost(nodes) {
  return nodes.filter(node => !nodes.some(other => other !== node && node.contains(other)));
}

// The next section title after the waiting room ("In the Meeting (5)"), so
// rows of people already in the meeting are never hovered.
function nextSectionHeader(section, header) {
  const walker = document.createTreeWalker(section, NodeFilter.SHOW_TEXT);
  walker.currentNode = header;
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (header.contains(node) || node.nodeValue.length > 60) continue;
    const text = normalize(node.nodeValue);
    if (text && SECTION_TITLE_RE.test(text) && !node.parentElement?.closest(WAITING_NAME_SELECTOR)) return node.parentElement;
  }
  return null;
}

function waitingRowsAfter(header) {
  const follows = node => (header.compareDocumentPosition(node) & Node.DOCUMENT_POSITION_FOLLOWING) && !node.contains(header);
  for (let section = header.parentElement, depth = 0; section && depth < 5; section = section.parentElement, depth++) {
    let targets = [...section.querySelectorAll(WAITING_NAME_SELECTOR)].filter(follows);
    if (!targets.length) targets = innermost([...section.querySelectorAll(ROW_SELECTOR)].filter(follows).slice(0, 200));
    if (!targets.length) continue;
    const next = nextSectionHeader(section, header);
    if (next) targets = targets.filter(target => target.compareDocumentPosition(next) & Node.DOCUMENT_POSITION_FOLLOWING);
    return { section, header, targets: targets.filter(isVisible).slice(0, MAX_HOVER_TARGETS) };
  }
  return null;
}

function waitingRowGroups() {
  const groups = [];
  const root = document.body || document.documentElement;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const value = node.nodeValue;
    if (value.length > 60 || !value.trim() || !WAITING_TITLE_RE.test(normalize(value))) continue;
    const header = node.parentElement;
    if (!header || !isVisible(header)) continue;
    const group = waitingRowsAfter(header);
    if (group?.targets.length) groups.push(group);
  }
  for (const section of document.querySelectorAll(WAITING_SECTION_SELECTOR)) {
    if (!isVisible(section) || groups.some(group => group.section.contains(section))) continue;
    const targets = [...section.querySelectorAll(WAITING_NAME_SELECTOR)].filter(isVisible).slice(0, MAX_HOVER_TARGETS);
    if (targets.length) groups.push({ section, header: null, targets });
  }
  return groups;
}

function ancestorsUpTo(node, stop) {
  const chain = [];
  for (let current = node; current && current !== stop && chain.length < 10; current = current.parentElement) chain.push(current);
  return chain;
}

// Synthetic mouse events make Zoom render the row's hover-only controls.
// relatedTarget stays null so React treats it as the pointer entering the row.
function setHovered(target, section, entering) {
  const rect = target.getBoundingClientRect();
  const init = { bubbles: true, cancelable: true, composed: true, relatedTarget: null, clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2 };
  const chain = ancestorsUpTo(target, section);
  const pointer = type => new PointerEvent(type, { ...init, bubbles: type.endsWith("over") || type.endsWith("out"), pointerType: "mouse", isPrimary: true });
  const mouse = type => new MouseEvent(type, { ...init, bubbles: type.endsWith("over") || type.endsWith("out") || type === "mousemove" });
  if (entering) {
    target.dispatchEvent(pointer("pointerover"));
    target.dispatchEvent(mouse("mouseover"));
    for (const node of chain.slice().reverse()) {
      node.dispatchEvent(pointer("pointerenter"));
      node.dispatchEvent(mouse("mouseenter"));
    }
    target.dispatchEvent(mouse("mousemove"));
  } else {
    target.dispatchEvent(pointer("pointerout"));
    target.dispatchEvent(mouse("mouseout"));
    for (const node of chain) {
      node.dispatchEvent(pointer("pointerleave"));
      node.dispatchEvent(mouse("mouseleave"));
    }
  }
}

async function hoverAndAdmit(groups) {
  const hovered = [];
  for (const { section, header, targets } of groups) {
    // The title first, in case "Admit all" is hover-only too. Rows go last to
    // first, so a list that reveals one row at a time admits in waiting order.
    for (const target of [header, ...targets.slice().reverse()]) {
      if (!target) continue;
      setHovered(target, section, true);
      hovered.push({ target, section });
    }
  }
  try {
    await clock.sleep(HOVER_SETTLE_MS);
    if (stopped || !SETTINGS.enabled) return false;
    return pressAdmitControls();
  } finally {
    for (const { target, section } of hovered) setHovered(target, section, false);
  }
}

// With the Participants panel closed, Zoom's notification only offers
// "See waiting room" when several people are waiting at once.
function openWaitingRoom() {
  if (Date.now() - lastOpenedWaitingRoomAt < OPEN_WAITING_ROOM_COOLDOWN_MS) return;
  const button = candidateButtons().find(candidate => matchesAny(labelsOf(candidate), ZAA_LABELS.openWaitingRoom));
  if (!button) return;
  lastOpenedWaitingRoomAt = Date.now();
  button.click();
  if (SETTINGS.debug) console.log("[zoom-auto-admit] opened waiting room", button);
}

function participantsButtons() {
  return admitCandidates().filter(button => labelsOf(button).some(label =>
    PARTICIPANTS_OPEN_RE.test(label) || PARTICIPANTS_CLOSE_RE.test(label) || PARTICIPANTS_BUTTON_RE.test(label)));
}

function saysOpen(button) {
  return labelsOf(button).some(label => PARTICIPANTS_CLOSE_RE.test(label)) ||
    button.getAttribute("aria-expanded") === "true" || button.getAttribute("aria-pressed") === "true";
}

function participantsPanelOpen(buttons) {
  if ([...document.querySelectorAll(PARTICIPANTS_PANEL_SELECTOR)].some(isVisible)) return true;
  return buttons.some(saysOpen);
}

// Returns true when it pressed the toolbar's Participants button.
function keepParticipantsOpen() {
  if (!SETTINGS.keepParticipantsOpen) return false;
  const buttons = participantsButtons();
  if (participantsPanelOpen(buttons)) { reopenPending = false; reopenMisses = 0; return false; }
  // The button is a toggle: pressing it again before the panel has shown up
  // (Zoom can be slow in a background tab) would close it. So after a press
  // that hasn't been seen to work, wait longer, then back off entirely.
  const wait = reopenMisses >= MAX_REOPEN_MISSES ? REOPEN_PARTICIPANTS_BACKOFF_MS
    : reopenPending ? REOPEN_UNCONFIRMED_COOLDOWN_MS : REOPEN_PARTICIPANTS_COOLDOWN_MS;
  const now = Date.now();
  if (now - lastReopenAt < wait) return false;
  // A button labelled "open …" is unambiguous. A plain "Participants" toggle
  // is pressed only while it doesn't report itself as already open.
  const button = buttons.find(b => labelsOf(b).some(label => PARTICIPANTS_OPEN_RE.test(label))) ||
    buttons.find(b => !saysOpen(b));
  if (!button) return false;
  if (reopenPending) reopenMisses++;
  reopenPending = true;
  lastReopenAt = now;
  button.click();
  if (SETTINGS.debug) console.log("[zoom-auto-admit] reopened Participants", button);
  return true;
}

async function scan() {
  if (stopped || scanning) return;
  // An updated copy may already be running in this frame; never press
  // anything from the old one.
  if (!extensionAlive()) { shutdown(); return; }
  scanning = true;
  try {
    // Resolve the meeting before auto-admit can remove its waiting-room rows,
    // but a slow service worker must never hold up admitting people.
    await Promise.race([ensureAttendanceContext(), clock.sleep(CONTEXT_WAIT_MS)]);
    scanWaitingRoom();
    confirmAdmissions();
    if (!SETTINGS.enabled) return;
    if (pressAdmitControls()) return;
    if (keepParticipantsOpen()) return;

    if (Date.now() - lastHoverAt < HOVER_INTERVAL_MS) return;
    lastHoverAt = Date.now();
    const groups = waitingRowGroups();
    if (groups.length) await hoverAndAdmit(groups);
    else openWaitingRoom();
  } finally {
    scanning = false;
  }
}

function scheduleScan() {
  if (scanQueued || stopped) return;
  scanQueued = true;
  clock.sleep(SCAN_DEBOUNCE_MS).then(() => {
    scanQueued = false;
    scan();
  });
}

function dumpButtons() {
  const rows = admitCandidates().map((button) => ({
    label: labelsOf(button)[0] || "",
    text: normalize(button.textContent).slice(0, 60),
    aria: button.getAttribute("aria-label") || "",
    classes: button.className || "",
    visible: isVisible(button),
    match: classify(button) || "-"
  }));
  console.table(rows);
  console.log("[zoom-auto-admit] frame:", location.href, "buttons:", rows.length,
    "waiting rows:", waitingRowGroups().reduce((sum, group) => sum + group.targets.length, 0),
    "participants open:", participantsPanelOpen(participantsButtons()), "participants buttons:", participantsButtons());
}

function cleanParticipantName(value) {
  return (value || "")
    .replace(/\((?:host|co-host|me|guest|مضيف|مضيف مشارك|أنا)\)/gi, "")
    .replace(/\b(?:muted|unmuted|speaking|raise(?:d)? hand)\b/gi, "")
    .replace(/\s+/g, " ")
    .trim();
}

function participantNames() {
  const selectors = [
    '[class*="participants-item__display-name"]',
    '[class*="participant-item__display-name"]',
    '[class*="participant-name"]',
    '[data-testid*="participant-name"]',
    '[aria-label*="participant name" i]'
  ];
  const names = new Set();
  const waiting = new Set(waitingNames().map(name => name.toLocaleLowerCase()));
  for (const node of document.querySelectorAll(selectors.join(","))) {
    if (!isVisible(node)) continue;
    const row = node.closest('[role="listitem"], li, [class*="participants-item"], [class*="participant-item"]');
    // Waiting-room rows have host controls and are not attendance evidence.
    if (node.closest(WAITING_SECTION_SELECTOR) || (row && [...row.querySelectorAll('button, [role="button"]')].some(button => classify(button)))) {
      continue;
    }
    const name = cleanParticipantName(
      node.getAttribute("aria-label") || node.getAttribute("title") || node.textContent
    );
    if (name.length >= 2 && name.length <= 120 && !waiting.has(name.toLocaleLowerCase())) names.add(name);
  }
  return [...names];
}

async function captureAttendance() {
  if (stopped) return;
  const context = await ensureAttendanceContext();
  if (!context?.sessionId) return;
  const names = participantNames().sort((a, b) => a.localeCompare(b));
  if (!names.length) return;
  const fingerprint = names.join("\n");
  if (fingerprint === lastAttendanceFingerprint) return;
  lastAttendanceFingerprint = fingerprint;
  send({ type: "attendanceSnapshot", sessionId: context.sessionId, names, capturedAt: new Date().toISOString(), url: location.href });
}

function activeSessionIds(sessions) {
  return Object.values(sessions || {}).filter(s => s?.active && !s.finalized).map(s => s.id).sort().join(",");
}

chrome.storage.local.get(SETTINGS, (stored) => {
  Object.assign(SETTINGS, stored);
  refreshAttendanceContext().then(scan);
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== "local" || stopped) return;
  for (const [key, change] of Object.entries(changes)) {
    if (key in SETTINGS) SETTINGS[key] = change.newValue;
  }
  // Sessions are rewritten on every snapshot; only a start/stop changes routing.
  const sessions = changes.attendanceSessions;
  if (sessions && activeSessionIds(sessions.oldValue) !== activeSessionIds(sessions.newValue)) {
    contextCheckedAt = 0;
    refreshAttendanceContext();
  }
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "dump") {
    dumpButtons();
    sendResponse({ ok: true, url: location.href });
  }
  if (message?.type === "ping") {
    sendResponse({ ok: true, top: window === window.top, version: chrome.runtime.getManifest().version });
  }
  if (message?.type === "captureAttendance") {
    if (!message.sessionId) { sendResponse({ ok: false, names: [] }); return false; }
    const key = MeetingSource.key(location.href);
    if (key && key !== message.meetingKey) { sendResponse({ ok: false, names: [] }); return false; }
    if (attendanceContext?.sessionId !== message.sessionId) { pendingAdmissions.clear(); lastAttendanceFingerprint = ""; }
    attendanceContext = { sessionId: message.sessionId, meetingKey: message.meetingKey };
    contextCheckedAt = Date.now();
    const names = participantNames();
    const reply = { ok: true, names, sessionId: message.sessionId, url: location.href };
    // Every frame receives this and the first reply wins, so only the frame
    // showing the Participants list answers at once; the top frame reports
    // "no names" only if nobody else did.
    if (names.length) { sendResponse(reply); return false; }
    if (window !== window.top) return false;
    setTimeout(() => sendResponse(reply), 1000);
    return true;
  }
  return false;
});

observer = new MutationObserver(scheduleScan);
observer.observe(document.documentElement, {
  childList: true,
  subtree: true
});

clock.every(SCAN_INTERVAL_MS, scan);
clock.every(15000, captureAttendance);
