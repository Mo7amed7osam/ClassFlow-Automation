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
  attendanceEnabled: false
};

const CLICK_COOLDOWN_MS = 3000;
const SCAN_DEBOUNCE_MS = 300;
const SCAN_INTERVAL_MS = 2000;

const lastClickedAt = new WeakMap();
let scanTimer = null;
let lastAttendanceFingerprint = "";
let attendanceContext = null;
let contextRequest = null;
function refreshAttendanceContext() {
  if(contextRequest) return contextRequest;
  contextRequest=new Promise(resolve=>{
    chrome.runtime.sendMessage({type:"attendanceContext",url:location.href},reply=>{
      const next=!chrome.runtime.lastError && reply?.ok && reply.sessionId ? reply : null;
      if(next?.sessionId!==attendanceContext?.sessionId) {pendingAdmissions.clear();lastAttendanceFingerprint="";}
      attendanceContext=next;resolve(next);
    });
  }).finally(()=>{contextRequest=null;});
  return contextRequest;
}
const pendingAdmissions = new Map();
const cachedButtonNames = new WeakMap();
const NAME_SELECTOR = '[class*="participants-item__display-name"], [class*="participant-item__display-name"], [class*="participant-name"], [data-testid*="participant-name"], [aria-label*="participant name" i]';
const WAITING_NAME_SELECTOR = NAME_SELECTOR + ', [class*="display-name"], [class*="displayName"], [class*="displayname"], [class*="waiting"][class*="name"], [data-testid*="waiting"][data-testid*="name"]';
const WAITING_SECTION_SELECTOR = '[class*="waiting-room"], [class*="waitingRoom"], [class*="waiting-list"], [data-testid*="waiting"]';

function waitingNames() {
  // Zoom can hide individual Admit buttons until hover, even with the list open.
  const names = [...document.querySelectorAll('button, [role="button"], input[type="button"]')]
    .filter(button => classify(button) === "admit").map(waitingName).filter(Boolean);
  for (const section of document.querySelectorAll(WAITING_SECTION_SELECTOR)) {
    if (!isVisible(section)) continue;
    for (const node of section.querySelectorAll(WAITING_NAME_SELECTOR)) {
      const name = readName(node);
      if (isVisible(node) && name && name.length <= 120) names.push(name);
    }
  }
  return [...new Set(names)];
}
const automaticClicks = new WeakSet();
const preparedClicks = new WeakMap();

function scanWaitingRoom() {
  if(!attendanceContext?.sessionId) return;
  const names = waitingNames();
  for(const button of candidateButtons()) {
    const kind=classify(button);
    if(!kind) continue;
    const direct=kind==='admitAll' ? names : [waitingName(button)].filter(Boolean);
    if(direct.length) cachedButtonNames.set(button,{names:direct,at:Date.now(),sessionId:attendanceContext.sessionId});
  }
  chrome.runtime.sendMessage({
    type:"waitingRoomSnapshot",
    sessionId:attendanceContext.sessionId,
    names:[...new Set(names)],
    url:location.href
  },()=>void chrome.runtime.lastError);
}

function readName(node) {
  return cleanParticipantName(node.getAttribute("title") || node.textContent || node.getAttribute("aria-label"));
}

function waitingName(button) {
  // Ascend only as far as a single participant's controls, never the whole list.
  let row=button.parentElement;
  for(let depth=0;row && depth<8;depth++,row=row.parentElement) {
    const controls=[...row.querySelectorAll('button, [role="button"], input[type="button"]')];
    if(controls.filter(b=>classify(b)==="admit").length>1 || controls.some(b=>classify(b)==="admitAll")) return null;
    const names=[...new Set([...row.querySelectorAll(WAITING_NAME_SELECTOR)].filter(isVisible).map(readName).filter(Boolean))];
    if(names.length>1) return null;
    if(names.length===1) return names[0];
    // Some Zoom waiting rows use plain text rather than participant-name classes.
    // Remove the controls from a detached copy; never alter the Zoom page.
    if (isVisible(row) && controls.includes(button) && row.cloneNode) {
      const copy=row.cloneNode(true);
      copy.querySelectorAll('button, [role="button"], input, svg, img, [aria-hidden="true"]').forEach(node=>node.remove());
      const text=cleanParticipantName(copy.textContent);
      if(text && text.length<=120 && !/waiting room|participants|غرفة الانتظار/i.test(text)) return text;
    }
  }
  return null;
}

