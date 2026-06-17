# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""DB-free tests for the import-md migration's pure pieces.

The live upsert is exercised end-to-end against Postgres during Step 1 verification;
here we only cover the parts that don't need a database: parsing a directory of
Markdown apps and flattening a parsed app into the upsert's named parameters.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from applytrack.importer import connect, iter_markdown, row_params
from applytrack.store import parse_app, today


def test_row_params_maps_every_column_and_keeps_created() -> None:
    fields = parse_app(
        "---\ncompany: Acme\nrole: Engineer\nstatus: applied\n"
        "created: '2020-01-01'\n---\n\nnotes here\n"
    )
    params = row_params("acme-engineer.md", fields, tenant_id=7)

    assert params["tenant_id"] == 7
    assert params["name"] == "acme-engineer.md"
    assert params["company"] == "Acme"
    assert params["role"] == "Engineer"
    assert params["status"] == "applied"
    assert params["created"] == "2020-01-01"  # preserved, not overwritten
    assert params["notes"] == "notes here"


def test_row_params_synthesizes_created_when_blank() -> None:
    fields = parse_app("---\ncompany: Acme\n---\n")
    assert row_params("acme.md", fields, tenant_id=1)["created"] == today()


def test_iter_markdown_skips_dotfiles_and_sorts(tmp_path: Path) -> None:
    (tmp_path / "beta.md").write_text("---\ncompany: Beta\n---\n", encoding="utf-8")
    (tmp_path / "alpha.md").write_text("---\ncompany: Alpha\n---\n", encoding="utf-8")
    (tmp_path / ".criteria.md").write_text("---\ncompany: Hidden\n---\n", encoding="utf-8")
    (tmp_path / "notes.txt").write_text("ignored", encoding="utf-8")

    names = [name for name, _ in iter_markdown(tmp_path)]
    assert names == ["alpha.md", "beta.md"]


def test_connect_sets_statement_timeout(monkeypatch: pytest.MonkeyPatch) -> None:
    calls: dict[str, object] = {}

    def fake_connect(url: str, **kwargs: object) -> object:
        calls["url"] = url
        calls.update(kwargs)
        return object()

    monkeypatch.setenv("APPLYTRACK_STATEMENT_TIMEOUT_SECONDS", "45")
    monkeypatch.setattr("applytrack.importer.psycopg.connect", fake_connect)

    connect("postgresql://db/app")

    assert calls == {
        "url": "postgresql://db/app",
        "options": "-c statement_timeout=45s",
    }


def test_connect_ignores_non_positive_statement_timeout(monkeypatch: pytest.MonkeyPatch) -> None:
    calls: dict[str, object] = {}

    def fake_connect(url: str, **kwargs: object) -> object:
        calls["url"] = url
        calls.update(kwargs)
        return object()

    monkeypatch.setenv("APPLYTRACK_STATEMENT_TIMEOUT_SECONDS", "0")
    monkeypatch.setattr("applytrack.importer.psycopg.connect", fake_connect)

    connect("postgresql://db/app")

    assert calls == {"url": "postgresql://db/app"}
