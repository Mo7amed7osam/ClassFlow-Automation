"""Operator commands. Run from Backend/ with the virtual environment active:

    python -m central_backend.cli migrate
    python -m central_backend.cli new-api-key
    python -m central_backend.cli create-enrollment-token --label PC-01 [--ttl-hours 24]
    python -m central_backend.cli revoke-device <deviceId>

A new key or token is printed once, on this terminal only; the database and the environment keep
only its SHA-256.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

from .config import ConfigurationError
from .db import make_engine, make_sessionmaker
from .devices import create_enrollment_token, revoke_device
from .security import hash_secret, new_client_api_key

BACKEND_DIR = Path(__file__).resolve().parent.parent


def database_url() -> str:
    import os

    url = (os.environ.get("CENTRAL_DATABASE_URL") or "").strip()
    if not url:
        raise ConfigurationError("CENTRAL_DATABASE_URL is not set.")
    return url


def migrate(url: str | None = None) -> None:
    """Apply every migration (alembic upgrade head). Migrations only add; none drops data."""
    from alembic import command
    from alembic.config import Config

    config = Config(str(BACKEND_DIR / "alembic.ini"))
    config.set_main_option("script_location", str(BACKEND_DIR / "migrations"))
    config.attributes["database_url"] = url or database_url()
    command.upgrade(config, "head")


async def _enroll(label: str, ttl_hours: int) -> str:
    engine = make_engine(database_url())
    try:
        async with make_sessionmaker(engine)() as session, session.begin():
            return await create_enrollment_token(session, label, timedelta(hours=ttl_hours), datetime.now(UTC))
    finally:
        await engine.dispose()


async def _revoke(device_id: uuid.UUID) -> bool:
    engine = make_engine(database_url())
    try:
        async with make_sessionmaker(engine)() as session, session.begin():
            return await revoke_device(session, device_id, datetime.now(UTC))
    finally:
        await engine.dispose()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m central_backend.cli")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("migrate", help="apply database migrations")
    sub.add_parser("new-api-key", help="make an API key for n8n and print its hash for CENTRAL_CLIENT_API_KEY_HASHES")
    enroll = sub.add_parser("create-enrollment-token", help="a single-use token for registering one Windows agent")
    enroll.add_argument("--label", required=True, help="what the token is for, e.g. the PC's name")
    enroll.add_argument("--ttl-hours", type=int, default=24, help="how long it stays valid, 1-336 hours")
    revoke = sub.add_parser("revoke-device", help="stop a device's token from working")
    revoke.add_argument("device_id", type=uuid.UUID)
    args = parser.parse_args(argv)
    if args.command == "create-enrollment-token" and not 1 <= args.ttl_hours <= 336:
        parser.error("--ttl-hours must be between 1 and 336")

    try:
        if args.command == "migrate":
            migrate()
            print("Migrations applied.")
        elif args.command == "new-api-key":
            key = new_client_api_key()
            print("API key (give it to n8n; it is not stored anywhere):")
            print(f"  {key}")
            print("Add this hash to CENTRAL_CLIENT_API_KEY_HASHES (comma-separated):")
            print(f"  {hash_secret(key)}")
        elif args.command == "create-enrollment-token":
            token = asyncio.run(_enroll(args.label, args.ttl_hours))
            print(f"Enrollment token for '{args.label}', single use, valid {args.ttl_hours} h:")
            print(f"  {token}")
        elif args.command == "revoke-device":
            print("Revoked." if asyncio.run(_revoke(args.device_id)) else "No such device.")
    except ConfigurationError as exc:
        print(exc, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
