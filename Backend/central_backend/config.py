"""Settings, read from the environment once at start-up.

Every secret the server needs is given as a SHA-256 hash, never as the secret itself, so the
environment, a process dump or a support screenshot never holds a usable key.
"""

from __future__ import annotations

import os
import re
from collections.abc import Mapping
from dataclasses import dataclass, field

_HASH = re.compile(r"^[0-9a-f]{64}$")


class ConfigurationError(RuntimeError):
    """A setting is missing or invalid. The message names the variable, never a secret."""


@dataclass(frozen=True)
class Settings:
    database_url: str
    client_api_key_hashes: frozenset[str]
    environment: str = "production"
    heartbeat_interval_seconds: int = 30
    device_stale_seconds: int = 90
    assignment_ack_timeout_seconds: int = 60
    running_orphan_timeout_seconds: int = 1800
    sweep_interval_seconds: float = 5.0
    busy_retry_delay_seconds: int = 60
    rejected_retry_delay_seconds: int = 10
    default_max_attempts: int = 3
    max_agent_message_bytes: int = 64 * 1024
    trusted_capabilities: frozenset[str] = field(
        default_factory=lambda: frozenset({"recording_processing", "zoom", "lms"})
    )

    @property
    def is_development(self) -> bool:
        return self.environment == "development"

    @property
    def require_https(self) -> bool:
        """Plain HTTP/WS is refused everywhere except in development."""
        return not self.is_development

    @classmethod
    def from_env(cls, env: Mapping[str, str] | None = None) -> Settings:
        env = os.environ if env is None else env

        database_url = (env.get("CENTRAL_DATABASE_URL") or "").strip()
        if not database_url:
            raise ConfigurationError(
                "CENTRAL_DATABASE_URL is not set (e.g. postgresql+asyncpg://user@host:5432/central)."
            )
        if not database_url.startswith("postgresql+asyncpg://"):
            raise ConfigurationError("CENTRAL_DATABASE_URL must start with postgresql+asyncpg://")

        hashes = frozenset(
            part.strip().lower()
            for part in re.split(r"[,\s]+", env.get("CENTRAL_CLIENT_API_KEY_HASHES") or "")
            if part.strip()
        )
        if not hashes:
            raise ConfigurationError(
                "CENTRAL_CLIENT_API_KEY_HASHES is not set. Create a key with "
                "`python -m central_backend.cli new-api-key` and put its hash there."
            )
        bad = [h for h in hashes if not _HASH.match(h)]
        if bad:
            raise ConfigurationError(
                "CENTRAL_CLIENT_API_KEY_HASHES must hold SHA-256 hashes (64 hex characters), not keys."
            )

        environment = (env.get("CENTRAL_ENVIRONMENT") or "production").strip().lower()
        if environment not in {"production", "development"}:
            raise ConfigurationError("CENTRAL_ENVIRONMENT must be 'production' or 'development'.")

        def seconds(name: str, default: int, low: int, high: int) -> int:
            text = (env.get(name) or "").strip()
            if not text:
                return default
            if not text.isdigit() or not low <= int(text) <= high:
                raise ConfigurationError(f"{name} must be a whole number of seconds between {low} and {high}.")
            return int(text)

        heartbeat = seconds("CENTRAL_HEARTBEAT_INTERVAL_SECONDS", 30, 5, 300)
        stale = seconds("CENTRAL_DEVICE_STALE_SECONDS", 90, 15, 3600)
        if stale < heartbeat * 2:
            raise ConfigurationError(
                "CENTRAL_DEVICE_STALE_SECONDS must be at least twice the heartbeat interval, "
                "so one missed heartbeat never marks a device offline."
            )
        return cls(
            database_url=database_url,
            client_api_key_hashes=hashes,
            environment=environment,
            heartbeat_interval_seconds=heartbeat,
            device_stale_seconds=stale,
            assignment_ack_timeout_seconds=seconds("CENTRAL_ASSIGNMENT_ACK_TIMEOUT_SECONDS", 60, 5, 3600),
            running_orphan_timeout_seconds=seconds("CENTRAL_RUNNING_ORPHAN_TIMEOUT_SECONDS", 1800, 60, 86400),
            busy_retry_delay_seconds=seconds("CENTRAL_BUSY_RETRY_DELAY_SECONDS", 60, 0, 3600),
        )
