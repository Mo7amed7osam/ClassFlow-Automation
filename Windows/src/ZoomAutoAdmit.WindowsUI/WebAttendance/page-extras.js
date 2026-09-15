// What the app adds to the extension's page, without touching its logic (attendance.js):
//   - a click on Present / Absent / Needs review shows only those people in Results
//     (click again, or "Show everyone", to clear); Zoom names jumps to the captured names;
//   - the page follows the app's night/day switch (the app sets data-theme on <html>).
(() => {
  const LABELS = { present: "Present", absent: "Absent", review: "Needs review" };
  const card = document.getElementById("resultsCard");
  const bar = document.getElementById("filterBar");
  const text = document.getElementById("filterText");
  const stats = [...document.querySelectorAll(".stats article[data-filter]")];
  let filter = "";
  try { filter = sessionStorage.getItem("zaa-filter") || ""; } catch { }

  function countShown() {
    if (!filter) return 0;
    return [...document.querySelectorAll("#results tr")].filter(tr => tr.querySelector(".badge." + filter)).length;
  }

  function apply() {
    if (filter) card.dataset.filter = filter; else delete card.dataset.filter;
    for (const s of stats) {
      const on = s.dataset.filter === filter;
      s.classList.toggle("on", on);
      s.classList.toggle("dim", Boolean(filter) && !on && s.dataset.filter !== "observed");
      s.setAttribute("aria-pressed", String(on));
    }
    bar.classList.toggle("on", Boolean(filter));
    if (filter) {
      const n = countShown();
      text.textContent = n ? `Showing only ${LABELS[filter]} · ${n} ${n === 1 ? "student" : "students"}` : `Nobody is ${LABELS[filter]} right now.`;
    }
    try { sessionStorage.setItem("zaa-filter", filter); } catch { }
  }

  function choose(kind) {
    if (kind === "observed") {
      const target = document.getElementById("capturedCard");
      target.scrollIntoView({ behavior: "smooth", block: "start" });
      target.classList.remove("flash"); void target.offsetWidth; target.classList.add("flash");
      return;
    }
    filter = filter === kind ? "" : kind;
    apply();
    if (filter) card.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  for (const s of stats) {
    s.addEventListener("click", () => choose(s.dataset.filter));
    s.addEventListener("keydown", e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); choose(s.dataset.filter); } });
  }
  document.getElementById("clearFilter").addEventListener("click", () => { filter = ""; apply(); });
  // Results are rebuilt whenever a read arrives; keep the count in the bar true.
  new MutationObserver(() => { if (filter) apply(); }).observe(document.getElementById("results"), { childList: true });
  apply();

  // ---------------------------------------------------------------- the roster follows the meeting
  // Choosing a Zoom meeting (named after its class, e.g. "CAI5_AIS4_S8 · Mon 14 Sep 19:00") loads
  // that group's roster, unless someone typed or imported another roster by hand.
  const zoomTab = document.getElementById("zoomTab");
  const rosterPicker = document.getElementById("savedRosters");
  const rosterBox = document.getElementById("roster");
  function autoRoster() {
    const group = window.__zaaGroupOf?.(zoomTab.selectedOptions[0]?.text || "");
    if (!group) return;
    const key = "app:" + group;
    if (![...rosterPicker.options].some(o => o.value === key)) return;
    if (rosterPicker.value === key && rosterBox.value.trim()) return;
    if (rosterBox.value.trim() && !rosterPicker.value.startsWith("app:") && rosterPicker.value !== "") return;
    rosterPicker.value = key;
    rosterPicker.dispatchEvent(new Event("change"));
    document.getElementById("loadRoster").click();
  }
  zoomTab.addEventListener("change", autoRoster);
  new MutationObserver(() => setTimeout(autoRoster, 0)).observe(zoomTab, { childList: true });
  new MutationObserver(() => setTimeout(autoRoster, 0)).observe(rosterPicker, { childList: true });

  // ---------------------------------------------------------------- a group's roster from the LMS
  // The app opens one of the group's sessions on the LMS (this month's first) with the LMS account
  // in use, reads its attendance list without changing it, and saves the students as the group's
  // roster - which then shows under Saved rosters as "<group> (app roster)" and is loaded here.
  if (window.chrome?.webview && window.__zaaHost) {
    const host = window.__zaaHost;
    const savedLabel = document.querySelector("label[for=savedRosters]");
    const status = document.getElementById("rosterStatus");
    const label = document.createElement("label");
    label.htmlFor = "lmsGroup"; label.textContent = "Students from the LMS";
    const row = document.createElement("div");
    row.className = "inline lms-row";
    row.innerHTML = '<select id="lmsGroup" aria-label="Group on the LMS"><option value="">Choose a group</option></select>' +
      '<button id="lmsRoster" class="primary" title="Reads the group\'s students from one of its sessions this month and saves them as its roster">Get from LMS</button>';
    savedLabel.parentNode.insertBefore(label, savedLabel);
    savedLabel.parentNode.insertBefore(row, savedLabel);
    const picker = row.querySelector("select"), button = row.querySelector("button");

    async function fillGroups() {
      let groups = [];
      try { groups = await host("lmsGroups"); } catch { }
      const keep = picker.value || window.__zaaGroupOf?.(zoomTab.selectedOptions[0]?.text || "") || "";
      picker.replaceChildren(new Option("Choose a group", ""), ...(groups || []).map(g => new Option(g, g)));
      if (keep && (groups || []).includes(keep)) picker.value = keep;
    }
    fillGroups();
    zoomTab.addEventListener("change", () => {
      const group = window.__zaaGroupOf?.(zoomTab.selectedOptions[0]?.text || "");
      if (group && [...picker.options].some(o => o.value === group)) picker.value = group;
    });

    button.addEventListener("click", async () => {
      const group = picker.value;
      if (!group) { status.textContent = "Choose the group first."; picker.focus(); return; }
      button.disabled = picker.disabled = true;
      const label0 = button.textContent;
      button.textContent = "Reading the LMS…";
      status.textContent = `Opening ${group}'s sessions on the LMS and reading the attendance list. This takes about a minute; nothing is changed on the LMS.`;
      try {
        const reply = await host("lmsRoster", { group }, 420000);
        status.textContent = reply?.message || "Done.";
        if (reply?.ok) {
          const key = "app:" + reply.group;
          // The roster arrives with the app's next push; wait for it, then load it.
          for (let i = 0; i < 20; i++) {
            if (typeof savedRosters !== "undefined" && savedRosters[key]?.names?.length >= (reply.count || 1)) break;
            await new Promise(r => setTimeout(r, 250));
          }
          if ([...rosterPicker.options].some(o => o.value === key)) {
            rosterPicker.value = key;
            rosterPicker.dispatchEvent(new Event("change"));
            const load = document.getElementById("loadRoster");
            if (!load.disabled) load.click();
            setTimeout(() => { status.textContent = reply.message; }, 50);
          }
        }
      } catch (e) {
        status.textContent = "The LMS roster could not be read: " + e.message;
      } finally {
        button.disabled = picker.disabled = false;
        button.textContent = label0;
      }
    });
  }

  // ---------------------------------------------------------------- one AI key
  if (window.chrome?.webview) {
    const card = document.getElementById("apiKey")?.closest("article");
    if (card) {
      card.querySelector("h2").textContent = "AI matching";
      card.querySelector(".section-title p").textContent = "Reviews uncertain matches with the app's own AI key and model (AI Engine page) - one key for the whole app.";
      const badge = card.querySelector(".privacy"); if (badge) badge.textContent = "App key";
      for (const el of [card.querySelector("label[for=apiKey]"), document.getElementById("apiKey")?.closest(".inline"), card.querySelector("label[for=model]"), document.getElementById("model")]) if (el) el.hidden = true;
      const status = document.getElementById("keyStatus");
      const say = () => { const text = "Uses the key saved on the AI Engine page. No key is kept in this page."; if (status.textContent !== text) status.textContent = text; };
      say();
      new MutationObserver(say).observe(status, { childList: true, characterData: true, subtree: true });
    }
  }
})();
