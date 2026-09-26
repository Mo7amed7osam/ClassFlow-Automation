# Production Data Migration Audit Report

Audit conducted against local production files in `~/Library/Application Support/Zoom Auto Admit/` versus the VPS / Central Backend PostgreSQL database.

---

## 1. Executive Summary

| Category | Local Production Source | Cloud / VPS State | Status |
|---|---|---|---|
| **Groups** | `schedules.json` (`CAI5_IND1_G1`, `CAI5_IND1_G2`) | PostgreSQL `groups` table | `MISSING` (needs seeding) |
| **G1 Students** | 25 students in `schedules.json` | PostgreSQL `students` table | `MISSING` (needs seeding) |
| **G2 Students** | 20 students in `schedules.json` | PostgreSQL `students` table | `MISSING` (needs seeding) |
| **Aliases** | 24 aliases across G1 & G2 students | PostgreSQL `student_aliases` table | `MISSING` (needs seeding) |
| **Global Ignore List** | `ignored-participants.json` (3 entries) | Central matching rules / database | `MISSING` (needs implementation & seeding) |
| **Group Ignore Lists** | Group `ignoredParticipantNames` in `schedules.json` | Central matching rules / database | `MISSING` (needs implementation & seeding) |
| **Co-Host Candidates** | Group `coHostCandidates` in `schedules.json` | `UserSchedule` / coordinator config | `MISSING` (needs seeding) |
| **Schedules / Plans** | 6 recurring slots in `schedules.json` | PostgreSQL `class_plans` table | `MISSING` (needs seeding) |
| **Zoom Accounts** | `accountProfiles` in `schedules.json` | PostgreSQL `zoom_accounts` table | `MISSING` (needs seeding) |
| **LMS Accounts** | Local Keychain / Credentials | PostgreSQL `lms_accounts` table | `MISSING` (needs seeding) |
| **Google Sheet Config** | Local sync configuration | `google_sheets_connections` table | `PARTIAL` (table ready, credentials pending) |
| **OpenRouter AI** | Local config | Environment `CENTRAL_AI_API_KEY` | `CONFIGURED_IN_ENV` |

---

## 2. Detailed Roster Audit

### Group 1: `CAI5_IND1_G1` (25 Students)
* **LMS Group Code**: `CAI5_IND1_G1`
* **Zoom Account**: `depi+10@eyouthlearning.com`
* **Zoom Meeting ID**: `94698416251` (`https://zoom.us/j/94698416251`)
* **Group Ignores**: `Mohamed Hosam`, `eyouth coordinator`, `Ibrahim Mohamed`
* **Co-Host Candidates**: `Yossef ayoub`, `Ibrahim Mohamed`, `Youssef Ayoub`

