"""Idempotent production data seeder for ClassFlow Automation.

Seeds:
- Groups CAI5_IND1_G1 and CAI5_IND1_G2
- 25 students for G1 and 20 students for G2 with official names and aliases
- Zoom accounts for depi+10 and depi+11 with default meeting URLs
- RunDelegation for active coordinator
- ClassPlan entries for recurring weekly timetables (Cairo timezone)
"""

from __future__ import annotations

import logging
import uuid
from datetime import UTC, date, datetime, timedelta
from typing import Any
from zoneinfo import ZoneInfo

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .attendance_names import normalize
from .models import (
    ClassPlan,
    Group,
    LmsAccount,
    RunDelegation,
    Student,
    StudentAlias,
    User,
    ZoomAccount,
)

logger = logging.getLogger("classflow.seed")
CAIRO = ZoneInfo("Africa/Cairo")

G1_STUDENTS = [
    ("AYMAN ABDELFATTAH MAHMOUD ZYAN", ["Dr. AYMAN ABDELFATTAH", "Dr. Ayman Zyan"]),
    ("Abeer Mohammed Abu Elhassan Elsayed", ["عبير", "عبير. محمد"]),
    ("Ahmed Mostafa Mohamed Abd Allah", ["Ahmed Mostafa"]),
    ("Ahmed Mumdoh Abd El Rahim Mohammed", []),
    ("Alaa AbdAllah Ahmed Mohamed Barakat", ["Alaa Barakat"]),
    ("seham Mohamed helmy ibrahem rezk", []),
    ("Aml Anter Mohamed Khalil", []),
    ("Elham Bakrey Mohammed Khesha", []),
    ("Karima Mahmoud Awad Allah Ahmed", ["KariMa MahMoud"]),
    ("Maiada Hatem Yassen Farahat", ["Maiada Yassen"]),
    ("Marim Mahmoud Mahmoud Wahba", ["Maryam Mahmoud"]),
    ("Martina Fakhry Sobhy Farag", ["Martina Fakhry"]),
    ("Amany Esmat Mohammed Mahmoud", []),
    ("Hanan Ibrahim Yousef ELShorbagy", ["Dr. Hanan Ibrahim EL-Shorbagy", "Dr. Hanan I. EL-Shorbagy"]),
    ("Prof Wafaa Osman Abd ElFatah Abd ElHady", ["wafaa_abdulFattah", "Dr/Wafaa Osman"]),
    ("RAFEEK MAGDY GERGES SALIB", ["رفيق", "رفيق مجدى جرجس", "rafeek magdy gerges"]),
    ("Weaam Mohamed Elsayed Ismaiel", ["Dr.Weaam Mohamed Ismael"]),
    ("Zahraa Hagag Abdelsamea Abdelrahman", ["Zahraa Swelim", "zahraa hagag abdelsamea"]),
    ("Mohamed Hatem Sadek Alsaeid", ["Mohamed Hatem"]),
    ("Nareman Salama AbdElkader Badr", []),
    ("mohamed hamam mohamed tamam", []),
    ("mohamed reda ahmed abdalittef", ["Mohamed Reda abo_alnor", "Mohamed Reda"]),
    ("Amir Girges Abdou Girges", ["amir abdu", "Amir Girges"]),
    ("fatma elagamy ali elbestar", ["فاطمة العجمي", "Fatma elagamy"]),
    ("Ahmed Elsayed Megahed Ahmed", []),
]

