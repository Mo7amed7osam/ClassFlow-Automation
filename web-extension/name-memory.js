// Persistent name links use official identities, never movable roster indexes.
const NameMemory = (() => {
  const officialKey = value => String(value || "").normalize("NFKD")
    .replace(/[\u064B-\u065F\u0670]/g, "")
    .replace(/[إأآٱ]/g,"ا").replace(/ى/g,"ي").replace(/ة/g,"ه")
    .replace(/[^\p{L}\p{N}]+/gu," ").trim().toLocaleLowerCase();
  const zoomKey = value => String(value || "").normalize("NFC").replace(/\s+/g," ").trim().toLocaleLowerCase();
  const pairKey = (official, zoom) => JSON.stringify([officialKey(official),zoomKey(zoom)]);
  function merge(memory, entries) {
    const next = {...memory};
    for (const entry of entries) {
      if (!entry?.officialName || !entry?.zoomName || !["accepted","rejected"].includes(entry.status)) continue;
      const key=pairKey(entry.officialName,entry.zoomName), previous=next[key];
      // Automatic learning cannot undo a user's rejection or explicit decision.
      if(entry.source!=="manual" && previous) continue;
      next[key]={officialName:entry.officialName,zoomName:entry.zoomName,status:entry.status,source:entry.source,updatedAt:new Date().toISOString()};
    }
    return next;
  }
  return {officialKey,zoomKey,pairKey,merge};
})();
