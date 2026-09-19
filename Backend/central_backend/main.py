"""The application. Run it with:

    uvicorn central_backend.main:create_app --factory --host 127.0.0.1 --port 8080

Behind the TLS-terminating reverse proxy add `--proxy-headers --forwarded-allow-ips=<proxy address>`
so the request scheme is https/wss; outside development anything else is refused.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
from collections.abc import AsyncIterator, Callable
from contextlib import asynccontextmanager
from datetime import UTC, datetime, timedelta
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from starlette.types import ASGIApp, Receive, Scope, Send

from . import __version__
from .agent_protocol import router as agent_router
from .api import ApiError, error_response
from .api import router as api_router
from .config import Settings
from .connections import ConnectionRegistry
from .db import make_engine, make_sessionmaker
from .dispatch import Dispatcher, Sweeper
from .admin import router as admin_router
from .auth import LEGACY_ADMIN_USERS_VARIABLE, AuthSettings, LoginThrottle, RateLimit
from .auth import router as auth_router
from .dashboard import router as dashboard_router
from .dashboard import site_router
from .dashboard_operations import router as dashboard_operations_router
from .delegated_runs import router as delegated_runs_router
from .activity import dashboard_router as activity_dashboard_router
from .app_updates import default_releases_dir
from .app_updates import router as app_updates_router
from .activity import router as activity_router
from .attendance import dashboard_router as attendance_dashboard_router
from .attendance import router as attendance_router
from .attendance_ai import AttendanceAi, ChatCompletionsAi
from .observability import configure_logging, emit
from .recordings import router as recordings_router
from .user_data import SecretBox
from .user_data import router as user_data_router

DEFAULT_DASHBOARD_DIST = Path(__file__).resolve().parents[2] / "Dashboard" / "dist"


class RequireHttpsMiddleware:
    """Outside development, only https:// and wss:// requests are served (as the proxy reports them)."""

    def __init__(self, app: ASGIApp, enabled: bool) -> None:
        self.app = app
        self.enabled = enabled

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if not self.enabled or scope["type"] not in ("http", "websocket") or scope.get("scheme") in ("https", "wss"):
            await self.app(scope, receive, send)
            return
        emit("request.refused_plain_http", path=scope.get("path"))
        if scope["type"] == "websocket":
            await receive()  # websocket.connect
            await send({"type": "websocket.close", "code": 4403})
            return
        body = json.dumps({"error": "HTTPS required"}).encode()
        await send({"type": "http.response.start", "status": 403,
                    "headers": [(b"content-type", b"application/json"), (b"content-length", str(len(body)).encode())]})
        await send({"type": "http.response.body", "body": body})


async def _check_accounts(sessionmaker) -> None:  # noqa: ANN001
    """Say at start-up what the operator still has to do about dashboard accounts."""
    from sqlalchemy import select

    from .models import User

    if (os.environ.get(LEGACY_ADMIN_USERS_VARIABLE) or "").strip():
        emit("auth.legacy_admin_users_ignored", level=logging.WARNING,
             hint=f"{LEGACY_ADMIN_USERS_VARIABLE} is no longer read. Move it into the database with "
                  "'python -m central_backend.cli create-admin --from-env', then remove it.")
    try:
        async with sessionmaker() as session:
            has_admin = (await session.execute(select(User.id).where(User.role == "admin"))).first() is not None
    except Exception:  # noqa: BLE001 - not migrated yet; the first request will say so
        return
    if not has_admin:
        emit("auth.no_admin", level=logging.WARNING,
             hint="No admin account yet: run 'python -m central_backend.cli create-admin'.")