G2_STUDENTS = [
    ("Aamn Ehab Sayed Mohamed", ["Aamn Ehab"]),
    ("Ahmed Fouad Rashad Elmogany", []),
    ("Ahmed hamed mohamed elfeky", ["Ahmed Hamed"]),
    ("Aliaa Omar elfarouk Ibrahim Eleraky", ["Aliaa Omar", "Aliaa Omar eLFAROUK"]),
    ("Aya Hussein Mohamed Mkhemar", []),
    ("Doaa Hafez Abbas Shalaby", ["Dr/Doaa Hafez", "Doaa Hafez"]),
    ("Eman Elsayed AbdAlRahman AbdElAal", ["Eman Elsayed"]),
    ("Esraa Ahmed Helmy Mohammed", ["EsraaAhmedHelmy"]),
    ("Mahmoud Abdelsalam Elsayed Alsisi", ["Mahmoud Al-sisi"]),
    ("Nisrine Mostafa Mohammed Yusuf", ["Nisrine Yusuf"]),
    ("Noha Ahmed Hisham Eissa Morshed", []),
    ("Nouran Hossameldin Aboubakr Badr Hassan", ["Nouran Hossameldin"]),
    ("Sherif Reda Abdel All Ali", []),
    ("Yasmine Mosaad Mosaad Elsokary", []),
    ("abdelrahman khaled abdelmenium younis", ["abdelrahman yonis"]),
    ("ghoson amr samy mohamed", ["Ghoson Amr Samy"]),
    ("Ehab Mohamed Abdellatif Zaki", ["Ehab Abdellatif"]),
    ("khloud mohamed ibrahim ali", []),
    ("mariam kotb mohamed kotb", ["HP", "Mariam Kotb"]),
    ("Amal Abdelrazik Mohamed AbuRohama", ["Amal Abdelrazek", "Amal Abd-Elrazek"]),
]

RECURRING_SLOTS = [
    # G1: Mon 17:55, Wed 17:55, Sat 13:55
    {"group": "CAI5_IND1_G1", "weekday": 0, "start_time": "17:55", "meeting_url": "https://zoom.us/j/94698416251", "zoom_account": "depi+10@eyouthlearning.com"},
    {"group": "CAI5_IND1_G1", "weekday": 2, "start_time": "17:55", "meeting_url": "https://zoom.us/j/94698416251", "zoom_account": "depi+10@eyouthlearning.com"},
    {"group": "CAI5_IND1_G1", "weekday": 5, "start_time": "13:55", "meeting_url": "https://zoom.us/j/94698416251", "zoom_account": "depi+10@eyouthlearning.com"},
    # G2: Tue 17:55, Thu 17:55, Sat 17:55
    {"group": "CAI5_IND1_G2", "weekday": 1, "start_time": "17:55", "meeting_url": "https://zoom.us/j/91011896947", "zoom_account": "depi+11@eyouthlearning.com"},
    {"group": "CAI5_IND1_G2", "weekday": 3, "start_time": "17:55", "meeting_url": "https://zoom.us/j/91011896947", "zoom_account": "depi+11@eyouthlearning.com"},
    {"group": "CAI5_IND1_G2", "weekday": 5, "start_time": "17:55", "meeting_url": "https://zoom.us/j/91011896947", "zoom_account": "depi+11@eyouthlearning.com"},
]


