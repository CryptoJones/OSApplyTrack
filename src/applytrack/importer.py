# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""One-shot migration of the legacy on-disk Markdown apps into Postgres.

The original applytrack stored one ``*.md`` file per application; the .NET API now
owns CRUD over the ``applications`` table. This module reuses the existing
:func:`applytrack.store.parse_app` codec (no reason to reimplement YAML parsing in
C# for a one-time copy) and upserts each file into the shared table, scoped to a
single ``tenant_id``. The file's own name is kept as the row's ``name`` (the slug
the SPA links by), so links survive the move.

Connection config follows the same ``.env`` the compose stack uses: pass an
explicit libpq URL, set ``DATABASE_URL``, or let the ``POSTGRES_*`` vars build one.
"""

from __future__ import annotations

import os
from collections.abc import Iterator
from pathlib import Path
from typing import Any

import psycopg

from applytrack.store import AppFields, parse_app, today

DEFAULT_STATEMENT_TIMEOUT_SECONDS = 30

# Column order shared by the INSERT and its VALUES list — the structured fields
# plus the slug + tenant. ``version``/``created_at``/``updated_at`` default in SQL.
_FIELD_COLUMNS = (
    "company", "role", "lane", "status", "link", "location", "salary", "source",
    "contact", "contact_email", "applied", "followup", "created", "score", "notes",
)

_UPSERT_SQL = f"""
INSERT INTO applications (tenant_id, name, {", ".join(_FIELD_COLUMNS)})
VALUES (%(tenant_id)s, %(name)s, {", ".join(f"%({c})s" for c in _FIELD_COLUMNS)})
ON CONFLICT (tenant_id, name) DO UPDATE SET
    {", ".join(f"{c} = EXCLUDED.{c}" for c in _FIELD_COLUMNS)},
    version = applications.version + 1,
    updated_at = now()
"""


def row_params(name: str, fields: AppFields, tenant_id: int) -> dict[str, Any]:
    """Flatten a parsed app into the upsert's named parameters (pure, DB-free)."""
    params: dict[str, Any] = {"tenant_id": tenant_id, "name": name}
    for col in _FIELD_COLUMNS:
        params[col] = getattr(fields, col)
    # Preserve the file's created date; only synthesize one when it was blank,
    # matching the API's create path.
    params["created"] = fields.created or today()
    return params


def iter_markdown(data_dir: Path) -> Iterator[tuple[str, AppFields]]:
    """Yield (name, parsed fields) for each non-dotfile ``*.md`` in the directory."""
    if not data_dir.is_dir():
        raise FileNotFoundError(f"not a directory: {data_dir}")
    for path in sorted(data_dir.glob("*.md")):
        if path.name.startswith("."):
            continue
        yield path.name, parse_app(path.read_text(encoding="utf-8"))


def _statement_timeout_options() -> dict[str, str]:
    raw = os.environ.get("APPLYTRACK_STATEMENT_TIMEOUT_SECONDS")
    try:
        seconds = int(raw) if raw is not None else DEFAULT_STATEMENT_TIMEOUT_SECONDS
    except ValueError:
        seconds = DEFAULT_STATEMENT_TIMEOUT_SECONDS
    if seconds <= 0:
        return {}
    return {"options": f"-c statement_timeout={seconds}s"}


def connect(database_url: str | None = None) -> psycopg.Connection:
    """Open a psycopg connection from an explicit URL, ``DATABASE_URL``, or PG env."""
    options = _statement_timeout_options()
    url = database_url or os.environ.get("DATABASE_URL")
    if url:
        return psycopg.connect(url, **options)  # type: ignore[arg-type]
    return psycopg.connect(
        host=os.environ.get("PGHOST", "localhost"),
        port=os.environ.get("PGPORT", "5432"),
        dbname=os.environ.get("POSTGRES_DB", "applytrack"),
        user=os.environ.get("POSTGRES_USER", "applytrack"),
        password=os.environ.get("POSTGRES_PASSWORD", "applytrack"),
        **options,  # type: ignore[arg-type]
    )


def import_markdown(conn: psycopg.Connection, data_dir: Path, tenant_id: int) -> list[str]:
    """Upsert every Markdown app under ``data_dir`` into ``applications``.

    Returns the slug names imported, in directory order. The whole run is one
    transaction so a mid-import failure leaves the table untouched.
    """
    imported: list[str] = []
    with conn.transaction(), conn.cursor() as cur:
        for name, fields in iter_markdown(data_dir):
            cur.execute(_UPSERT_SQL, row_params(name, fields, tenant_id))
            imported.append(name)
    return imported
