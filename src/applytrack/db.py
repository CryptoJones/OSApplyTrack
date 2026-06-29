# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Tenant-scoped Postgres access for the discovery poller (psycopg3).

The poller no longer reads ``applications/.criteria.json`` or writes ``*.md``
files: it shares one Postgres with the .NET API, where **the schema is the
contract**. This module is the thin Python side of that contract — it loads a
tenant's :class:`~applytrack.criteria.Criteria` from ``search_profiles``, reads
the rows it must dedup against (``applications`` + ``blacklist``), and stages new
leads back into ``applications``.

**Every** statement filters ``WHERE tenant_id`` — the same choke-point the .NET
``ApplicationRepo`` enforces. The jsonb columns map straight onto the loose dict
:meth:`Criteria.from_dict` already normalizes, so the two runtimes read the
profile through identical rules.

The connection should be in **autocommit** mode: each :meth:`PollRepo.add_lead`
stands alone, so one listing whose slug collides with an existing row raises a
unique-violation that the caller skips without poisoning the rest of the run.
"""

from __future__ import annotations

from collections.abc import Iterator

import psycopg

from applytrack.criteria import Criteria
from applytrack.importer import _FIELD_COLUMNS, row_params
from applytrack.store import AppFields, filename_for

# A plain INSERT (not the importer's upsert): a slug already present is a genuine
# collision, so :meth:`PollRepo.add_lead` retries under a suffixed name rather than
# overwriting a row the user may have since edited.
_INSERT_SQL = f"""
INSERT INTO applications (tenant_id, name, {", ".join(_FIELD_COLUMNS)})
VALUES (%(tenant_id)s, %(name)s, {", ".join(f"%({c})s" for c in _FIELD_COLUMNS)})
"""

# How many slug names (``stem.md``, ``stem-2.md`` … ``stem-N.md``) ``add_lead`` will
# try before giving up — a backstop against an unforeseen unique-violation loop.
_MAX_NAME_ATTEMPTS = 50

# Column order mirrors search_profiles; the keys match Criteria.from_dict's dict.
_PROFILE_COLUMNS = (
    "keywords", "default_lane", "min_fit_score", "remote_only",
    "exclude_locations", "sources", "ats_boards",
)
_PROFILE_SQL = f"""
SELECT {", ".join(_PROFILE_COLUMNS)}
FROM search_profiles
WHERE tenant_id = %s
"""


class PollRepo:
    """Tenant-scoped reader/writer the poller drives instead of the disk store."""

    def __init__(self, conn: psycopg.Connection, tenant_id: int) -> None:
        self._conn = conn
        self._t = tenant_id

    def load_profile(self) -> Criteria:
        """Load this tenant's criteria; fall back to defaults when no row exists.

        psycopg returns the jsonb columns already decoded (list / dict), so the
        row maps directly onto the dict :meth:`Criteria.from_dict` normalizes —
        the same path the .NET ``CriteriaRepo`` composes for ``GET /api/criteria``.
        """
        with self._conn.cursor() as cur:
            cur.execute(_PROFILE_SQL, (self._t,))
            row = cur.fetchone()
        if row is None:
            return Criteria()
        return Criteria.from_dict(dict(zip(_PROFILE_COLUMNS, row, strict=True)))

    def iter_existing(self) -> Iterator[tuple[str, str, str]]:
        """Yield ``(link, company, role)`` for every application already stored.

        These seed the in-memory dedup ledger so a role you already have a row for
        — staged earlier or created by hand in the SPA — is never re-added.
        """
        with self._conn.cursor() as cur:
            cur.execute(
                "SELECT link, company, role FROM applications WHERE tenant_id = %s",
                (self._t,),
            )
            rows = cur.fetchall()
        return ((link or "", company or "", role or "") for link, company, role in rows)

    def blacklist_companies(self) -> list[str]:
        """Return the tenant's blacklisted company keys (already normalized in SQL)."""
        with self._conn.cursor() as cur:
            cur.execute(
                "SELECT company FROM blacklist WHERE tenant_id = %s", (self._t,)
            )
            return [row[0] for row in cur.fetchall()]

    def load_seen(self) -> tuple[set[str], set[str]]:
        """Return the persisted ``(url_keys, slug_keys)`` dedup ledger for this tenant.

        Rows survive their originating application's deletion, so a lead the user
        removed is never re-discovered. ``kind`` partitions the two key spaces.
        """
        urls: set[str] = set()
        slugs: set[str] = set()
        with self._conn.cursor() as cur:
            cur.execute(
                "SELECT kind, key FROM seen WHERE tenant_id = %s", (self._t,)
            )
            for kind, key in cur.fetchall():
                (urls if kind == "url" else slugs).add(key)
        return urls, slugs

    def mark_seen(self, url_key: str, slug_key: str) -> None:
        """Persist newly seen keys; either may be empty. Idempotent per (kind, key)."""
        rows = []
        if url_key:
            rows.append((self._t, "url", url_key))
        if slug_key:
            rows.append((self._t, "slug", slug_key))
        if not rows:
            return
        with self._conn.cursor() as cur:
            cur.executemany(
                "INSERT INTO seen (tenant_id, kind, key) VALUES (%s, %s, %s) "
                "ON CONFLICT DO NOTHING",
                rows,
            )

    def add_lead(self, fields: AppFields) -> str:
        """Stage one new lead, returning its slug ``name``.

        When the natural ``company-role`` slug already exists for this tenant —
        two genuinely distinct postings (distinct URLs) whose titles normalize to
        the same name — a numeric ``-N`` suffix is appended so the second is
        staged rather than dropped. Autocommit means each failed INSERT stands
        alone, so the retry is clean. Only after :data:`_MAX_NAME_ATTEMPTS` free
        names are exhausted does the underlying
        :class:`psycopg.errors.UniqueViolation` propagate.
        """
        base = filename_for(fields)
        stem = base[:-3] if base.endswith(".md") else base
        name = base
        attempt = 1
        while True:
            try:
                with self._conn.cursor() as cur:
                    cur.execute(_INSERT_SQL, row_params(name, fields, self._t))
                return name
            except psycopg.errors.UniqueViolation:
                attempt += 1
                if attempt > _MAX_NAME_ATTEMPTS:
                    raise
                name = f"{stem}-{attempt}.md"
