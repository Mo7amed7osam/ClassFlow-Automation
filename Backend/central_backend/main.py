"""The application. Run it with:

    uvicorn central_backend.main:create_app --factory --host 127.0.0.1 --port 8080

Behind the TLS-terminating reverse proxy add `--proxy-headers --forwarded-allow-ips=<proxy address>`
so the request scheme is https/wss; outside development anything else is refused.
"""

from __future__ import annotations

import asyncio
import json
from collections.abc import AsyncIterator, Callable
from contextlib import asynccontextmanager
from datetime import UTC, datetime

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
from .observability import configure_logging, emit


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


def create_app(
    settings: Settings | None = None,
    *,
    clock: Callable[[], datetime] | None = None,
    run_background: bool = True,
    configure_logs: bool = True,
) -> FastAPI:
    if configure_logs:
        configure_logging()
    settings = settings or Settings.from_env()
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
    return app


def main() -> None:  # pragma: no cover - convenience entry point
    import uvicorn

    uvicorn.run(create_app(), host="127.0.0.1", port=8080, proxy_headers=True, ws_max_size=64 * 1024)
