// The Chrome APIs the extension's attendance page and session worker use, provided by the app.
//
//   chrome.storage.local   -> this page's own storage (kept by the app's WebView2 profile)
//   chrome.runtime         -> messages go to worker.js in this page, exactly as to the extension's worker
//   chrome.tabs            -> "Zoom tabs" are the app's Zoom meetings (a class: group, day, time)
//   tabs.sendMessage(captureAttendance) -> the app's latest read of that meeting's Participants list
//
// The app pushes every Participants read it takes (every minute) as an attendanceSnapshot for the
// meeting, starting the meeting here with the group's roster when it has not been started yet.
(() => {
  const webview = window.chrome && window.chrome.webview;
  const PREFIX = "zaa-ext:";
  const storageListeners = [], messageListeners = [], removedListeners = [], updatedListeners = [];

  // ------------------------------------------------------------------ storage
  const read = key => { const v = localStorage.getItem(PREFIX + key); return v === null ? undefined : JSON.parse(v); };
  const fire = changes => setTimeout(() => { for (const l of storageListeners) { try { l(changes, "local"); } catch (e) { console.error(e); } } }, 0);
  const local = {
    async get(keys) {
      if (keys == null) {
        const all = {};
        for (let i = 0; i < localStorage.length; i++) { const k = localStorage.key(i); if (k.startsWith(PREFIX)) all[k.slice(PREFIX.length)] = JSON.parse(localStorage.getItem(k)); }
        return all;
      }
      if (typeof keys === "string") keys = [keys];
      const result = {};
      if (Array.isArray(keys)) { for (const k of keys) { const v = read(k); if (v !== undefined) result[k] = v; } return result; }
      for (const [k, fallback] of Object.entries(keys)) { const v = read(k); result[k] = v === undefined ? fallback : v; }
      return result;
    },
    async set(items) {
      const changes = {};
      for (const [k, v] of Object.entries(items)) { const old = read(k); localStorage.setItem(PREFIX + k, JSON.stringify(v)); changes[k] = { oldValue: old, newValue: JSON.parse(JSON.stringify(v)) }; }
      fire(changes);
    },
    async remove(keys) {
      if (typeof keys === "string") keys = [keys];
      const changes = {};
      for (const k of keys) { const old = read(k); localStorage.removeItem(PREFIX + k); changes[k] = { oldValue: old }; }
      fire(changes);
    },
  };

  // ------------------------------------------------------------------ the app
  const pending = new Map(); let nextId = 1;
  function host(method, params, timeoutMs) {
    return new Promise((resolve, reject) => {
      if (!webview) { reject(new Error("Open this page from the Zoom Auto Admit app.")); return; }
      const id = nextId++;
      pending.set(id, { resolve, reject });
      webview.postMessage({ id, method, params: params || {} });
      setTimeout(() => { if (pending.has(id)) { pending.delete(id); reject(new Error("The app did not answer.")); } }, timeoutMs || 20000);
    });
  }

  // ------------------------------------------------------------------ one AI key for the whole app
  // The page's AI calls (OpenRouter chat completions) go through the app, which adds the key saved
  // on the AI Engine page and uses the app's provider and model. The page never holds a key: the
  // stored value is only a marker, and any key typed here before is overwritten by it.
  if (webview) {
    localStorage.setItem(PREFIX + "openRouterAPIKey", JSON.stringify("app"));
    const realFetch = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const url = typeof input === "string" ? input : input?.url || "";
      if (!/^https:\/\/openrouter\.ai\/api\/v1\/chat\/completions/.test(url)) return realFetch(input, init);
      const signal = init?.signal;
      if (signal?.aborted) throw new DOMException("Aborted", "AbortError");
      const call = host("ai", { body: typeof init?.body === "string" ? init.body : "" }, 180000);
      const aborted = new Promise((_, reject) => signal?.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")), { once: true }));
      const reply = await Promise.race([call, aborted]);
      return new Response(reply?.body || "{}", { status: reply?.status || 502, headers: { "Content-Type": "application/json" } });
    };
  }

  let tabs = [];
  // "CAI5_AIS4_S8 · Mon 14 Sep 19:00" -> CAI5_AIS4_S8
  const groupOf = title => (/\b[A-Z]{2,6}\d*_[A-Z0-9]+_[A-Z0-9]+\b/.exec(title || "") || [])[0] || "";
  window.__zaaGroupOf = groupOf;
  window.__zaaHost = host;
  async function refreshTabs() { try { tabs = await host("tabs"); } catch { } return tabs; }
  const tabsApi = {
    async query() { await refreshTabs(); return tabs.filter(t => t.live); },
    async get(id) {
      let t = tabs.find(x => x.id === id);
      if (!t) { await refreshTabs(); t = tabs.find(x => x.id === id); }
      if (!t) throw new Error("No meeting with id " + id);
      return t;
    },
    async sendMessage(tabId, message) {
      if (message?.type !== "captureAttendance") return { ok: false };
      const reply = await host("capture", { tabId });
      return { ok: true, sessionId: message.sessionId, names: reply?.names || [], url: reply?.url };
    },
    onRemoved: { addListener: f => removedListeners.push(f) },
    onUpdated: { addListener: f => updatedListeners.push(f) },
  };

  const runtime = {
    onMessage: { addListener: f => messageListeners.push(f) },
    onStartup: { addListener() { } },
    onInstalled: { addListener() { } },
    getManifest: () => ({ content_scripts: [] }),
    sendMessage(message) {
      // A meeting started here is named after the class (its group, day and time), and starts with
      // that group's roster when none was typed - the meeting and the roster have one name.
      if (message?.type === "startAttendance") {
        const tab = tabs.find(t => t.id === message.sourceTabId);
        if (tab && !message.name) message = { ...message, name: tab.title };
        const group = groupOf(tab?.title);
        if (group && !(message.roster || []).length) {
          const saved = (read("savedRosters") || {})["app:" + group];
          if (saved?.names?.length) message = { ...message, roster: [...saved.names] };
        }
      }
      return new Promise(resolve => {
        let answered = false;
        const respond = r => { if (!answered) { answered = true; resolve(r); } };
        for (const listener of messageListeners) {
          const keepOpen = listener(message, {}, respond);
          if (keepOpen === true) return;
        }
        respond(undefined);
      });
    },
  };

  // ------------------------------------------------------------------ what the app pushes
  const DONE = "processedAppSnapshots";
  async function applySnapshot(m) {
    const seen = new Set(read(DONE) || []);
    if (m.snapshotId && seen.has(m.snapshotId)) return;
    await refreshTabs();
    const list = (await runtime.sendMessage({ type: "listAttendance" }))?.sessions || {};
    let session = Object.values(list).find(s => s.active && !s.finalized && s.source?.tabId === m.tabId);
    if (!session) {
      // A meeting already finalized here is never restarted by the app.
      if (Object.values(list).some(s => s.finalized && s.source?.tabId === m.tabId)) return;
      const started = await runtime.sendMessage({ type: "startAttendance", sourceTabId: m.tabId, roster: m.roster || [] });
      session = started?.session;
      if (!session) return;
    }
    await runtime.sendMessage({ type: "attendanceSnapshot", sessionId: session.id, sourceTabId: m.tabId, url: m.url, names: m.names || [], capturedAt: m.capturedAt });
    if (m.snapshotId) { seen.add(m.snapshotId); localStorage.setItem(PREFIX + DONE, JSON.stringify([...seen].slice(-6000))); }
    // Nothing on screen yet: show the meeting the app is recording.
    try {
      if (m.live && typeof session !== "undefined" && !globalThis.__zaaShown) {
        const picker = document.getElementById("meetingPicker");
        if (picker && !picker.value) { globalThis.__zaaShown = true; setTimeout(() => { picker.value = sessionIdFor(m.tabId) || ""; picker.onchange?.(); }, 300); }
      }
    } catch { }
  }
  function sessionIdFor(tabId) {
    const all = read("attendanceSessions") || {};
    return Object.values(all).find(s => s.active && !s.finalized && s.source?.tabId === tabId)?.id;
  }

  // Who the app's auto admit let in, by name, as the extension counts its own Admit clicks:
  // each admit is one click, and a named one is added to that person's admissions this session.
  async function applyAdmissions(m) {
    const all = read("attendanceSessions") || {};
    const session = Object.values(all).find(s => s.active && !s.finalized && s.source?.tabId === m.tabId);
    if (!session) return;
    const applied = new Set(session.appAdmissionIds || []);
    let changed = false;
    for (const e of m.entries || []) {
      if (!e?.id || applied.has(e.id)) continue;
      applied.add(e.id); changed = true;
      session.admitClickCount = (session.admitClickCount || 0) + 1;
      const name = typeof e.name === "string" ? e.name.trim() : "";
      if (!name || name.length > 120) continue;
      const key = name.toLocaleLowerCase(), at = e.at || new Date().toISOString();
      const prior = (session.admissions ||= {})[key];
      session.admissions[key] = { name: prior?.name || name, count: (prior?.count || 0) + 1, lastAdmittedAt: at, admissionTimes: [...(prior?.admissionTimes || []), at] };
    }
    if (!changed) return;
    session.appAdmissionIds = [...applied].slice(-4000);
    all[session.id] = session;
    await local.set({ attendanceSessions: all });
  }

  // Night or day, as the app is (also given in the page address so the first paint is right).
  function setTheme(dark) { document.documentElement.dataset.theme = dark ? "dark" : "light"; }
  try { const t = new URLSearchParams(location.search).get("theme"); if (t) setTheme(t === "dark"); } catch { }

  // The page's own results go back to the app every few seconds: the LMS attendance the app
  // uploads is what this page shows (Present), including every confirmation and correction made here.
  setInterval(() => {
    try {
      if (!webview || typeof calculate !== "function" || typeof session === "undefined" || !session?.source) return;
      const matches = calculate(), roster = session.roster || [];
      const present = [], review = [], absent = [];
      roster.forEach((name, index) => { const m = matches[index]; (m ? (m.review ? review : present) : absent).push(name); });
      webview.postMessage({ method: "results", params: { tabId: session.source.tabId, present, review, absent, finalized: Boolean(session.finalized), meeting: session.name } });
    } catch (e) { console.error(e); }
  }, 15000);
  let queue = Promise.resolve();
  function onPush(m) {
    queue = queue.catch(() => { }).then(async () => {
      if (m.push === "snapshot") await applySnapshot(m);
      else if (m.push === "admissions") await applyAdmissions(m);
      else if (m.push === "theme") setTheme(Boolean(m.dark));
      else if (m.push === "ended") for (const f of removedListeners) f(m.tabId, {});
      else if (m.push === "rosters") {
        // The app's groups appear under Saved rosters, kept in step with the app.
        const saved = read("savedRosters") || {};
        // The app's list is the whole set: an app roster it no longer sends (another person's group) goes.
        const sent = new Set((m.rosters || []).map(r => "app:" + r.group));
        for (const key of Object.keys(saved)) if (key.startsWith("app:") && !sent.has(key)) delete saved[key];
        for (const r of m.rosters || []) saved["app:" + r.group] = { name: r.group + " (app roster)", names: r.names, updatedAt: new Date().toISOString() };
        await local.set({ savedRosters: saved });
      }
    });
  }
  if (webview) webview.addEventListener("message", e => {
    const m = e.data;
    if (m && m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.reject(new Error(m.error)) : p.resolve(m.result); }
    else if (m && m.push) onPush(m);
  });

  const chromeObject = window.chrome || (window.chrome = {});
  chromeObject.storage = { local, onChanged: { addListener: f => storageListeners.push(f) } };
  chromeObject.runtime = runtime;
  chromeObject.tabs = tabsApi;
  chromeObject.action = { async setBadgeBackgroundColor() { }, async setBadgeText() { } };
  chromeObject.scripting = { async executeScript() { return []; } };
  window.addEventListener("load", () => { if (webview) webview.postMessage({ method: "ready" }); });
})();
