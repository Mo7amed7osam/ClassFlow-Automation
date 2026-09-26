"""Jobs: creation (idempotent), status, and every state change an agent can report.

    queued -> assigned -> (accepted) -> running -> succeeded | failed
    queued -> cancelled
    assigned/running -> queued again: assignment expired, agent rejected it, or a retryable failure

Each change is one transaction that also writes a `job_events` row.
"""

from __future__ import annotations

import enum
import logging
import uuid
from datetime import datetime, timedelta
from typing import Any

from sqlalchemy import select
from sqlalchemy.dialects.postgresql import insert
from sqlalchemy.ext.asyncio import AsyncSession

from .config import Settings
from .models import Job, JobEvent
from .occurrence_lifecycle import record_occurrence_outcome
from .observability import emit, link_preview
from .recording_jobs import record_job_outcome
from .validation import clean_error, clean_result


class IdempotencyConflict(Exception):
    """The Idempotency-Key was already used for a different job."""


class Decision(enum.Enum):
    START = "start"
    REVOKE = "revoke"
    IGNORE = "ignore"


def add_event(
    session: AsyncSession,
    job_id: uuid.UUID,
    event_type: str,
    now: datetime,
    device_id: uuid.UUID | None = None,
    payload: dict[str, Any] | None = None,
) -> None:
    session.add(JobEvent(job_id=job_id, device_id=device_id, event_type=event_type, payload=payload, created_at=now))


async def create_job(
    session: AsyncSession,
    *,
    job_type: str,
    payload: dict[str, Any],
    idempotency_key: str | None,
    now: datetime,
    max_attempts: int,
) -> tuple[Job, bool]:
    """(job, created). A repeated Idempotency-Key with the same job returns the first one."""
    job_id = uuid.uuid4()
    values = {
        "id": job_id,
        "type": job_type,
        "payload": payload,
        "status": "queued",
        "idempotency_key": idempotency_key,
        "attempts": 0,
        "max_attempts": max_attempts,
        "available_at": now,
        "created_at": now,
        "updated_at": now,
    }
    statement = insert(Job).values(**values)
    if idempotency_key is not None:
        statement = statement.on_conflict_do_nothing(index_elements=[Job.idempotency_key])
    inserted = (await session.execute(statement.returning(Job.id))).scalar_one_or_none()

    if inserted is None:
        existing = (
            await session.execute(select(Job).where(Job.idempotency_key == idempotency_key))
        ).scalar_one()
        if existing.type != job_type or existing.payload != payload:
            raise IdempotencyConflict()
        return existing, False

    add_event(session, job_id, "created", now)
    job = await session.get(Job, job_id)
    assert job is not None
    emit(
        "job.created",
        jobId=str(job_id),
        type=job_type,
        group=payload.get("group"),
        date=payload.get("date"),
        recordLink=link_preview(payload.get("recordLink")),
    )
    return job, True


def job_view(job: Job) -> dict[str, Any]:
    return {
        "jobId": str(job.id),
        "type": job.type,
        "status": job.status,
        "deviceId": str(job.device_id) if job.device_id else None,
        "payload": job.payload,
        "result": job.result,
        "error": job.error,
        "attempts": job.attempts,
        "maxAttempts": job.max_attempts,
        "createdAt": job.created_at,
        "assignedAt": job.assigned_at,
        "startedAt": job.started_at,
        "finishedAt": job.finished_at,
        "updatedAt": job.updated_at,
    }


async def cancel_job(session: AsyncSession, job_id: uuid.UUID, now: datetime) -> Job | None:
    """Cancel a queued job. Returns the job (whatever its status) or None if unknown."""
    job = await session.get(Job, job_id, with_for_update=True)
    if job is None or job.status != "queued":
        return job
    job.status = "cancelled"
    job.finished_at = now
    job.updated_at = now
    add_event(session, job.id, "cancelled", now)
    emit("job.cancelled", jobId=str(job.id))
    return job


def requeue(job: Job, now: datetime, delay_seconds: int, reason: str) -> None:
    """Back to the queue, unassigned; it becomes available again after the delay."""
    previous = job.device_id
    job.status = "queued"
    job.device_id = None
    job.assigned_at = None
    job.accepted_at = None
    job.started_at = None
    job.available_at = now + timedelta(seconds=delay_seconds)
    job.updated_at = now
    emit("job.requeued", jobId=str(job.id), previousDeviceId=str(previous) if previous else None, reason=reason)


async def _locked_job(session: AsyncSession, job_id: uuid.UUID) -> Job | None:
    return await session.get(Job, job_id, with_for_update=True)


