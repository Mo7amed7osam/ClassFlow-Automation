"""Tokens and keys: how they are made, stored (as hashes) and checked (in constant time).

Every secret here is 256 bits from `secrets`, so a plain SHA-256 is the right storage: there is
nothing to brute-force, and a slow password hash would only slow every request down.
"""

from __future__ import annotations

import hashlib
import hmac
import secrets
import uuid
from collections.abc import Iterable

CLIENT_KEY_PREFIX = "zaak_"
ENROLLMENT_TOKEN_PREFIX = "zaae_"
DEVICE_TOKEN_PREFIX = "zaad_"


def hash_secret(secret: str) -> str:
    return hashlib.sha256(secret.encode("utf-8")).hexdigest()


def matches_any(presented: str | None, hashes: Iterable[str]) -> bool:
    """Whether a presented secret hashes to one of `hashes`. Every hash is compared, in constant time."""
    if not presented:
        return False
    digest = hash_secret(presented.strip())
    found = False
    for stored in hashes:
        found |= hmac.compare_digest(digest, stored)
    return found


def new_client_api_key() -> str:
    return CLIENT_KEY_PREFIX + secrets.token_urlsafe(32)


def new_enrollment_token() -> str:
    return ENROLLMENT_TOKEN_PREFIX + secrets.token_urlsafe(32)


def new_device_token(device_id: uuid.UUID) -> str:
    """`zaad_<device id>.<secret>`: the id says which hash to compare with, the secret proves it."""
    return f"{DEVICE_TOKEN_PREFIX}{device_id}.{secrets.token_urlsafe(32)}"


def device_id_of(token: str | None) -> uuid.UUID | None:
    """The device a token claims to be, or None for anything that is not shaped like a device token."""
    if not token or not token.startswith(DEVICE_TOKEN_PREFIX) or len(token) > 200:
        return None
    claimed, _, secret = token[len(DEVICE_TOKEN_PREFIX):].partition(".")
    if not secret:
        return None
    try:
        return uuid.UUID(claimed)
    except ValueError:
        return None


def bearer_token(authorization: str | None) -> str | None:
    if not authorization:
        return None
    scheme, _, value = authorization.strip().partition(" ")
    if scheme.lower() != "bearer":
        return None
    return value.strip() or None
