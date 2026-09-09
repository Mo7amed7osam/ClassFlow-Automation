const MeetingSource = {
  key(value) {
    try {
      const url=new URL(value);
      if(url.protocol!=="https:" || !/(^|\.)zoom\.us$/i.test(url.hostname)) return null;
      const match=url.pathname.match(/\/(?:wc\/(?:join\/)?|j\/|s\/|w\/)(\d{6,15})(?:\/|$)/);
      const query=url.searchParams.get("confno") || url.searchParams.get("meetingId");
      const number=match?.[1] || (/^\d{6,15}$/.test(query || "") ? query : null);
      return number ? "zoom:"+number : null;
    } catch { return null; }
  }
};