async def agent_accepted(session: AsyncSession, device_id: uuid.UUID, job_id: uuid.UUID, now: datetime) -> tuple[Decision, Job | None]:
    """Confirm or revoke. The agent runs a job only after `job.start`, so a job that was re-queued
    or given to another device meanwhile is never run by this one."""
    job = await _locked_job(session, job_id)
    if job is None or job.device_id != device_id:
        return Decision.REVOKE, job
    if job.status == "assigned":
        if job.accepted_at is None:
            job.accepted_at = now
            job.updated_at = now
            add_event(session, job.id, "accepted", now, device_id)
            emit("job.accepted", jobId=str(job.id), deviceId=str(device_id))
        return Decision.START, job
    if job.status in ("running", "succeeded", "failed"):
        return Decision.IGNORE, job
    return Decision.REVOKE, job


async def agent_rejected(
    session: AsyncSession, device_id: uuid.UUID, job_id: uuid.UUID, reason: str, now: datetime, settings: Settings
) -> bool:
    job = await _locked_job(session, job_id)
    if job is None or job.device_id != device_id or job.status != "assigned":
        return False
    add_event(session, job.id, "rejected", now, device_id, {"reason": reason[:100]})

    # 'busy' is worth trying again - the device is running something else and will not be in a
    # minute. 'unsupportedType' is not: no device on this deployment can run a job type none of
    # them was built with, and requeue does not spend an attempt, so it came back every ten
    # seconds for ever. The job sat queued, the log filled with the same line, and nothing said
    # what was actually wrong.
    if reason == "unsupportedType":
        job.status = "failed"
        job.device_id = None
        job.assigned_at = None
        job.accepted_at = None
        job.error = {"code": "unsupportedType",
                     "message": f"No agent on this deployment runs '{job.type}'. The job was not retried, "
                                "because waiting does not give a device a capability it was not built with.",
                     "retryable": False}
        job.finished_at = now
        job.updated_at = now
        add_event(session, job.id, "failed", now, device_id, {"reason": "unsupportedType"})
        emit("job.failed", jobId=str(job.id), deviceId=str(device_id), code="unsupportedType")
        return True

    requeue(job, now, settings.rejected_retry_delay_seconds, f"rejected: {reason[:100]}")
    return True


async def agent_started(session: AsyncSession, device_id: uuid.UUID, job_id: uuid.UUID, now: datetime) -> bool:
    job = await _locked_job(session, job_id)
    if job is None or job.device_id != device_id:
        return False
    if job.status == "assigned":
        job.status = "running"
        job.started_at = now
        job.attempts += 1
        job.updated_at = now
        add_event(session, job.id, "started", now, device_id, {"attempt": job.attempts})
        emit("job.started", jobId=str(job.id), deviceId=str(device_id), attempt=job.attempts)
        return True
    return job.status == "running"


async def agent_finished(
    session: AsyncSession,
    device_id: uuid.UUID,
    job_id: uuid.UUID,
    *,
    succeeded: bool,
    body: Any,
    now: datetime,
    settings: Settings,
) -> str:
    """Record a result. Returns what happened: 'succeeded', 'failed', 'retry' or 'ignored'."""
    job = await _locked_job(session, job_id)
    if job is None or job.device_id != device_id or job.status not in ("assigned", "running"):
        return "ignored"
    job.attempts = max(job.attempts, 1)
    job.updated_at = now

    if succeeded:
        job.status = "succeeded"
        job.result = clean_result(body)
        job.error = None
        job.finished_at = now
        add_event(session, job.id, "succeeded", now, device_id, job.result)
        emit("job.succeeded", jobId=str(job.id), deviceId=str(device_id), alreadyExists=job.result.get("alreadyExists"))
        await record_job_outcome(session, job, now)
        await record_occurrence_outcome(session, job, now)
        return "succeeded"

    error = clean_error(body)
    job.error = error
    add_event(session, job.id, "failed", now, device_id, error)
    if error.get("retryable") and job.attempts < job.max_attempts:
        delay = error.get("retryAfterSeconds", settings.busy_retry_delay_seconds)
        requeue(job, now, delay, f"retryable failure: {error['code']}")
        add_event(session, job.id, "retry_scheduled", now, None, {"availableAt": job.available_at.isoformat()})
        return "retry"
    job.status = "failed"
    job.finished_at = now
    emit("job.failed", level=logging.WARNING, jobId=str(job.id), deviceId=str(device_id), code=error["code"])
    await record_job_outcome(session, job, now)
    await record_occurrence_outcome(session, job, now)
    return "failed"