def create_app(
    settings: Settings | None = None,
    *,
    clock: Callable[[], datetime] | None = None,
    run_background: bool = True,
    configure_logs: bool = True,
    auth_settings: AuthSettings | None = None,
    dashboard_dist: Path | None = None,
    attendance_ai: AttendanceAi | None = None,
    secret_box: SecretBox | None = None,
    releases_dir: Path | None = None,
) -> FastAPI:
    if configure_logs:
        configure_logging()
    settings = settings or Settings.from_env()
    auth_settings = auth_settings or AuthSettings.from_env()
    clock = clock or (lambda: datetime.now(UTC))

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        engine = make_engine(settings.database_url)
        sessionmaker = make_sessionmaker(engine)
        registry = ConnectionRegistry()
        dispatcher = Dispatcher(sessionmaker, registry, settings, clock)
        sweeper = Sweeper(sessionmaker, registry, dispatcher, settings, clock)
        app.state.sessionmaker = sessionmaker
        app.state.registry = registry
        app.state.dispatcher = dispatcher
        app.state.sweeper = sweeper
        stop = asyncio.Event()
        task = asyncio.create_task(sweeper.run(stop)) if run_background else None
        emit("backend.started", version=__version__, environment=settings.environment)
        await _check_accounts(sessionmaker)
        try:
            yield
        finally:
            stop.set()
            if task is not None:
                await task
            for device_id in list(registry.connected_device_ids()):
                await registry.close(device_id, 1012, "server restarting")
            await engine.dispose()
            emit("backend.stopped")

    development = settings.is_development
    app = FastAPI(
        title="Zoom Auto Admit — Central Backend",
        version=__version__,
        lifespan=lifespan,
        docs_url="/docs" if development else None,
        redoc_url=None,
        openapi_url="/openapi.json" if development else None,
    )
    app.state.settings = settings
    app.state.clock = clock
    app.state.auth_settings = auth_settings
    # Where a published version of the Windows app waits for the copies that ask for it.
    app.state.releases_dir = releases_dir or default_releases_dir()
    app.state.login_throttle = LoginThrottle(auth_settings.max_failures, timedelta(seconds=auth_settings.lockout_seconds))
    app.state.register_limit = RateLimit(auth_settings.registrations_per_hour, timedelta(hours=1))
    # The optional AI step of attendance matching (CENTRAL_AI_API_KEY); the key stays on the server.
    app.state.attendance_ai = attendance_ai if attendance_ai is not None else ChatCompletionsAi.from_env()
    # Encrypts the LMS passwords users keep on the server (CENTRAL_SECRETS_KEY); None refuses them.
    app.state.secret_box = secret_box if secret_box is not None else SecretBox.from_env()
    app.add_middleware(RequireHttpsMiddleware, enabled=settings.require_https)

    @app.exception_handler(ApiError)
    async def _api_error(_: Request, exc: ApiError):  # noqa: ANN202
        return error_response(exc.status, exc.error, exc.details)

    @app.exception_handler(RequestValidationError)
    async def _invalid(_: Request, exc: RequestValidationError):  # noqa: ANN202
        # Where and what, never the value that was sent: it could be a link or a token.
        details = [{"loc": [str(p) for p in e.get("loc", ())], "msg": e.get("msg", "")} for e in exc.errors()]
        return error_response(400, "Invalid request", details)

    app.include_router(api_router)
    app.include_router(agent_router)
    app.include_router(recordings_router)
    app.include_router(auth_router)
    app.include_router(admin_router)
    app.include_router(dashboard_router)
    app.include_router(dashboard_operations_router)
    app.include_router(delegated_runs_router)
    app.include_router(attendance_router)
    app.include_router(attendance_dashboard_router)
    app.include_router(activity_router)
    app.include_router(activity_dashboard_router)
    app.include_router(app_updates_router)
    app.include_router(user_data_router)

    # The dashboard's web page, when it has been built (Dashboard/dist, or CENTRAL_DASHBOARD_DIST).
    dist = dashboard_dist or Path(os.environ.get("CENTRAL_DASHBOARD_DIST") or DEFAULT_DASHBOARD_DIST)
    if (dist / "index.html").is_file():
        app.include_router(site_router(dist))
        emit("dashboard.served", path="/dashboard/", registration="open" if auth_settings.allow_registration else "closed")
    else:
        emit("dashboard.not_built", hint="cd Dashboard && npm run build")
    return app


def main() -> None:  # pragma: no cover - convenience entry point
    import uvicorn

    uvicorn.run(create_app(), host="127.0.0.1", port=8080, proxy_headers=True, ws_max_size=64 * 1024)
