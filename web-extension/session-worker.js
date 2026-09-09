importScripts("name-memory.js", "meeting-source.js");
let writes=Promise.resolve();
function serialize(action) {const result=writes.catch(()=>{}).then(action);writes=result.catch(()=>{});return result;}
async function paintBadge(count) {
  await chrome.action.setBadgeBackgroundColor({color:"#2563EB"});
  await chrome.action.setBadgeText({text:count>0?String(Math.min(count,999)):""});
}
async function sessionsStore() {
  const stored=await chrome.storage.local.get({attendanceSessions:{},attendanceSession:null,meetingSequence:0,sessionsMigrated:false});
  if(!stored.sessionsMigrated) {
    if(stored.attendanceSession) {
      const old=stored.attendanceSession,id=old.id || crypto.randomUUID();
      stored.attendanceSessions[id]={...old,id,name:"Previous meeting (unlinked)",active:false,source:null,legacy:true,endedAt:old.endedAt || new Date().toISOString()};
    }
    await chrome.storage.local.set({attendanceSessions:stored.attendanceSessions,sessionsMigrated:true,attendanceEnabled:false});
    await chrome.storage.local.remove(["attendanceSession","waitingAdmissions","admissionDiagnostics"]);
  }
  return stored;
}
async function saveSession(session) {
  const {attendanceSessions}=await sessionsStore();attendanceSessions[session.id]=session;
  await chrome.storage.local.set({attendanceSessions});
}
async function getSession(id) {return (await sessionsStore()).attendanceSessions[id];}
async function sourceFor(message,sender) {
  const tabId=sender.tab?.id ?? message.sourceTabId;
  if(!Number.isInteger(tabId)) return null;
  let tab;try {tab=await chrome.tabs.get(tabId);} catch {return null;}
  const meetingKey=MeetingSource.key(tab.url);if(!meetingKey) return null;
  const sentKey=MeetingSource.key(sender.tab?.url || message.sourceURL || message.url),frameKey=MeetingSource.key(message.url);
  if((sentKey && sentKey!==meetingKey)||(frameKey && frameKey!==meetingKey)) return null;
  return {tabId,meetingKey};
}
async function routedSession(message,sender) {
  const source=await sourceFor(message,sender);if(!source || !message.sessionId) return null;
  const session=await getSession(message.sessionId);
  return session?.active && !session.finalized && session.source?.tabId===source.tabId && session.source?.meetingKey===source.meetingKey ? session : null;
}
async function startAttendance(message) {
  const source=await sourceFor(message,{});
  if(!source) throw new Error("Choose an open Zoom meeting tab with a meeting number. Refresh the tab list after joining Zoom.");
  const {attendanceSessions,meetingSequence}=await sessionsStore();
  const existing=Object.values(attendanceSessions).find(s=>s.active && s.source?.tabId===source.tabId && s.source?.meetingKey===source.meetingKey);
  if(existing) return {ok:true,session:existing,existing:true};
  for(const old of Object.values(attendanceSessions)) if(old.active && old.source?.tabId===source.tabId) {old.active=false;old.endedAt=new Date().toISOString();}
  const sequence=meetingSequence+1;
  const session={id:crypto.randomUUID(),name:"Meeting "+sequence,sequence,source,active:true,startedAt:new Date().toISOString(),endedAt:null,roster:message.roster || [],observed:{},snapshots:0,admissions:{},waitingAdmissions:{},admissionDiagnostics:{},aiMatches:{},manualMatches:{},rejectedMatches:{}};
  attendanceSessions[session.id]=session;await chrome.storage.local.set({attendanceSessions,meetingSequence:sequence});return {ok:true,session};
}
async function updateAttendance(message) {
  const current=await getSession(message.sessionId);
  if(!current) throw new Error("Select a meeting first.");
  if(current.finalized) throw new Error("This meeting is finalized.");
  const allowed=["roster","aiMatches","manualMatches","rejectedMatches","observed","snapshots","lastCapturedAt","endedAt"];
  const patch=Object.fromEntries(allowed.filter(k=>Object.hasOwn(message.patch || {},k)).map(k=>[k,message.patch[k]]));
  if(message.patch?.active===false) patch.active=false;
  const session={...current,...patch};await saveSession(session);return {ok:true,session};
}
async function snapshot(message,sender) {
  const session=await routedSession(message,sender);if(!session) return {ok:true,ignored:true};
  const now=new Date().toISOString();
  for(const raw of new Set(message.names || [])) {
    const name=String(raw).trim();if(!name || name.length>120) continue;
    const key=name.toLocaleLowerCase(),prior=session.observed[key];
    session.observed[key]={name:prior?.name || name,firstSeenAt:prior?.firstSeenAt || now,lastSeenAt:now,sightings:(prior?.sightings || 0)+1};
  }
  session.snapshots=(session.snapshots||0)+1;session.lastCapturedAt=now;await saveSession(session);
  const names=new Set((message.names||[]).map(n=>String(n).trim().toLocaleLowerCase()));
  for(const event of Object.values(session.waitingAdmissions || {})) if(Date.now()-event.at<=120000 && names.has(event.name.toLocaleLowerCase())) await recordAdmission({...message,eventId:event.eventId},sender);
  return {ok:true};
}

