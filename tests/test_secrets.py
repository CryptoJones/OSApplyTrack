# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""The poller opens what the API sealed, with the key the API uses (#340)."""

from __future__ import annotations

import logging
from pathlib import Path

import pytest
import yaml

from applytrack import secrets
from applytrack.db import PollRepo

ROOT = Path(__file__).resolve().parents[1]

# Also opened by the API's SecretProtectorTests: the one token both runtimes must read.
VECTOR_KEY = "cross-runtime-vector-key"
VECTOR_PLAIN = "li_at=AQEDAT-sealed-by-the-api"
VECTOR_TOKEN = (
    "enc:v1:0c528fdc:"
    "qL1AhGMt5TvB7Q5a3NXDWXG8+87coW/3pBVxYKRlpZxHkq7mMt78xWBxRQOI8n4iqiHWytfye1kOXQ=="
)


def test_the_shared_vector_opens_here_as_it_does_in_the_api() -> None:
    assert secrets.unseal(VECTOR_TOKEN, VECTOR_KEY) == VECTOR_PLAIN


def test_an_empty_master_key_neither_seals_nor_unseals() -> None:
    with pytest.raises(secrets.SealError):
        secrets.seal("hunter2", "")
    with pytest.raises(secrets.SealError):
        secrets.unseal(VECTOR_TOKEN, "")


def test_a_mangled_body_is_a_seal_error_not_a_binascii_error() -> None:
    fp = VECTOR_TOKEN.split(":")[2]
    with pytest.raises(secrets.SealError):
        secrets.unseal(f"enc:v1:{fp}:not*base64!", VECTOR_KEY)


def test_the_env_key_wins_and_the_apis_key_file_is_the_fallback(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    key_file = tmp_path / "secrets.key"
    key_file.write_text(VECTOR_KEY + "\n")
    monkeypatch.setenv("APPLYTRACK_SECRETS_KEY_FILE", str(key_file))

    monkeypatch.setenv("APPLYTRACK_SECRETS_KEY", " from-env ")
    assert secrets.master_key() == "from-env"

    monkeypatch.delenv("APPLYTRACK_SECRETS_KEY")
    assert secrets.master_key() == VECTOR_KEY
    assert secrets.unseal(VECTOR_TOKEN, secrets.master_key()) == VECTOR_PLAIN

    key_file.unlink()
    assert secrets.master_key() == ""


class _Cursor:
    def __init__(self, conn: _Conn) -> None:
        self._conn = conn

    def __enter__(self) -> _Cursor:
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def execute(self, sql: str, params: tuple[object, ...]) -> None:
        self._conn.executed.append((sql, params))

    def fetchone(self) -> tuple[object, ...] | None:
        return self._conn.row


class _Conn:
    def __init__(self, row: tuple[object, ...] | None) -> None:
        self.row = row
        self.executed: list[tuple[str, tuple[object, ...]]] = []

    def cursor(self) -> _Cursor:
        return _Cursor(self)


def test_a_session_under_another_key_is_logged_not_silently_dropped(
    monkeypatch: pytest.MonkeyPatch, caplog: pytest.LogCaptureFixture
) -> None:
    monkeypatch.setenv("APPLYTRACK_SECRETS_KEY", "not-the-apis-key")
    conn = _Conn(("linkedin.com", "me@example.com", VECTOR_TOKEN, None))
    with caplog.at_level(logging.WARNING, logger="applytrack.db"):
        account = PollRepo(conn, 7).linkedin_account()  # type: ignore[arg-type]
    assert account is not None and account.session == ""
    [record] = caplog.records
    assert "tenant 7" in record.getMessage() and "linkedin.com" in record.getMessage()
    assert VECTOR_PLAIN not in record.getMessage() and "not-the-apis-key" not in record.getMessage()


def test_the_right_key_opens_the_session_the_api_sealed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("APPLYTRACK_SECRETS_KEY", VECTOR_KEY)
    conn = _Conn(("linkedin.com", "me@example.com", VECTOR_TOKEN, None))
    account = PollRepo(conn, 7).linkedin_account()  # type: ignore[arg-type]
    assert account is not None and account.session == VECTOR_PLAIN


def test_without_a_key_a_renewed_session_is_not_written_under_an_empty_one(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.delenv("APPLYTRACK_SECRETS_KEY", raising=False)
    monkeypatch.setenv("APPLYTRACK_SECRETS_KEY_FILE", str(tmp_path / "absent.key"))
    conn = _Conn(None)
    PollRepo(conn, 7).save_linkedin_session("li_at=fresh", None)  # type: ignore[arg-type]
    assert conn.executed == []


def test_every_deploy_hands_the_poller_the_key() -> None:
    dev = yaml.safe_load((ROOT / "docker-compose.yml").read_text())["services"]["poller"]
    assert "APPLYTRACK_SECRETS_KEY" in dev["environment"]
    assert "secrets:/var/lib/applytrack:ro" in dev["volumes"]
    assert dev["user"] == "1654:1654"  # the api's uid: the generated key file is owner-only

    prod = yaml.safe_load((ROOT / "docker-compose.production.yml").read_text())["services"]
    key = "APPLYTRACK_SECRETS_KEY"
    assert prod["poller"]["environment"][key] == prod["api"]["environment"][key]

    quadlet = (ROOT / "deploy/quadlet/applytrack-poller.container").read_text()
    assert "Volume=applytrack-secrets.volume:/var/lib/applytrack:ro" in quadlet
    assert "User=1654:1654" in quadlet
