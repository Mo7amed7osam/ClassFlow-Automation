// The same normalization as the app's Swift NameNormalizer, so a name the app
// decided was present is compared with the dashboard's row the same way.

const ROLE_SUFFIXES = ["(host, me)", "(host)", "(me)", "(guest)", "(co-host)", "(you)"];

export function normalizeArabic(value) {
  let out = "";
  for (const char of value) {
    const code = char.codePointAt(0);
    if (code === 0x0623 || code === 0x0625 || code === 0x0622 || code === 0x0671) out += "ا";
    else if (code === 0x0649) out += "ي";
    else if (code === 0x0629) out += "ه";
    else if (code === 0x0640) continue;
    else if (
      (code >= 0x064b && code <= 0x0652) ||
      code === 0x0670 ||
      (code >= 0x0653 && code <= 0x065f) ||
      (code >= 0x06d6 && code <= 0x06ed)
    ) continue;
    else out += char;
  }
  return out;
}

export function normalizeName(raw) {
  let value = String(raw ?? "").normalize("NFKC").toLowerCase();
  for (const suffix of ROLE_SUFFIXES) {
    if (value.endsWith(suffix)) value = value.slice(0, -suffix.length);
  }
  value = normalizeArabic(value);
  value = value.replace(/[_\-.]+/g, " ");
  value = value.replace(/[^\p{L}\p{N}\p{M}\s]/gu, "");
  value = value.replace(/\s+/g, " ");
  return value.trim();
}
