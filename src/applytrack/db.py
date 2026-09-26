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

import logging
from collections.abc import Collection, Iterable, Iterator

import psycopg

from applytrack import handshake, linkedin, secrets
from applytrack.criteria import Criteria
from applytrack.importer import _FIELD_COLUMNS, row_params
from applytrack.mygreenhouse import ACCOUNT_HOSTS, PortalAccount
from applytrack.store import AppFields, filename_for

logger = logging.getLogger(__name__)

# A plain INSERT: if a slug or URL already exists for this tenant, the unique
# constraint halts the insert and the caller skips staging a duplicate lead.
_INSERT_SQL = f"""
INSERT INTO applications (tenant_id, name, {", ".join(_FIELD_COLUMNS)})
VALUES (%(tenant_id)s, %(name)s, {", ".join(f"%({c})s" for c in _FIELD_COLUMNS)})
"""

# Column order mirrors search_profiles; the keys match Criteria.from_dict's dict.
_PROFILE_COLUMNS = (
    "keywords",
    "default_lane",
    "min_fit_score",
    "remote_only",
    "exclude_locations",
    "sources",
    "ats_boards",
    "rss_feeds",
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
        # The ledger's URL keys, read on the first seen_url() (#348).
        self._seen_urls: set[str] | None = None

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

    def _account(self, hosts: tuple[str, ...]) -> tuple[str, str, object] | None:
        """``(username, session_plaintext, session_expires_at)`` of the tenant's board
        account on one of ``hosts``, the session unsealed — or None when there is none."""
        with self._conn.cursor() as cur:
            cur.execute(
                "SELECT host, username, session_ciphertext, session_expires_at "
                "FROM board_accounts WHERE tenant_id = %s AND host = ANY(%s)",
                (self._t, list(hosts)),
            )
            row = cur.fetchone()
        if row is None or not row[1]:
            return None
        session = ""
        if row[2]:
            try:
                session = secrets.unseal(row[2], secrets.master_key())
            except secrets.SealError as exc:
                # Say so: without this the board just reads "no live session" (#340).
                logger.warning(
                    "tenant %s: the kept %s session cannot be unsealed (%s) — is "
                    "APPLYTRACK_SECRETS_KEY (or the API's key file) the one the API uses?",
                    self._t, row[0], exc,
                )
                session = ""
        return row[1], session, row[3]

    def _save_session(self, hosts: tuple[str, ...], session: str, expires_at: object) -> None:
        try:
            sealed = secrets.seal(session, secrets.master_key()) if session else ""
        except secrets.SealError as exc:
            logger.warning("tenant %s: not keeping the %s session (%s)", self._t, hosts[0], exc)
            return
        with self._conn.cursor() as cur:
            cur.execute(
                "UPDATE board_accounts SET session_ciphertext = %s, session_expires_at = %s, "
                "updated_at = now() WHERE tenant_id = %s AND host = ANY(%s)",
                (sealed, expires_at if session else None, self._t, list(hosts)),
            )

    def portal_account(self) -> PortalAccount | None:
        """The tenant's MyGreenhouse sign-in (a board account for greenhouse.io) with the
        session kept for it, unsealed — or None when there is no such account (#218)."""
        found = self._account(ACCOUNT_HOSTS)
        if found is None:
            return None
        email, session, expires = found
        return PortalAccount(email=email, session=session, session_expires_at=expires)  # type: ignore[arg-type]

    def save_portal_session(self, session: str, expires_at: object) -> None:
        """Keep the portal session sealed on the board-account row (blank clears it)."""
        self._save_session(ACCOUNT_HOSTS, session, expires_at)

    def linkedin_account(self) -> linkedin.LinkedInAccount | None:
        """The tenant's LinkedIn sign-in (a board account for linkedin.com) with the
        session kept for it, unsealed — or None when there is no such account (#233)."""
        found = self._account(linkedin.ACCOUNT_HOSTS)
        if found is None:
            return None
        email, session, expires = found
        return linkedin.LinkedInAccount(email=email, session=session, session_expires_at=expires)  # type: ignore[arg-type]

    def linkedin_easy_enabled(self) -> bool:
        """Whether this tenant has switched LinkedIn Easy Apply on (#278): the poller stages
        Easy Apply postings only then. Read through ``to_jsonb`` so a database the API has
        not migrated yet (no ``linkedin_easy`` column) answers False instead of erroring."""
        with self._conn.cursor() as cur:
            cur.execute(
                "SELECT to_jsonb(s) ->> 'linkedin_easy' FROM agent_settings s WHERE tenant_id = %s",
                (self._t,),
            )
            row = cur.fetchone()
        return row is not None and row[0] == "true"

    def jev_classify_enabled(self) -> bool:
        """Whether this tenant has opted in to Jev's semantic fit in place of the keyword
        score (#300). Read like ``linkedin_easy``: an unmigrated database answers False."""
        with self._conn.cursor() as cur:
            cur.execute(
                "SELECT to_jsonb(s) ->> 'jev_classify' FROM agent_settings s WHERE tenant_id = %s",
                (self._t,),
            )
            row = cur.fetchone()
        return row is not None and row[0] == "true"

    def save_linkedin_session(self, session: str, expires_at: object) -> None:
        """Keep the LinkedIn session sealed on the board-account row (blank clears it)."""
        self._save_session(linkedin.ACCOUNT_HOSTS, session, expires_at)

    def handshake_account(self) -> handshake.HandshakeAccount | None:
        """The tenant's Handshake sign-in (a board account for joinhandshake.com) with the
        session kept for it, unsealed — or None when there is no such account (#267)."""
        found = self._account(handshake.ACCOUNT_HOSTS)
        if found is None:
            return None
        username, session, expires = found
        return handshake.HandshakeAccount(
            username=username, session=session, session_expires_at=expires  # type: ignore[arg-type]
        )

    def save_handshake_session(self, session: str, expires_at: object) -> None:
        """Keep the Handshake session sealed on the board-account row (blank clears it)."""
        self._save_session(handshake.ACCOUNT_HOSTS, session, expires_at)

    def seen_url(self, url: str) -> bool:
        """Is this listing URL already in the tenant's ledger? A source that pays a
        request per listing asks before reading one (#233). The ledger's URL keys are
        read once per repo, on the first ask, and answered from memory after that --
        a Handshake pass asks up to 800 times (#348)."""
        from applytrack.poll import _norm_url

        key = _norm_url(url)
        if not key:
            return False
        if self._seen_urls is None:
            with self._conn.cursor() as cur:
                cur.execute(
                    "SELECT key FROM seen WHERE tenant_id = %s AND kind = 'url'", (self._t,)
                )
                self._seen_urls = {row[0] for row in cur.fetchall()}
        return key in self._seen_urls

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
            cur.execute("SELECT company FROM blacklist WHERE tenant_id = %s", (self._t,))
            return [row[0] for row in cur.fetchall()]

    def load_seen(
        self, url_keys: Collection[str], slug_keys: Collection[str]
    ) -> tuple[set[str], set[str]]:
        """Return which of these ``(url_keys, slug_keys)`` the tenant's ledger holds.

        Rows survive their originating application's deletion, so a lead the user
        removed is never re-discovered. ``kind`` partitions the two key spaces. Only
        the asked-for keys are read (a primary-key lookup each), never the whole
        ledger, which only grows (#348).
        """
        urls: set[str] = set()
        slugs: set[str] = set()
        if not url_keys and not slug_keys:
            return urls, slugs
        with self._conn.cursor() as cur:
            cur.execute(
                "SELECT kind, key FROM seen WHERE tenant_id = %s AND ("
                "(kind = 'url' AND key = ANY(%s::text[])) OR "
                "(kind = 'slug' AND key = ANY(%s::text[])))",
                (self._t, list(url_keys), list(slug_keys)),
            )
            for kind, key in cur.fetchall():
                (urls if kind == "url" else slugs).add(key)
        return urls, slugs

    def mark_seen_many(self, keys: Iterable[tuple[str, str]]) -> None:
        """Persist newly seen ``(url_key, slug_key)`` pairs in one statement rather
        than a round trip and a commit each (#348). Idempotent per (kind, key)."""
        kinds: list[str] = []
        values: list[str] = []
        for url_key, slug_key in keys:
            if url_key:
                kinds.append("url")
                values.append(url_key)
            if slug_key:
                kinds.append("slug")
                values.append(slug_key)
        if not values:
            return
        with self._conn.cursor() as cur:
            cur.execute(
                "INSERT INTO seen (tenant_id, kind, key) "
                "SELECT %s, k, v FROM unnest(%s::text[], %s::text[]) AS t(k, v) "
                "ON CONFLICT DO NOTHING",
                (self._t, kinds, values),
            )
        if self._seen_urls is not None:
            self._seen_urls.update(v for k, v in zip(kinds, values, strict=True) if k == "url")

    def add_lead(self, fields: AppFields) -> str:
        """Stage one new lead, returning its slug ``name``.

        The slug is minted from ``company`` and ``role``. If an application
        with this name already exists for this tenant,
        :class:`psycopg.errors.UniqueViolation` is raised so the caller can
        skip staging a duplicate lead.
        """
        name = filename_for(fields)
        with self._conn.cursor() as cur:
            cur.execute(_INSERT_SQL, row_params(name, fields, self._t))
        return name