async def seed_production_data(session: AsyncSession, now: datetime | None = None) -> dict[str, int]:
    """Idempotently seed groups, rosters, zoom accounts, and upcoming schedules."""
    now = now or datetime.now(UTC)
    counts = {"groups": 0, "students": 0, "aliases": 0, "zoom_accounts": 0, "plans": 0}

    # 1. Ensure user
    user = (await session.execute(select(User).order_by(User.created_at.asc()).limit(1))).scalar_one_or_none()
    if user is None:
        user = User(
            id=uuid.uuid4(),
            username="admin",
            display_name="Coordinator Admin",
            password_hash="scrypt$32768$8$1$L6zZJM1z8+KGJY8mvOerqQ==$MVu9wGiM3lsnIWtu10BX+f5SdtjVTdSQczHuaYYiEdM=",
            role="admin",
            status="active",
            approved_at=now,
            created_at=now,
            updated_at=now,
        )
        session.add(user)
        await session.flush()

    # 2. Ensure LMS account
    lms_acc = (await session.execute(
        select(LmsAccount).where(LmsAccount.user_id == user.id).limit(1)
    )).scalar_one_or_none()
    if lms_acc is None:
        lms_acc = LmsAccount(
            id=uuid.uuid4(),
            user_id=user.id,
            account_id="depi-lms",
            label="DEPI LMS",
            active=True,
            created_at=now,
            updated_at=now,
        )
        session.add(lms_acc)
        await session.flush()

    # 3. Ensure Groups
    for gcode in ("CAI5_IND1_G1", "CAI5_IND1_G2"):
        existing_g = (await session.execute(select(Group).where(Group.name == gcode))).scalar_one_or_none()
        if existing_g is None:
            session.add(Group(id=uuid.uuid4(), name=gcode, display_name=gcode, created_at=now, updated_at=now))
            counts["groups"] += 1
    await session.flush()

    # 4. Ensure Zoom Accounts
    z_configs = [
        {"id": "32149553-8559-4FCF-8EEA-C4E52FFD5023", "email": "depi+10@eyouthlearning.com", "group": "CAI5_IND1_G1", "url": "https://zoom.us/j/94698416251"},
        {"id": "34AE57B5-1432-4AD3-A595-86C3CC1328AA", "email": "depi+11@eyouthlearning.com", "group": "CAI5_IND1_G2", "url": "https://zoom.us/j/91011896947"},
    ]
    first_zoom_id = None
    for z in z_configs:
        z_id = uuid.UUID(z["id"])
        if first_zoom_id is None:
            first_zoom_id = z_id
        existing_z = await session.get(ZoomAccount, z_id)
        if existing_z is None:
            session.add(ZoomAccount(
                id=z_id,
                user_id=user.id,
                account_id=z["email"],
                label=z["group"],
                zoom_email=z["email"],
                group_name=z["group"],
                default_meeting_url=z["url"],
                preferred_engine="web",
                active=True,
                created_at=now,
                updated_at=now,
            ))
            counts["zoom_accounts"] += 1
    await session.flush()

    # 5. Ensure RunDelegation
    delegation = await session.get(RunDelegation, user.id)
    if delegation is None:
        session.add(RunDelegation(
            coordinator_id=user.id,
            lms_account_id=lms_acc.id,
            zoom_account_id=first_zoom_id,
            enabled=True,
            updated_at=now,
        ))
    # An existing delegation is operator-owned. In particular, never re-enable it or
    # replace its selected LMS account on a deployment.
    await session.flush()

    # 6. Seed Students and Aliases
    for group_code, student_list in (("CAI5_IND1_G1", G1_STUDENTS), ("CAI5_IND1_G2", G2_STUDENTS)):
        for idx, (full_name, aliases) in enumerate(student_list):
            existing_s = (await session.execute(
                select(Student).where(Student.group_name == group_code, Student.full_name == full_name)
            )).scalar_one_or_none()
            is_new = existing_s is None
            if is_new:
                s_id = uuid.uuid4()
                existing_s = Student(
                    id=s_id,
                    group_name=group_code,
                    full_name=full_name,
                    order_index=idx + 1,
                    aliases=aliases,
                    active=True,
                    created_at=now,
                    updated_at=now,
                )
                session.add(existing_s)
                counts["students"] += 1
                await session.flush()

            # Starter aliases belong only to a newly created roster row. Recreating a
            # deleted alias would undo an operator's dashboard decision on every deploy.
            if is_new:
                for alias in aliases:
                    key = normalize(alias)
                    if not key:
                        continue
                    existing_alias = (await session.execute(
                        select(StudentAlias).where(StudentAlias.student_id == existing_s.id, StudentAlias.alias_key == key)
                    )).scalar_one_or_none()
                    if existing_alias is None:
                        session.add(StudentAlias(
                            id=uuid.uuid4(),
                            student_id=existing_s.id,
                            alias=alias,
                            alias_key=key,
                            status="accepted",
                            source="seed",
                            created_at=now,
                        ))
                        counts["aliases"] += 1
    await session.flush()

    # 7. Seed ClassPlans for current week + next 2 weeks (21 days lookahead)
    today = datetime.now(CAIRO).date()
    for day_offset in range(-1, 21):
        target_date = today + timedelta(days=day_offset)
        target_weekday = target_date.weekday()  # Monday is 0, Sunday is 6
        for slot in RECURRING_SLOTS:
            if slot["weekday"] == target_weekday:
                existing_plan = (await session.execute(
                    select(ClassPlan).where(
                        ClassPlan.group_name == slot["group"],
                        ClassPlan.session_date == target_date,
                        ClassPlan.start_time == slot["start_time"],
                    )
                )).scalar_one_or_none()
                if existing_plan is None:
                    session.add(ClassPlan(
                        id=uuid.uuid4(),
                        coordinator_id=user.id,
                        group_name=slot["group"],
                        session_date=target_date,
                        start_time=slot["start_time"],
                        title=f"{slot['group']} Session",
                        meeting_url=slot["meeting_url"],
                        zoom_account=slot["zoom_account"],
                        preferred_engine="web",
                        source="manual",
                        status="planned",
                        imported_at=now,
                        created_at=now,
                        updated_at=now,
                    ))
                    counts["plans"] += 1

    await session.flush()
    return counts