function admissionNames(button) {
  const kind=classify(button);
  if (!kind) return [];
  const direct=kind==='admitAll' ? waitingNames() : [waitingName(button)].filter(Boolean);
  if(direct.length) return direct;
  const cached=cachedButtonNames.get(button);
  if(cached?.sessionId===attendanceContext?.sessionId && Date.now()-cached.at<3000) return cached.names;
  // A notification's Admit button may be outside the participant row.
  const current=waitingNames();
  if(kind==='admit' && current.length===1) return current;
  return [];
}

function trackAdmitClick(button, names=admissionNames(button)) {
  if (!classify(button) && !names.length) return;
  // Never assign an unreadable click to an unrelated, recently seen person.
  lastAttendanceFingerprint = "";
  const events=[];
  for(const name of names) {
    if(!attendanceContext?.sessionId) break;
    const key=name.toLocaleLowerCase();
    if(pendingAdmissions.get(key)?.leftWaiting) pendingAdmissions.delete(key);
    if(!pendingAdmissions.has(key)) pendingAdmissions.set(key,{name,eventId:crypto.randomUUID(),at:Date.now(),sessionId:attendanceContext.sessionId});
    events.push(pendingAdmissions.get(key));
  }
  if(attendanceContext?.sessionId) chrome.runtime.sendMessage({type:"admissionAttempt",sessionId:attendanceContext.sessionId,events,clickId:crypto.randomUUID(),kind:classify(button),at:Date.now(),url:location.href},()=>void chrome.runtime.lastError);
  chrome.runtime.sendMessage({type:"admitted"},()=>void chrome.runtime.lastError);
}

function confirmAdmissions() {
  if(!pendingAdmissions.size) return;
  const waiting=new Set(waitingNames().map(n=>n.toLocaleLowerCase()));
  const present=new Set(participantNames().map(n=>n.toLocaleLowerCase()));
  for(const [key,event] of pendingAdmissions) {
    if(!waiting.has(key)) event.leftWaiting=true;
    if(Date.now()-event.at>120000) {pendingAdmissions.delete(key);continue;}
    if(waiting.has(key)||!present.has(key)||event.sending) continue;
    event.sending=true;
    chrome.runtime.sendMessage({type:"admissionConfirmed",sessionId:event.sessionId,url:location.href,name:event.name,eventId:event.eventId},reply=>{
      if(!chrome.runtime.lastError && reply?.ok) {if(pendingAdmissions.get(key)===event) pendingAdmissions.delete(key);}
      else event.sending=false;
    });
  }
}

document.addEventListener("pointerdown",event=>{
  const button=event.target.closest?.('button, [role="button"], input[type="button"]');
  if(button && classify(button)) preparedClicks.set(button,{names:admissionNames(button),at:Date.now()});
},true);
document.addEventListener("click",event=>{
  const button=event.target.closest?.('button, [role="button"], input[type="button"]');
  if(button && !automaticClicks.has(button)) {
    const prepared=preparedClicks.get(button);preparedClicks.delete(button);
    trackAdmitClick(button,prepared && Date.now()-prepared.at<5000 ? prepared.names : admissionNames(button));
  }
},true);

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

function isVisible(element) {
  if (element.disabled) return false;
  if (element.getAttribute("aria-disabled") === "true") return false;
  if (element.getAttribute("aria-hidden") === "true") return false;
  return element.getClientRects().length > 0;
}

function isCoolingDown(element) {
  const previous = lastClickedAt.get(element);
  return previous !== undefined && Date.now() - previous < CLICK_COOLDOWN_MS;
}