async function waitingRoomSnapshot(message,sender) {
  const session=await routedSession(message,sender);
  if(!session) return {ok:true,ignored:true};
  session.waitingRoom ||= {};
  session.waitingFrames ||= {};
  session.waitingFrames[sender.frameId ?? 0]={names:message.names || [],at:Date.now()};
  const now=new Date().toISOString();
  for(const raw of new Set(message.names || [])) {
    const name=String(raw).trim();
    if(!name || name.length>120) continue;
    const key=name.toLocaleLowerCase();
    session.waitingRoom[key]={name,firstSeenAt:session.waitingRoom[key]?.firstSeenAt || now,lastSeenAt:now};
  }
  await saveSession(session);
  return {ok:true};
}

async function admissionAttempt(message,sender) {
  const session=await routedSession(message,sender);if(!session) return {ok:true,ignored:true};
  const {admissionEventIds=[]}=await chrome.storage.local.get("admissionEventIds");session.waitingAdmissions ||= {};
  if(message.clickId && (session.admitClickIds || []).includes(message.clickId)) return {ok:true};
  for(const [id,event] of Object.entries(session.waitingAdmissions)) if(Date.now()-event.at>120000) delete session.waitingAdmissions[id];
  const events=(message.events||[]).filter(e=>e.eventId && typeof e.name==="string" && e.name.trim() && e.name.length<=120);
  // The Admit notification and Participants list can live in different frames.
  if(!events.length) {
    const candidates=[...new Set(Object.values(session.waitingFrames || {}).filter(f=>Date.now()-f.at<5000).flatMap(f=>f.names))]
      .filter(name=>typeof name==='string' && name.trim() && name.length<=120)
      .filter(name=>!Object.values(session.waitingAdmissions).some(e=>e.name.toLocaleLowerCase()===name.toLocaleLowerCase()));
    if(candidates.length===1 || (message.kind==='admitAll' && candidates.length)) {
      for(const name of candidates) events.push({name,eventId:crypto.randomUUID(),at:Date.now()});
    }
  }
  if(message.clickId && !(session.admitClickIds || []).includes(message.clickId)) {
    session.admitClickCount=(session.admitClickCount || 0)+1;
    session.admitClickIds=[...(session.admitClickIds || []).slice(-1999),message.clickId];
  }
  for(const frame of Object.values(session.waitingFrames || {})) frame.names=frame.names.filter(name=>!events.some(e=>e.name.toLocaleLowerCase()===String(name).toLocaleLowerCase()));
  for(const event of events) if(!session.waitingAdmissions[event.eventId] && !admissionEventIds.includes(event.eventId)) session.waitingAdmissions[event.eventId]={eventId:event.eventId,name:event.name.trim(),at:Number.isFinite(event.at) && event.at<=Date.now() && event.at>=Date.now()-120000 ? event.at : Date.now()};
  session.admissionDiagnostics={lastAttemptAt:new Date().toISOString(),lastAttemptNames:events.map(e=>e.name),lastIssue:events.length?"":"An Admit click was detected, but its waiting-room name could not be read. Expand the waiting-room list."};
  await saveSession(session);
  for(const event of events) await recordAdmission({...message,eventId:event.eventId,attempt:true},sender);
  return {ok:true};
}
async function recordAdmission(message,sender) {
  const session=await routedSession(message,sender);if(!session) return {ok:true,ignored:true};
  const event=session.waitingAdmissions?.[message.eventId];if(!event || Date.now()-event.at>120000) return {ok:true,ignored:true};
  const {admissionHistory={},admissionEventIds=[]}=await chrome.storage.local.get(["admissionHistory","admissionEventIds"]);
  if(admissionEventIds.includes(event.eventId)) {
    if(!message.attempt) {
      const key=event.name.toLocaleLowerCase(),now=new Date().toISOString();
      session.observed[key] ||= {name:event.name,firstSeenAt:now,lastSeenAt:now,sightings:1};
      delete session.waitingAdmissions[event.eventId];
      await saveSession(session);
    }
    return {ok:true};
  }
  const name=event.name,key=name.toLocaleLowerCase(),now=new Date(event.at).toISOString();
  admissionHistory[key]={name,count:(admissionHistory[key]?.count||0)+1,lastAdmittedAt:now,admissionTimes:[...(admissionHistory[key]?.admissionTimes||[]),now]};
  session.admissions[key]={name,count:(session.admissions[key]?.count||0)+1,lastAdmittedAt:now,admissionTimes:[...(session.admissions[key]?.admissionTimes||[]),now]};
  if(!message.attempt) {
    session.observed[key] ||= {name,firstSeenAt:now,lastSeenAt:now,sightings:1};
    delete session.waitingAdmissions[event.eventId];
  }
  const {attendanceSessions}=await sessionsStore();attendanceSessions[session.id]=session;
  await chrome.storage.local.set({attendanceSessions,admissionHistory,admissionEventIds:[...admissionEventIds.slice(-1999),event.eventId]});return {ok:true};
}
async function finalizeAttendance(message) {
  const current=await getSession(message.sessionId);if(!current) throw new Error("Select a meeting first.");
  if(JSON.stringify(current.roster)!==JSON.stringify(message.roster)) throw new Error("The roster changed. Please finalize again.");
  if(current.finalized) return {ok:true,session:current};
  const {admissionHistory={}}=await chrome.storage.local.get("admissionHistory");
  const finalMatches={},finalAssignments={},observed={},allTime={};
  for(const [index,match] of Object.entries(message.matches || {})) {
    const entry=current.observed?.[match.observedKey];if(!current.roster[index] || !entry) continue;
    finalMatches[index]=match;finalAssignments[match.observedKey]={index:Number(index),review:match.review,additional:false};
    observed[match.observedKey]={name:entry.name};allTime[match.observedKey]=admissionHistory[match.observedKey]?.count || 0;
  }
  for(const [key,link] of Object.entries(message.assignments || {})) {
    if(finalAssignments[key] || !link.additional || link.review || !current.observed[key] || !finalMatches[link.index] || finalMatches[link.index].review) continue;
    finalAssignments[key]={index:link.index,review:false,additional:true};observed[key]={name:current.observed[key].name};allTime[key]=admissionHistory[key]?.count || 0;
  }
  const now=new Date().toISOString();
  const session={id:current.id,name:current.name,sequence:current.sequence,source:current.source,roster:current.roster,active:false,startedAt:current.startedAt,endedAt:now,finalized:true,finalizedAt:now,finalMatches,finalAssignments,finalAdmissionsAllTime:allTime,admitClickCount:current.admitClickCount || 0,observed,admissions:current.admissions || {}};
  await saveSession(session);return {ok:true,session};
}
const supported=["admitted","waitingRoomSnapshot","attendanceSnapshot","admissionAttempt","admissionConfirmed","updateAttendance","saveNameMemory","finalizeAttendance","startAttendance","listAttendance","attendanceContext"];
chrome.runtime.onMessage.addListener((message,sender,sendResponse)=>{
  if(!supported.includes(message?.type)) return false;
  serialize(async()=>{
    if(message.type==="listAttendance") return {ok:true,sessions:(await sessionsStore()).attendanceSessions};
    if(message.type==="attendanceContext") {
      const source=await sourceFor(message,sender),{attendanceSessions}=await sessionsStore();
      const session=source && Object.values(attendanceSessions).find(s=>s.active && !s.finalized && s.source?.tabId===source.tabId && s.source?.meetingKey===source.meetingKey);
      return {ok:true,sessionId:session?.id || null,meetingKey:source?.meetingKey || null};
    }
    if(message.type==="startAttendance") return startAttendance(message);
    if(message.type==="updateAttendance") return updateAttendance(message);
    if(message.type==="finalizeAttendance") return finalizeAttendance(message);
    if(message.type==="waitingRoomSnapshot") return waitingRoomSnapshot(message,sender);
    if(message.type==="attendanceSnapshot") return snapshot(message,sender);
    if(message.type==="admissionAttempt") return admissionAttempt(message,sender);
    if(message.type==="admissionConfirmed") return recordAdmission(message,sender);
    if(message.type==="saveNameMemory") {const {nameMemory={}}=await chrome.storage.local.get("nameMemory");const memory=NameMemory.merge(nameMemory,message.entries || []);await chrome.storage.local.set({nameMemory:memory});return {ok:true,memory};}
    const {admittedCount=0}=await chrome.storage.local.get("admittedCount");await chrome.storage.local.set({admittedCount:admittedCount+1});await paintBadge(admittedCount+1);return {ok:true};
  }).then(sendResponse,error=>sendResponse({ok:false,error:error.message}));return true;
});
async function stopSources(predicate) {
  const {attendanceSessions}=await sessionsStore();let changed=false;
  for(const session of Object.values(attendanceSessions)) if(session.active && predicate(session.source)) {session.active=false;session.endedAt=new Date().toISOString();session.waitingAdmissions={};changed=true;}
  if(changed) await chrome.storage.local.set({attendanceSessions});
}
chrome.tabs.onRemoved.addListener(tabId=>{serialize(()=>stopSources(source=>source?.tabId===tabId));});
chrome.tabs.onUpdated.addListener((tabId,change)=>{if(change.url) serialize(()=>stopSources(source=>source?.tabId===tabId && source.meetingKey!==MeetingSource.key(change.url)));});
chrome.runtime.onStartup.addListener(()=>{serialize(()=>stopSources(()=>true));});
chrome.storage.onChanged.addListener((changes,area)=>{if(area==="local"&&changes.admittedCount) paintBadge(changes.admittedCount.newValue || 0);});