| # | Official Student Name | Aliases in Production | Migration Status |
|---|---|---|---|
| 1 | `AYMAN ABDELFATTAH MAHMOUD ZYAN` | `Dr. AYMAN ABDELFATTAH`, `Dr. Ayman Zyan` | Missing in DB |
| 2 | `Abeer Mohammed Abu Elhassan Elsayed` | `عبير`, `عبير. محمد` | Missing in DB |
| 3 | `Ahmed Mostafa Mohamed Abd Allah` | `Ahmed Mostafa` | Missing in DB |
| 4 | `Ahmed Mumdoh Abd El Rahim Mohammed` | *(none)* | Missing in DB |
| 5 | `Alaa AbdAllah Ahmed Mohamed Barakat` | `Alaa Barakat` | Missing in DB |
| 6 | `seham Mohamed helmy ibrahem rezk` | *(none)* | Missing in DB |
| 7 | `Aml Anter Mohamed Khalil` | *(none)* | Missing in DB |
| 8 | `Elham Bakrey Mohammed Khesha` | *(none)* | Missing in DB |
| 9 | `Karima Mahmoud Awad Allah Ahmed` | `KariMa MahMoud` | Missing in DB |
| 10 | `Maiada Hatem Yassen Farahat` | `Maiada Yassen` | Missing in DB |
| 11 | `Marim Mahmoud Mahmoud Wahba` | `Maryam Mahmoud` | Missing in DB |
| 12 | `Martina Fakhry Sobhy Farag` | `Martina Fakhry` | Missing in DB |
| 13 | `Amany Esmat Mohammed Mahmoud` | *(none)* | Missing in DB |
| 14 | `Hanan Ibrahim Yousef ELShorbagy` | `Dr. Hanan Ibrahim EL-Shorbagy`, `Dr. Hanan I. EL-Shorbagy` | Missing in DB |
| 15 | `Prof Wafaa Osman Abd ElFatah Abd ElHady`| `wafaa_abdulFattah`, `Dr/Wafaa Osman` | Missing in DB |
| 16 | `RAFEEK MAGDY GERGES SALIB` | `رفيق`, `رفيق مجدى جرجس`, `rafeek magdy gerges` | Missing in DB |
| 17 | `Weaam Mohamed Elsayed Ismaiel` | `Dr.Weaam Mohamed Ismael` | Missing in DB |
| 18 | `Zahraa Hagag Abdelsamea Abdelrahman` | `Zahraa Swelim`, `zahraa hagag abdelsamea` | Missing in DB |
| 19 | `Mohamed Hatem Sadek Alsaeid` | `Mohamed Hatem` | Missing in DB |
| 20 | `Nareman Salama AbdElkader Badr` | *(none)* | Missing in DB |
| 21 | `mohamed hamam mohamed tamam` | *(none)* | Missing in DB |
| 22 | `mohamed reda ahmed abdalittef` | `Mohamed Reda abo_alnor`, `Mohamed Reda` | Missing in DB |
| 23 | `Amir Girges Abdou Girges` | `amir abdu`, `Amir Girges` | Missing in DB |
| 24 | `fatma elagamy ali elbestar` | `فاطمة العجمي`, `Fatma elagamy` | Missing in DB |
| 25 | `Ahmed Elsayed Megahed Ahmed` | *(none)* | Missing in DB |

---

### Group 2: `CAI5_IND1_G2` (20 Students)
* **LMS Group Code**: `CAI5_IND1_G2`
* **Zoom Account**: `depi+11@eyouthlearning.com`
* **Zoom Meeting ID**: `91011896947` (`https://zoom.us/j/91011896947`)
* **Group Ignores**: `Mohamed Hosam`, `eyouth coordinator`, `Youssef Ayoub`
* **Co-Host Candidates**: `Yossef ayoub`, `Hussein Farghal`, `Acc Hussein`, `Youssef Ayoub`

| # | Official Student Name | Aliases in Production | Migration Status |
|---|---|---|---|
| 1 | `Aamn Ehab Sayed Mohamed` | `Aamn Ehab` | Missing in DB |
| 2 | `Ahmed Fouad Rashad Elmogany` | *(none)* | Missing in DB |
| 3 | `Ahmed hamed mohamed elfeky` | `Ahmed Hamed` | Missing in DB |
| 4 | `Aliaa Omar elfarouk Ibrahim Eleraky` | `Aliaa Omar`, `Aliaa Omar eLFAROUK` | Missing in DB |
| 5 | `Aya Hussein Mohamed Mkhemar` | *(none)* | Missing in DB |
| 6 | `Doaa Hafez Abbas Shalaby` | `Dr/Doaa Hafez`, `Doaa Hafez` | Missing in DB |
| 7 | `Eman Elsayed AbdAlRahman AbdElAal` | `Eman Elsayed` | Missing in DB |
| 8 | `Esraa Ahmed Helmy Mohammed` | `EsraaAhmedHelmy` | Missing in DB |
| 9 | `Mahmoud Abdelsalam Elsayed Alsisi` | `Mahmoud Al-sisi` | Missing in DB |
| 10 | `Nisrine Mostafa Mohammed Yusuf` | `Nisrine Yusuf` | Missing in DB |
| 11 | `Noha Ahmed Hisham Eissa Morshed` | *(none)* | Missing in DB |
| 12 | `Nouran Hossameldin Aboubakr Badr Hassan`| `Nouran Hossameldin` | Missing in DB |
| 13 | `Sherif Reda Abdel All Ali` | *(none)* | Missing in DB |
| 14 | `Yasmine Mosaad Mosaad Elsokary` | *(none)* | Missing in DB |
| 15 | `abdelrahman khaled abdelmenium younis` | `abdelrahman yonis` | Missing in DB |
| 16 | `ghoson amr samy mohamed` | `Ghoson Amr Samy` | Missing in DB |
| 17 | `Ehab Mohamed Abdellatif Zaki` | `Ehab Abdellatif` | Missing in DB |
| 18 | `khloud mohamed ibrahim ali` | *(none)* | Missing in DB |
| 19 | `mariam kotb mohamed kotb` | `HP`, `Mariam Kotb` | Missing in DB |
| 20 | `Amal Abdelrazik Mohamed AbuRohama` | `Amal Abdelrazek`, `Amal Abd-Elrazek` | Missing in DB |

