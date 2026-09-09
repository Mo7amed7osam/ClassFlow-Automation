function renderMeetingPicker() {
  $("meetingPicker").replaceChildren(new Option("New meeting — choose a Zoom tab", ""));
  Object.values(attendanceSessions).sort((a,b)=>(b.startedAt || "").localeCompare(a.startedAt || "")).forEach(meeting=>{
    const date=meeting.startedAt ? new Date(meeting.startedAt).toLocaleString() : "";
    $("meetingPicker").add(new Option(`${meeting.name} · ${meeting.active?"Recording":meeting.finalized?"Finalized":"Stopped"} · ${date}`,meeting.id));
  });
  $("meetingPicker").value=session?.id || "";
}
async function refreshZoomTabs(preferredTabId) {
  const selected=preferredTabId || Number($("zoomTab").value) || session?.source?.tabId;
  const tabs=await chrome.tabs.query({url:["https://*.zoom.us/wc/*","https://*.zoom.us/j/*","https://*.zoom.us/s/*","https://*.zoom.us/w/*"]});
  $("zoomTab").replaceChildren(new Option("Choose Zoom meeting", ""));
  const available=tabs.filter(tab=>MeetingSource.key(tab.url));
  for(const tab of available) {
    const meetingKey=MeetingSource.key(tab.url);
    $("zoomTab").add(new Option(`${tab.title || "Zoom"} · ${meetingKey.slice(5)} · tab ${tab.id}`,String(tab.id)));
  }
  if(available.some(tab=>tab.id===selected)) $("zoomTab").value=String(selected);
  else if(available.length===1) $("zoomTab").value=String(available[0].id);
  if(!available.length) $("meetingStatus").textContent="Join a Zoom meeting, then refresh Zoom tabs. No meeting is recording until you start it here.";
}
$("refreshZoomTabs").onclick=()=>refreshZoomTabs();
$("meetingPicker").onchange=async()=>{
  const selectedId=$("meetingPicker").value;
  cancelAI();await memoryWrites;
  session=attendanceSessions[selectedId] || null;rosterDirty=false;
  renderMeetingPicker();render();
  if(session?.source) $("zoomTab").value=String(session.source.tabId);
  $("meetingStatus").textContent=session ? `${session.name}: only this meeting's names and attendance are shown.` : "Choose a Zoom tab and roster, then start a new meeting. Previous meetings remain saved.";
};
chrome.storage.onChanged.addListener((changes,area)=>{
  if(area!=="local") return;
  if(changes.nameMemory) nameMemory=changes.nameMemory.newValue || {};
  if(changes.savedRosters) {savedRosters=changes.savedRosters.newValue || {};renderSavedRosters();}
  if(changes.admissionHistory) admissionHistory=changes.admissionHistory.newValue || {};
  if(changes.attendanceSessions) {
    attendanceSessions=changes.attendanceSessions.newValue || {};
    if(session?.id) session=attendanceSessions[session.id] || null;
    renderMeetingPicker();
  }
  if(changes.attendanceSessions || changes.nameMemory || changes.admissionHistory) render();
});
async function initializeMeetings() {
  try {
    const [stored,reply]=await Promise.all([
      chrome.storage.local.get({savedRosters:{},nameMemory:{},admissionHistory:{},openRouterAPIKey:"",openRouterModel:DEFAULT_MODEL}),
      chrome.runtime.sendMessage({type:"listAttendance"})
    ]);
    if(!reply?.ok) throw new Error(reply?.error || "Could not load meetings. Reload the extension.");
    attendanceSessions=reply.sessions;savedRosters=stored.savedRosters;nameMemory=stored.nameMemory;admissionHistory=stored.admissionHistory;
    apiKey=stored.openRouterAPIKey;model=stored.openRouterModel;$("model").value=model;
    if(apiKey) $("keyStatus").textContent=`Key stored locally · ending ${apiKey.slice(-4)}`;
    const params=new URLSearchParams(location.search),tabId=Number(params.get("sourceTabId"));
    // Never silently load an old meeting as today's attendance.
    session=attendanceSessions[params.get("sessionId")] || Object.values(attendanceSessions).find(s=>s.active && s.source?.tabId===tabId) || null;
    renderSavedRosters();renderMeetingPicker();render();await refreshZoomTabs(tabId);
  } catch(error) {$("meetingStatus").textContent=error.message;}
}
initializeMeetings();
