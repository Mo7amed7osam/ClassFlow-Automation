"""Tests for Phase 2 core logic additions:
- Global ignore system
- Automatic Zoom recording attachment pipeline
- Occurrence state transitions (waitingForDrive, driveLinkAttached, attendanceFinalized)
- Idempotent production data seeder
"""

import uuid
from datetime import UTC, date, datetime

import pytest
from central_backend.attendance_names import clean_display_name, is_globally_ignored
from central_backend.models import (
    AttendanceSession,
    ClassOccurrence,
    ClassPlan,
    Group,
    Job,
    Student,
    StudentAlias,
    User,
    ZoomAccount,
)
from central_backend.occurrence_lifecycle import record_occurrence_outcome
from central_backend.seed import G1_STUDENTS, G2_STUDENTS, seed_production_data


def test_global_ignored_names():
    assert is_globally_ignored("eyouth coordinator") is True
    assert is_globally_ignored("EYOUTH COORDINATOR") is True
    assert is_globally_ignored("depi wavz") is True
    assert is_globally_ignored("youssef ayoub") is True
    assert is_globally_ignored("Yossef ayoub") is True
    assert is_globally_ignored("Mohamed Hosam") is True
    assert is_globally_ignored("Abeer Mohammed Abu Elhassan Elsayed") is False
    assert is_globally_ignored("Ahmed Mostafa") is False
    assert is_globally_ignored("") is False
    assert is_globally_ignored(None) is False

    cleaned = clean_display_name("eyouth coordinator (Guest)")
    assert cleaned.is_staff is True

    cleaned_student = clean_display_name("Abeer Mohammed")
    assert cleaned_student.is_staff is False


from central_backend.db import make_engine, make_sessionmaker


@pytest.fixture
def session_maker(settings):
    engine = make_engine(settings.database_url)
    sm = make_sessionmaker(engine)
    yield sm
    engine.sync_engine.dispose()


@pytest.mark.asyncio
async def test_occurrence_lifecycle_zoom_recording_pipeline(session_maker):
    now = datetime.now(UTC)
    async with session_maker() as session, session.begin():
        user = User(
            id=uuid.uuid4(),
            username="test_coord",
            display_name="Test Coordinator",
            password_hash="dummy",
            role="coordinator",
            status="active",
            created_at=now,
            updated_at=now,
        )
        session.add(user)
        await session.flush()

        plan = ClassPlan(
            id=uuid.uuid4(),
            coordinator_id=user.id,
            group_name="CAI5_IND1_G1",
            session_date=date(2026, 9, 28),
            start_time="17:55",
            meeting_url="https://zoom.us/j/94698416251",
            zoom_account="depi+10@eyouthlearning.com",
            status="planned",
            created_at=now,
            updated_at=now,
        )
        session.add(plan)
        await session.flush()

        att_session = AttendanceSession(
            id=uuid.uuid4(),
            group_name=plan.group_name,
            session_date=plan.session_date,
            start_time=plan.start_time,
            status="open",
            created_at=now,
            updated_at=now,
        )
        session.add(att_session)
        await session.flush()

        occurrence = ClassOccurrence(
            id=uuid.uuid4(),
            class_plan_id=plan.id,
            group_name=plan.group_name,
            session_date=plan.session_date,
            attendance_session_id=att_session.id,
            state="lmsSessionCompleted",
            created_at=now,
            updated_at=now,
        )
        session.add(occurrence)
        await session.flush()

        # 1. zoom.recording succeeds with shareUrl
        rec_job = Job(
            id=uuid.uuid4(),
            type="zoom.recording",
            status="succeeded",
            occurrence_id=occurrence.id,
            result={"shareUrl": "https://zoom.us/rec/share/12345ABC"},
            created_at=now,
            updated_at=now,
        )
        session.add(rec_job)
        await session.flush()

        await record_occurrence_outcome(session, rec_job, now)
        await session.flush()

        assert occurrence.state == "zoomLinkFound"
        assert occurrence.zoom_recording_url == "https://zoom.us/rec/share/12345ABC"

        # Check that recording.process job was automatically created
        from sqlalchemy import select
        attach_jobs = (await session.execute(
            select(Job).where(Job.occurrence_id == occurrence.id, Job.type == "recording.process")
        )).scalars().all()
        assert len(attach_jobs) == 1
        assert attach_jobs[0].payload["recordLink"] == "https://zoom.us/rec/share/12345ABC"

        # 2. recording.process with Zoom link finishes
        zoom_attach_job = attach_jobs[0]
        zoom_attach_job.status = "succeeded"
        zoom_attach_job.result = {"did": "saved"}
        await record_occurrence_outcome(session, zoom_attach_job, now)
        await session.flush()

        assert occurrence.state == "waitingForDrive"

        # 3. recording.process with Drive link finishes
        drive_attach_job = Job(
            id=uuid.uuid4(),
            type="recording.process",
            status="succeeded",
            occurrence_id=occurrence.id,
            payload={"recordLink": "https://drive.google.com/file/d/test12345/view"},
            result={"did": "saved"},
            created_at=now,
            updated_at=now,
        )
        session.add(drive_attach_job)
        await session.flush()

        await record_occurrence_outcome(session, drive_attach_job, now)
        await session.flush()

        assert occurrence.state == "driveLinkAttached"
        assert occurrence.drive_recording_url == "https://drive.google.com/file/d/test12345/view"

        # 4. lms.late_joiners finalizes attendance session
        late_job = Job(
            id=uuid.uuid4(),
            type="lms.late_joiners",
            status="succeeded",
            occurrence_id=occurrence.id,
            result={"did": "corrected"},
            created_at=now,
            updated_at=now,
        )
        session.add(late_job)
        await session.flush()

        await record_occurrence_outcome(session, late_job, now)
        await session.flush()

        assert att_session.status == "finalized"


@pytest.mark.asyncio
async def test_seed_production_data_idempotent(session_maker):
    now = datetime.now(UTC)
    async with session_maker() as session, session.begin():
        counts1 = await seed_production_data(session, now)
        assert counts1["groups"] == 2
        assert counts1["students"] == len(G1_STUDENTS) + len(G2_STUDENTS)
        assert counts1["students"] == 45
        assert counts1["zoom_accounts"] == 2

        # Second run should make 0 additions
        counts2 = await seed_production_data(session, now)
        assert counts2["groups"] == 0
        assert counts2["students"] == 0
        assert counts2["aliases"] == 0
        assert counts2["zoom_accounts"] == 0
        assert counts2["plans"] == 0
