"""Operator commands. Run from Backend/ with the virtual environment active:

    python -m central_backend.cli migrate
    python -m central_backend.cli new-api-key
    python -m central_backend.cli create-enrollment-token --label PC-01 [--ttl-hours 24]
    python -m central_backend.cli revoke-device <deviceId>
    python -m central_backend.cli create-admin [--username admin] [--display-name "..."] [--from-env]

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


async def _create_admin(username: str, password_hash: str, display_name: str) -> str | None:
    """Insert the one admin account; the reason it could not be, or None."""
    from sqlalchemy import select

    from .models import User

    engine = make_engine(database_url())
    try:
        async with make_sessionmaker(engine)() as session, session.begin():
            existing = (await session.execute(select(User.username).where(User.role == "admin"))).scalar_one_or_none()
            if existing is not None:
                return f"There is already an admin account ('{existing}'). There can be only one."
            if (await session.execute(select(User.id).where(User.username == username))).first():
                return f"The username '{username}' is taken by a coordinator."
            now = datetime.now(UTC)
            session.add(User(id=uuid.uuid4(), username=username, display_name=display_name, password_hash=password_hash,
                             role="admin", status="active", approved_at=now, created_at=now, updated_at=now))
        return None
    finally:
        await engine.dispose()


def _create_admin_command(username: str | None, display_name: str | None, from_env: bool) -> int:
    """The admin account, made once on the server. The password is asked for twice (not echoed) and
    is never printed, stored in the clear or passed on a command line. With --from-env the hash of
    the old CENTRAL_ADMIN_USERS setting is moved into the database instead: never a guess about
    which entry, and every entry left behind is named."""
    import getpass
    import os

    from .auth import (
        LEGACY_ADMIN_USERS_VARIABLE,
        MAX_PASSWORD,
        MIN_PASSWORD,
        USERNAME,
        hash_password,
        parse_legacy_admin_users,
    )

    left_behind: list[str] = []
    if from_env:
        legacy, skipped = parse_legacy_admin_users(os.environ.get(LEGACY_ADMIN_USERS_VARIABLE))
        for reason in skipped:
            print(f"{LEGACY_ADMIN_USERS_VARIABLE}: skipped {reason}.", file=sys.stderr)
        if not legacy:
            print(f"{LEGACY_ADMIN_USERS_VARIABLE} holds no usable 'name:scrypt$...' entry.", file=sys.stderr)
            return 1
        if username is not None:
            name = username.strip().lower()
            if name not in legacy:
                print(f"'{name}' is not in {LEGACY_ADMIN_USERS_VARIABLE} (it has: {', '.join(legacy)}).", file=sys.stderr)
                return 1
        elif len(legacy) == 1:
            name = next(iter(legacy))
        else:
            print(f"{LEGACY_ADMIN_USERS_VARIABLE} has {len(legacy)} accounts ({', '.join(legacy)}), and there is only one "
                  "admin now. Choose it with --username.", file=sys.stderr)
            return 1
        stored = legacy[name]
        left_behind = [other for other in legacy if other != name]
    else:
        name = (username or "admin").strip().lower()
        if not USERNAME.match(name):
            print("The username needs 3-50 characters: lower-case letters, digits and . _ -", file=sys.stderr)
            return 1
        password = getpass.getpass(f"Password for the admin '{name}': ")
        if not MIN_PASSWORD <= len(password) <= MAX_PASSWORD:
            print(f"Use {MIN_PASSWORD} to {MAX_PASSWORD} characters.", file=sys.stderr)
            return 1
        if password.strip().lower() == name:
            print("The password cannot be the username.", file=sys.stderr)
            return 1
        if getpass.getpass("Again: ") != password:
            print("The two passwords differ.", file=sys.stderr)
            return 1
        stored = hash_password(password)
    problem = asyncio.run(_create_admin(name, stored, (display_name or name).strip()[:100] or name))
    if problem:
        print(problem, file=sys.stderr)
        return 1
    print(f"Admin account '{name}' created. Sign in at /dashboard/.")
    if left_behind:
        print(f"Not imported: {', '.join(left_behind)}. There is one admin now; if they still need the "
              "dashboard, create them as coordinators on the Users page.")
    if from_env:
        print(f"{LEGACY_ADMIN_USERS_VARIABLE} is no longer read; remove it from the environment.")
    return 0


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
    admin = sub.add_parser("create-admin", help="create the dashboard's one admin account (the password is asked for, hidden)")
    admin.add_argument("--username", default=None,
                       help="the admin's username (default: admin; with --from-env, which entry to move - "
                            "needed when the variable holds several)")
    admin.add_argument("--display-name", default=None, help="the name shown in the dashboard (default: the username)")
    admin.add_argument("--from-env", action="store_true",
                       help="take the account from the old CENTRAL_ADMIN_USERS setting instead of asking for a password")
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
        elif args.command == "create-admin":
            return _create_admin_command(args.username, args.display_name, args.from_env)
    except ConfigurationError as exc:
        print(exc, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
