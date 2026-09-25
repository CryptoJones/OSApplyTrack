# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Seal and unseal secrets exactly as the API's ``SecretProtector`` does.

The two runtimes share one Postgres and the schema is the contract; a column the
API seals (a mailbox password, a board account's session) has to be readable here
with the same master key. Format: ``enc:v1:<fingerprint>:<base64(nonce||cipher||tag)>``
— AES-256-GCM under ``SHA-256(master)``, fingerprint = first four bytes of
``SHA-256(key)`` as lower-case hex, 12-byte nonce, 16-byte tag.

The master key is resolved the way the API's ``SecretKeySource`` does, minus the
generation: ``APPLYTRACK_SECRETS_KEY`` if set, else the key file the API generated
(``APPLYTRACK_SECRETS_KEY_FILE``, default ``/var/lib/applytrack/secrets.key`` in a
container) — the poller only reads it, never writes one (#340).
"""

from __future__ import annotations

import base64
import binascii
import hashlib
import logging
import os
import sys
from pathlib import Path

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

PREFIX = "enc:v1:"
_NONCE = 12
CONTAINER_KEY_FILE = "/var/lib/applytrack/secrets.key"

logger = logging.getLogger(__name__)


class SealError(ValueError):
    """The token is not one this key can open."""


def _key(master: str) -> tuple[bytes, str]:
    # The API refuses an empty master; sealing under SHA-256("") would be no seal at all.
    if not master:
        raise SealError("no master key (APPLYTRACK_SECRETS_KEY or the API's key file)")
    key = hashlib.sha256(master.encode("utf-8")).digest()
    return key, hashlib.sha256(key).digest()[:4].hex()


def key_file() -> Path:
    """Where the API keeps a generated key: ``APPLYTRACK_SECRETS_KEY_FILE``, else the
    container path when that exists, else the API's per-user default."""
    configured = os.environ.get("APPLYTRACK_SECRETS_KEY_FILE", "").strip()
    if configured:
        return Path(configured)
    if Path(CONTAINER_KEY_FILE).exists():
        return Path(CONTAINER_KEY_FILE)
    if sys.platform == "darwin":
        base = Path.home() / "Library" / "Application Support"
    else:
        base = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share")
    return base / "applytrack" / "secrets.key"


def master_key() -> str:
    """The operator's master key, as the API resolves it — or "" when there is none here."""
    configured = os.environ.get("APPLYTRACK_SECRETS_KEY", "").strip()
    if configured:
        return configured
    path = key_file()
    try:
        return path.read_text(encoding="utf-8").strip()
    except FileNotFoundError:
        return ""
    except OSError as exc:
        logger.warning(
            "secrets key file %s is unreadable (%s); sealed sessions cannot be opened",
            path, exc.strerror,
        )
        return ""


def seal(plain: str, master: str) -> str:
    key, fp = _key(master)
    nonce = os.urandom(_NONCE)
    sealed = AESGCM(key).encrypt(nonce, plain.encode("utf-8"), None)
    return PREFIX + fp + ":" + base64.b64encode(nonce + sealed).decode("ascii")


def unseal(token: str, master: str) -> str:
    if not token.startswith(PREFIX):
        raise SealError("not a sealed token")
    try:
        fp, b64 = token[len(PREFIX):].split(":", 1)
    except ValueError as exc:
        raise SealError("malformed token") from exc
    key, mine = _key(master)
    if fp != mine:
        raise SealError("sealed under a key this poller does not have")
    try:
        raw = base64.b64decode(b64, validate=True)
    except (binascii.Error, ValueError) as exc:
        raise SealError("malformed token") from exc
    if len(raw) < _NONCE + 16:
        raise SealError("token too short")
    try:
        return AESGCM(key).decrypt(raw[:_NONCE], raw[_NONCE:], None).decode("utf-8")
    except InvalidTag as exc:
        raise SealError("token failed to open") from exc
