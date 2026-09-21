"""What a cloud worker asks the server for beyond its jobs.

    POST /api/v1/agent/companion-enrollment    a single-use enrolment token for this device's LMS lane
    GET  /api/v1/agent/jobs/{id}/attendance    who was present at the class a held LMS job writes up

Both are answered only to a device, with its own device token, and both are narrow on purpose.

**The companion.** A device runs one job at a time, and a worker holding a class's meeting is busy
for the class's whole length. On Windows the LMS steps have their own queue beside the meeting; on
a server they would otherwise wait three hours behind it, and Run Session at the start of the class
would run at its end. So a worker is two devices: the one the operator enrolled, which holds
meetings, and a companion it enrols itself for the LMS. The operator still makes one token. The
companion can only be made once per device, and it can do nothing its parent could not already do.

**The attendance.** The meeting lane posts what it sees to /api/v1/attendance/snapshots, where the
names are matched against the group's roster like any other snapshot. The LMS lane then asks for
the result - the roster names of the students found present, which is exactly what the Windows app
hands its attendance step. It is asked for through the job, so a device learns a class's attendance
only while it holds a job to write that class up.
"""

from __future__ import annotations

import uuid
from datetime import date, timedelta
from typing import Any

from fastapi import APIRouter, Request
from sqlalchemy import func, select

from .api import ApiError
from .delegated_runs import _no_store
from .devices import authenticate_device, create_enrollment_token
from .models import AttendanceRecord, AttendanceSession, AttendanceSnapshot, Device, EnrollmentToken, Job, Student
from .observability import emit
from .security import bearer_token

router = APIRouter()

# How long a companion's enrolment token lives. The worker spends it within a second of asking; a
# token that sits unused is a token that can be stolen.
COMPANION_TOKEN_TTL = timedelta(minutes=10)

# The jobs that may read a class's attendance: the two that write it up.
READS_ATTENDANCE = ("lms.attendance", "lms.late_joiners")


def companion_label(device: Device) -> str:
    """The enrolment label that ties a companion to its parent, for as long as both exist."""
    return f"companion:{device.id}"


async def _device(session: Any, request: Request) -> Device:
    device = await authenticate_device(session, bearer_token(request.headers.get("authorization")))
    if device is None:
        raise ApiError(401, "Unauthorized")
    return device


@router.post("/api/v1/agent/companion-enrollment")
async def companion_enrollment(request: Request) -> Any:
    """One single-use enrolment token for this device's LMS lane.

    Refused when the device already has a companion that is still enrolled, or an unspent token
    for one: a device token that leaks must not become a way to mint devices. A companion that was
    revoked can be replaced, which is how an operator recovers a lane whose state was lost.
    """
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        device = await _device(session, request)
        if device.name.endswith("-lms"):
            raise ApiError(409, "Conflict", "A companion does not have a companion of its own.")

        label = companion_label(device)
        earlier = (await session.execute(
            select(EnrollmentToken).where(EnrollmentToken.label == label).with_for_update())).scalars().all()
        for token in earlier:
            if token.used_at is None and token.expires_at > now:
                raise ApiError(409, "Conflict", "A companion token for this device is already waiting to be used.")
            if token.used_by_device_id is not None:
                companion = await session.get(Device, token.used_by_device_id)
                if companion is not None and companion.revoked_at is None:
                    raise ApiError(409, "Conflict",
                                   f"This device already has its LMS companion ('{companion.name}'). Revoke that "
                                   "one first if it has to be enrolled again.")

        secret = await create_enrollment_token(session, label, COMPANION_TOKEN_TTL, now)
        emit("device.companion_token", deviceId=str(device.id), name=device.name)
    return _no_store({"enrollmentToken": secret, "name": f"{device.name}-lms",
                      "expiresAt": now + COMPANION_TOKEN_TTL}, 201)


@router.get("/api/v1/agent/jobs/{job_id}/attendance")
async def job_attendance(job_id: str, request: Request) -> Any:
    """The students found present at the class this LMS job writes up, by their roster names.

    Answered only for a job this device holds and is running, and only for the attendance stages.
    A class nobody collected anything for is a 409 that says so - never an empty list, which the
    LMS would write down as a whole class absent.
    """
    state = request.app.state
    async with state.sessionmaker() as session:
        device = await _device(session, request)
        try:
            job = await session.get(Job, uuid.UUID(job_id))
        except ValueError:
            job = None
        # One answer for "no such job" and "not yours", as the sign-in endpoints give.
        if job is None or job.device_id != device.id:
            raise ApiError(404, "Not found")
        if job.status not in ("assigned", "running"):
            raise ApiError(409, "Conflict", f"This job is {job.status}; attendance is only given for one being run.")
        if job.type not in READS_ATTENDANCE:
            raise ApiError(409, "Conflict", f"A {job.type} job does not write attendance.")

        payload = job.payload or {}
        group = payload.get("group")
        try:
            day = date.fromisoformat(str(payload.get("date")))
        except ValueError:
            raise ApiError(409, "Conflict", "This job names no date.") from None
        start = payload.get("startTime") or ""

        att = (await session.execute(
            select(AttendanceSession).where(
                AttendanceSession.group_name == group, AttendanceSession.session_date == day,
                func.coalesce(AttendanceSession.start_time, "") == start,
                AttendanceSession.external_ref.is_(None))
            .order_by(AttendanceSession.created_at.desc()).limit(1))).scalar_one_or_none()
        snapshots = 0 if att is None else (await session.execute(
            select(func.count()).select_from(AttendanceSnapshot)
            .where(AttendanceSnapshot.session_id == att.id))).scalar_one()
        if att is None or snapshots == 0:
            raise ApiError(409, "Conflict",
                           f"Nothing was collected from the meeting of {group} on {day} at {start or 'its time'}, so "
                           "there is nobody to write up. The meeting may not have been held by this system.")

        roster = (await session.execute(select(func.count()).select_from(Student).where(
            Student.group_name == group, Student.active.is_(True)))).scalar_one()
        if roster == 0:
            raise ApiError(409, "Conflict",
                           f"{group} has no students on the server, so the names seen in the meeting cannot be "
                           "matched to anybody. Import the group's roster on the dashboard.")

        rows = (await session.execute(
            select(AttendanceRecord.status, Student.full_name, Student.order_index)
            .join(Student, Student.id == AttendanceRecord.student_id)
            .where(AttendanceRecord.session_id == att.id)
            .order_by(Student.order_index.asc().nulls_last(), Student.full_name))).all()

    present = [name for status, name, _ in rows if status == "present"]
    review = sum(1 for status, _, _ in rows if status == "needs_review")
    emit("attendance.read_by_device", deviceId=str(device.id), jobId=str(job.id), present=len(present))
    return _no_store({"sessionId": str(att.id), "present": present, "needsReview": review,
                      "students": len(rows), "snapshots": snapshots})
