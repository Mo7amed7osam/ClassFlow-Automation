// Button labels the content script looks for, per Zoom UI language.
// Matching is exact (after whitespace/case normalization) so unrelated buttons
// that merely contain the word "admit" are never pressed.
const ZAA_LABELS = {
  admitAll: [
    "admit all",
    "admit all participants",
    "قبول الكل",
    "السماح للجميع",
    "admitir a todos",
    "admettre tous",
    "alle zulassen",
    // Shown to a host who is inside a breakout room.
    "admit all to main session",
    "admit all to the main session",
    "قبول الكل في الجلسة الرئيسية",
    "قبول الكل إلى الجلسة الرئيسية"
  ],
  admit: [
    "admit",
    "قبول",
    "السماح",
    "admitir",
    "admettre",
    "zulassen",
    // Shown to a host who is inside a breakout room.
    "admit to main session",
    "admit to the main session",
    "admit to main room",
    "قبول في الجلسة الرئيسية",
    "قبول إلى الجلسة الرئيسية",
    "السماح بالدخول إلى الجلسة الرئيسية",
    "admitir en la sesión principal",
    "admettre dans la session principale"
  ],
  // Notification buttons that open the waiting-room list when the
  // Participants panel is closed. Never classified as admit controls.
  openWaitingRoom: [
    "see waiting room",
    "view waiting room",
    "open waiting room",
    "عرض غرفة الانتظار",
    "مشاهدة غرفة الانتظار",
    "رؤية غرفة الانتظار"
  ],
  // Labels that must never be pressed, even if a match above also fits.
  blocked: [
    "deny",
    "remove",
    "deny entry",
    "remove from meeting",
    "رفض",
    "إزالة"
  ]
};
