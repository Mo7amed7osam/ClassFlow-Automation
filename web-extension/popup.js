const DEFAULTS = {
  enabled: true,
  preferAdmitAll: true,
  debug: false,
  admittedCount: 0
};

const TOGGLES = ["enabled", "preferAdmitAll", "debug"];

const statusEl = document.getElementById("status");
const statusText = document.getElementById("statusText");
const countEl = document.getElementById("admittedCount");
const hintEl = document.getElementById("hint");
const attendanceSummaryEl = document.getElementById("attendanceSummary");

let onZoomPage = false;

function renderCount(value, animate = false) {
  countEl.textContent = value ?? 0;
  if (!animate) return;
  countEl.classList.remove("bump");
  void countEl.offsetWidth; // restart the animation
  countEl.classList.add("bump");
}

function renderStatus() {
  const enabled = document.getElementById("enabled").checked;
  if (!onZoomPage) {
    statusEl.dataset.state = "idle";
    statusText.textContent = "No meeting";
    return;
  }
  statusEl.dataset.state = enabled ? "live" : "paused";
  statusText.textContent = enabled ? "Watching" : "Paused";
}

async function activeTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab;
}

async function detectZoomPage() {
  const tab = await activeTab();
  if (!tab?.id) return;
  try {
    const reply = await chrome.tabs.sendMessage(tab.id, { type: "ping" });
    onZoomPage = Boolean(reply?.ok);
  } catch {
    // No content script in this tab — not a Zoom meeting page.
    onZoomPage = false;
  }
  hintEl.textContent = onZoomPage
    ? "Open the Participants panel so the waiting-room controls exist in the page."
    : "Open a Zoom meeting at app.zoom.us as host, then reopen this popup.";
  renderStatus();
}

chrome.storage.local.get(DEFAULTS, (stored) => {
  for (const key of TOGGLES) {
    document.getElementById(key).checked = Boolean(stored[key]);
  }
  renderCount(stored.admittedCount);
  renderStatus();
});

(async()=>{
  const tab=await activeTab();
  if(!tab?.id)return;
  const reply=await chrome.runtime.sendMessage({type:"attendanceContext",sourceTabId:tab.id});
  if(!reply?.sessionId)return;
  const data=await chrome.runtime.sendMessage({type:"listAttendance"});
  const meeting=data.sessions?.[reply.sessionId];
  if(meeting)attendanceSummaryEl.textContent=`${meeting.name}: ${Object.keys(meeting.observed||{}).length} names captured`;
})();
for (const key of TOGGLES) {
  document.getElementById(key).addEventListener("change", (event) => {
    chrome.storage.local.set({ [key]: event.target.checked });
    if (key === "enabled") renderStatus();
  });
}

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes.admittedCount) {
    renderCount(changes.admittedCount.newValue, true);
  }
});

document.getElementById("reset").addEventListener("click", () => {
  chrome.storage.local.set({ admittedCount: 0 });
  renderCount(0);
});

document.getElementById("dump").addEventListener("click", async () => {
  const tab = await activeTab();
  if (!tab?.id) return;
  chrome.tabs.sendMessage(tab.id, { type: "dump" }, () => void chrome.runtime.lastError);
});

document.getElementById("openAttendance").addEventListener("click", async () => {
  const tab=await activeTab(); chrome.tabs.create({ url: chrome.runtime.getURL("attendance.html") + (tab?.id ? "?sourceTabId="+tab.id : "") });
});

// A plain target="_blank" works here, but Chrome closes the popup before the
// navigation lands often enough to be worth opening the tab explicitly.
document.getElementById("credit").addEventListener("click", (event) => {
  event.preventDefault();
  chrome.tabs.create({ url: event.currentTarget.href });
});

detectZoomPage();