---

## 3. Global & Group Ignore List Audit

### Global Ignore List (Mac `ignored-participants.json`)
* `eyouth coordinator`
* `depi wavz`
* `youssef ayoub`

**Gap**: The cloud backend (`attendance_names.py`) only strips role suffixes like `(Host)` and `(Co-host)`. The specific static names above are not hardcoded or seeded into a global exclusion table. They must be registered globally so they are never matched to students and never passed to OpenRouter AI.

### Group-Specific Ignore Lists
* **`CAI5_IND1_G1`**: `Mohamed Hosam`, `eyouth coordinator`, `Ibrahim Mohamed`
* **`CAI5_IND1_G2`**: `Mohamed Hosam`, `eyouth coordinator`, `Youssef Ayoub`

**Gap**: Currently, group-level ignore lists from `schedules.json` do not have a dedicated column or relationship in `groups` or `attendance_sessions`.

---

## 4. Timetable / Schedule Audit (6 Recurring Slots)

All times are in `Africa/Cairo` timezone:

| Schedule Name | Group | Day of Week | Start Time | End Time | Meeting URL |
|---|---|---|---|---|---|
| **G1 — Monday** | `CAI5_IND1_G1` | Monday (2) | 17:55 | 21:00 | `https://zoom.us/j/94698416251` |
| **G1 — Wednesday** | `CAI5_IND1_G1` | Wednesday (4) | 17:55 | 21:00 | `https://zoom.us/j/94698416251` |
| **G1 — Saturday** | `CAI5_IND1_G1` | Saturday (7) | 13:55 | 17:00 | `https://zoom.us/j/94698416251` |
| **G2 — Tuesday** | `CAI5_IND1_G2` | Tuesday (3) | 17:55 | 21:00 | `https://zoom.us/j/91011896947` |
| **G2 — Thursday** | `CAI5_IND1_G2` | Thursday (5) | 17:55 | 21:00 | `https://zoom.us/j/91011896947` |
| **G2 — Saturday** | `CAI5_IND1_G2` | Saturday (7) | 17:55 | 21:00 | `https://zoom.us/j/91011896947` |

**Gap**: The cloud `scheduling.py` expects rows in `class_plans` with concrete dates (`session_date`). There is currently no recurring generator in the backend that populates upcoming class dates for these 6 slots.

---

## 5. Discrepancies & Contamination Risks

1. **Youssef Ayoub Spelling**: Appears as `youssef ayoub` in global ignore, `Yossef ayoub` and `Youssef Ayoub` in co-hosts, and ignored in G2. The system must treat normalized `youssef ayoub` as excluded from student matching across all groups.
2. **Ibrahim Mohamed Role Inversion**: `Ibrahim Mohamed` is a co-host candidate and ignored in G1, but must never match any student record.
3. **Cross-Group Isolation**: G1 runs on `depi+10@eyouthlearning.com`, G2 on `depi+11@eyouthlearning.com`. Profile directories (`zoom-{id}`) prevent cross-contamination.

---

## 6. Migration Action Plan

1. **Seed Script / Command**: Implement a CLI command `python -m central_backend.cli seed-production-data` to load G1/G2 groups, 45 students, 24 aliases, and the 6 timetable plans into PostgreSQL.
2. **Global & Group Ignore System**: Embed the global ignore list (`eyouth coordinator`, `depi wavz`, `youssef ayoub`) directly into `attendance_matching.py` and `attendance.py`, and exclude co-host candidates automatically from student matching.
