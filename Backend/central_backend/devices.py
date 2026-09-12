"""Devices: enrollment, registration, authentication and whether a device counts as online."""

from __future__ import annotations

import hmac
import logging
import uuid
from datetime import datetime, timedelta
from typing import Any

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .config import Settings
from .models import Device, EnrollmentToken
from .observability import emit
from .security import device_id_of, hash_secret, new_device_token, new_enrollment_token
from .validation import clean_capabilities


class RegistrationRefused(Exception):
    """The enrollment token is unknown, used or expired. Deliberately not told apart to the caller."""


async def create_enrollment_token(session: AsyncSession, label: str, ttl: timedelta, now: datetime) -> str:
    """A single-use token an operator hands to one Windows installation. Only its hash is stored."""
    token = new_enrollment_token()
    session.add(
        EnrollmentToken(
            id=uuid.uuid4(),
            token_hash=hash_secret(token),
            label=label.strip()[:100] or "device",
            expires_at=now + ttl,
            created_at=now,
        )
    )
    await session.flush()
    emit("enrollment.created", label=label.strip()[:100], expiresAt=(now + ttl).isoformat())
    return token


async def register_device(
    session: AsyncSession,
    *,
    enrollment_token: str,
    installation_id: uuid.UUID,
    name: str,
    version: str,
    capabilities: list[Any],
    now: datetime,
) -> tuple[Device, str]:
    """Spend an enrollment token on this installation and issue its device token.

    The same installation registering again (its token was lost, or it was revoked) keeps its
    device id and gets a new token; the old token stops working at once.
    """
    enrollment = (
        await session.execute(
            select(EnrollmentToken)
            .where(EnrollmentToken.token_hash == hash_secret(enrollment_token.strip()))
            .with_for_update()
        )
    ).scalar_one_or_none()
    if enrollment is None or enrollment.used_at is not None or enrollment.expires_at <= now:
        raise RegistrationRefused()

    device = (
        await session.execute(select(Device).where(Device.installation_id == installation_id).with_for_update())
    ).scalar_one_or_none()
    created = device is None
    if device is None:
        device = Device(id=uuid.uuid4(), installation_id=installation_id, status="offline", created_at=now)
        session.add(device)

    token = new_device_token(device.id)
    device.name = name.strip()[:100] or "device"
    device.version = version.strip()[:50] or "unknown"
    device.capabilities = clean_capabilities(capabilities)
    device.token_hash = hash_secret(token)
    device.revoked_at = None
    device.updated_at = now
    await session.flush()

    enrollment.used_at = now
    enrollment.used_by_device_id = device.id
    emit(
        "device.registered" if created else "device.reregistered",
        deviceId=str(device.id),
        name=device.name,
        version=device.version,
        capabilities=device.capabilities,
    )
    return device, token


async def authenticate_device(session: AsyncSession, token: str | None) -> Device | None:
    device_id = device_id_of(token)
    if device_id is None or token is None:
        return None
    device = await session.get(Device, device_id)
    if device is None or device.revoked_at is not None or not device.token_hash:
        return None
    return device if hmac.compare_digest(hash_secret(token), device.token_hash) else None


async def revoke_device(session: AsyncSession, device_id: uuid.UUID, now: datetime) -> bool:
    device = await session.get(Device, device_id, with_for_update=True)
    if device is None:
        return False
    device.revoked_at = now
    device.token_hash = None
    device.status = "offline"
    device.updated_at = now
    emit("device.revoked", level=logging.WARNING, deviceId=str(device_id))
    return True


def heartbeat_is_fresh(device: Device, now: datetime, settings: Settings) -> bool:
    return device.last_heartbeat is not None and device.last_heartbeat >= now - timedelta(
        seconds=settings.device_stale_seconds
    )


def is_online(device: Device, connected: set[uuid.UUID], now: datetime, settings: Settings) -> bool:
    """Online = connected to this backend, not revoked, and heard from within the stale window."""
    return device.id in connected and device.revoked_at is None and heartbeat_is_fresh(device, now, settings)


def device_view(device: Device, connected: set[uuid.UUID], now: datetime, settings: Settings) -> dict[str, Any]:
    return {
        "deviceId": str(device.id),
        "name": device.name,
        "installationId": str(device.installation_id),
        "status": "online" if is_online(device, connected, now, settings) else "offline",
        "connected": device.id in connected,
        "agentState": device.agent_state,
        "version": device.version,
        "capabilities": device.capabilities,
        "lastHeartbeat": device.last_heartbeat,
        "revoked": device.revoked_at is not None,
        "createdAt": device.created_at,
        "updatedAt": device.updated_at,
    }
