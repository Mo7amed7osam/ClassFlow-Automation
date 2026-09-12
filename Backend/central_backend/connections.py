"""Which agents are connected to this process right now, and a safe way to send to one.

In memory, so V1 runs as a single backend instance (see ARCHITECTURE.md, Phase 2).
"""

from __future__ import annotations

import asyncio
import json
import uuid
from typing import Any, Protocol


class AgentSocket(Protocol):
    async def send_text(self, data: str) -> None: ...
    async def close(self, code: int = 1000, reason: str | None = None) -> None: ...


class AgentConnection:
    def __init__(self, device_id: uuid.UUID, socket: AgentSocket) -> None:
        self.device_id = device_id
        self.socket = socket
        self._send_lock = asyncio.Lock()
        self.closed = False

    async def send(self, message: dict[str, Any]) -> bool:
        """Send one message; False if the connection is gone (the caller decides what that means)."""
        if self.closed:
            return False
        try:
            async with self._send_lock:
                await self.socket.send_text(json.dumps(message, separators=(",", ":"), default=str))
            return True
        except Exception:  # noqa: BLE001 - any send failure means this connection is unusable
            self.closed = True
            return False

    async def close(self, code: int, reason: str) -> None:
        self.closed = True
        try:
            await self.socket.close(code=code, reason=reason)
        except Exception:  # noqa: BLE001
            pass


class ConnectionRegistry:
    def __init__(self) -> None:
        self._connections: dict[uuid.UUID, AgentConnection] = {}

    def connected_device_ids(self) -> set[uuid.UUID]:
        return {device_id for device_id, conn in self._connections.items() if not conn.closed}

    def get(self, device_id: uuid.UUID) -> AgentConnection | None:
        conn = self._connections.get(device_id)
        return conn if conn is not None and not conn.closed else None

    async def register(self, connection: AgentConnection) -> None:
        """A device has one connection; a newer one replaces (and closes) the older."""
        previous = self._connections.get(connection.device_id)
        self._connections[connection.device_id] = connection
        if previous is not None and previous is not connection:
            await previous.close(4000, "replaced by a newer connection")

    def unregister(self, connection: AgentConnection) -> bool:
        """Forget this connection if it is still the device's current one."""
        connection.closed = True
        if self._connections.get(connection.device_id) is connection:
            del self._connections[connection.device_id]
            return True
        return False

    async def send(self, device_id: uuid.UUID, message: dict[str, Any]) -> bool:
        conn = self.get(device_id)
        return await conn.send(message) if conn is not None else False

    async def close(self, device_id: uuid.UUID, code: int, reason: str) -> None:
        conn = self._connections.get(device_id)
        if conn is not None:
            await conn.close(code, reason)
