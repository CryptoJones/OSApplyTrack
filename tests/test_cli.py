# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Smoke tests for the applytrack CLI entrypoint."""

from __future__ import annotations

from pathlib import Path

import pytest

from applytrack.cli import main
from applytrack.criteria import Criteria


class FakeConnection:
    autocommit = False

    def __enter__(self) -> FakeConnection:
        return self

    def __exit__(self, exc_type: object, exc: object, traceback: object) -> None:
        return None


class FakePollRepo:
    def __init__(self, conn: FakeConnection, tenant_id: int) -> None:
        self.conn = conn
        self.tenant_id = tenant_id

    def load_profile(self) -> Criteria:
        return Criteria()


def test_poll_tenant_dispatch(
    monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    calls: dict[str, object] = {}

    def fake_connect(database_url: str | None) -> FakeConnection:
        calls["database_url"] = database_url
        return FakeConnection()

    def fake_run_poll(repo: FakePollRepo, profile: Criteria, *, limit_per_source: int) -> list[str]:
        calls["repo_tenant"] = repo.tenant_id
        calls["limit"] = limit_per_source
        assert profile == Criteria()
        return ["acme-corp-engineer.md"]

    monkeypatch.setattr("applytrack.importer.connect", fake_connect)
    monkeypatch.setattr("applytrack.poll.run_poll", fake_run_poll)
    monkeypatch.setattr("applytrack.db.PollRepo", FakePollRepo)

    assert (
        main(
            ["poll", "--tenant", "7", "--database-url", "postgresql://db/app", "--limit", "12"]
        )
        == 0
    )

    assert calls == {
        "database_url": "postgresql://db/app",
        "repo_tenant": 7,
        "limit": 12,
    }
    captured = capsys.readouterr()
    assert "1 new lead(s) added for tenant 7" in captured.out
    assert "+ acme-corp-engineer.md" in captured.out


def test_poll_drain_dispatch(
    monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    calls: dict[str, object] = {}

    def fake_connect(database_url: str | None) -> FakeConnection:
        calls["database_url"] = database_url
        return FakeConnection()

    def fake_drain_requests(conn: FakeConnection, *, limit_per_source: int) -> dict[int, list[str]]:
        calls["limit"] = limit_per_source
        assert isinstance(conn, FakeConnection)
        return {3: ["globex-platform.md"]}

    monkeypatch.setattr("applytrack.importer.connect", fake_connect)
    monkeypatch.setattr("applytrack.worker.drain_requests", fake_drain_requests)

    assert main(["poll", "--drain", "--database-url", "postgresql://db/app", "--limit", "9"]) == 0

    assert calls == {"database_url": "postgresql://db/app", "limit": 9}
    captured = capsys.readouterr()
    assert "1 new lead(s) added across 1 queued tenant(s)" in captured.out


def test_import_md_dispatch(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    calls: dict[str, object] = {}

    def fake_connect(database_url: str | None) -> FakeConnection:
        calls["database_url"] = database_url
        return FakeConnection()

    def fake_import_markdown(conn: FakeConnection, data_dir: Path, tenant: int) -> list[str]:
        calls["dir"] = data_dir
        calls["tenant"] = tenant
        assert isinstance(conn, FakeConnection)
        return ["legacy.md"]

    monkeypatch.setattr("applytrack.importer.connect", fake_connect)
    monkeypatch.setattr("applytrack.importer.import_markdown", fake_import_markdown)

    assert (
        main(
            ["import-md", "--dir", str(tmp_path), "--tenant", "4", "--database-url", "postgresql://db/app"]
        )
        == 0
    )

    assert calls == {
        "database_url": "postgresql://db/app",
        "dir": tmp_path.resolve(),
        "tenant": 4,
    }
    captured = capsys.readouterr()
    assert "1 application(s) imported into tenant 4" in captured.out