function candidateButtons() {
  const nodes = document.querySelectorAll(
    'button, [role="button"], a[role="button"], input[type="button"]'
  );
  return Array.from(nodes).filter(isVisible);
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

async function scan() {
  // Resolve the meeting before auto-admit can remove its waiting-room rows.
  await refreshAttendanceContext();
  scanWaitingRoom();
  confirmAdmissions();
  if (!SETTINGS.enabled) return;

  const buttons = candidateButtons();
  const admitAll = [];
  const admit = [];

  for (const button of buttons) {
    if (isCoolingDown(button)) continue;
    const kind = classify(button);
    if (kind === "admitAll") admitAll.push(button);
    else if (kind === "admit") admit.push(button);
  }

  if (SETTINGS.preferAdmitAll && admitAll.length > 0) {
    press(admitAll[0], "admitAll");
    return;
  }

  for (const button of admit) {
    press(button, "admit");
  }
}

function scheduleScan() {
  if (scanTimer !== null) return;
  scanTimer = setTimeout(() => {
    scanTimer = null;
    scan();
  }, SCAN_DEBOUNCE_MS);
}

function dumpButtons() {
  const rows = candidateButtons().map((button) => ({
    label: labelsOf(button)[0] || "",
    text: normalize(button.textContent).slice(0, 60),
    aria: button.getAttribute("aria-label") || "",
    classes: button.className || "",
    match: classify(button) || "-"
  }));
  console.table(rows);
  console.log("[zoom-auto-admit] frame:", location.href, "buttons:", rows.length);
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
  const waiting = new Set(waitingNames().map(name=>name.toLocaleLowerCase()));
  for (const node of document.querySelectorAll(selectors.join(","))) {
    if (!isVisible(node)) continue;
    const row = node.closest('[role="listitem"], li, [class*="participants-item"], [class*="participant-item"]');
    const rowText = normalize(row?.textContent);
    // Waiting-room rows have host controls and are not attendance evidence.
    if (node.closest(WAITING_SECTION_SELECTOR) || (row && [...row.querySelectorAll('button, [role="button"]')].some(button=>classify(button)))) {
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
  const context=await refreshAttendanceContext();
  if (!context?.sessionId) return;
  const names = participantNames().sort((a, b) => a.localeCompare(b));
  if (!names.length) return;
  const fingerprint = names.join("\n");
  if (fingerprint === lastAttendanceFingerprint) return;
  lastAttendanceFingerprint = fingerprint;
  chrome.runtime.sendMessage(
    { type: "attendanceSnapshot", sessionId:context.sessionId,names, capturedAt: new Date().toISOString(), url: location.href },
    () => void chrome.runtime.lastError
  );
}

chrome.storage.local.get(SETTINGS, (stored) => {
  Object.assign(SETTINGS, stored);
  refreshAttendanceContext().then(scan);
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== "local") return;
  for (const [key, change] of Object.entries(changes)) {
    if (key in SETTINGS) SETTINGS[key] = change.newValue;
  }
  if(changes.attendanceSessions) refreshAttendanceContext();
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "dump") {
    dumpButtons();
    sendResponse({ ok: true, url: location.href });
  }
  if (message?.type === "ping") {
    sendResponse({ ok: true, top: window === window.top, version:"0.7.3" });
  }
  if (message?.type === "captureAttendance") {
    if(!message.sessionId) {sendResponse({ok:false,names:[]});return false;}
    const key=MeetingSource.key(location.href);
    if(key && key!==message.meetingKey) {sendResponse({ok:false,names:[]});return false;}
    if(attendanceContext?.sessionId!==message.sessionId) {pendingAdmissions.clear();lastAttendanceFingerprint="";}
    attendanceContext={sessionId:message.sessionId,meetingKey:message.meetingKey};
    const names = participantNames();
    sendResponse({ ok: true, names, sessionId:message.sessionId, url: location.href });
  }
  return false;
});

new MutationObserver(scheduleScan).observe(document.documentElement, {
  childList: true,
  subtree: true
});

setInterval(scan, SCAN_INTERVAL_MS);
setInterval(captureAttendance, 15000);
