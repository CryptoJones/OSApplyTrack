# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Seal and unseal secrets exactly as the API's ``SecretProtector`` does.

The two runtimes share one Postgres and the schema is the contract; a column the
API seals (a mailbox password, a board account's session) has to be readable here
with the same master key. Format: ``enc:v1:<fingerprint>:<base64(nonce||cipher||tag)>``
— AES-256-GCM under ``SHA-256(master)``, fingerprint = first four bytes of
``SHA-256(key)`` as lower-case hex, 12-byte nonce, 16-byte tag.
"""

from __future__ import annotations

import base64
import hashlib
import os

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

PREFIX = "enc:v1:"
_NONCE = 12


class SealError(ValueError):
    """The token is not one this key can open."""


def _key(master: str) -> tuple[bytes, str]:
    key = hashlib.sha256(master.encode("utf-8")).digest()
    return key, hashlib.sha256(key).digest()[:4].hex()


def master_key() -> str:
    """The operator's master key, as the API reads it (``APPLYTRACK_SECRETS_KEY``)."""
    return os.environ.get("APPLYTRACK_SECRETS_KEY", "")


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
    raw = base64.b64decode(b64)
    if len(raw) < _NONCE + 16:
        raise SealError("token too short")
    try:
        return AESGCM(key).decrypt(raw[:_NONCE], raw[_NONCE:], None).decode("utf-8")
    except InvalidTag as exc:
        raise SealError("token failed to open") from exc
