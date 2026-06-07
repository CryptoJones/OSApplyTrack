# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Multi-tenant cron worker: poll every active tenant in one pass.

A bare ``applytrack poll`` (no ``--tenant``) drives this. Where
:func:`applytrack.poll.run_poll` serves one tenant end-to-end, the worker
fetches each enabled source **once** across all tenants and then routes the
shared buckets per tenant: the public job boards are the slow, rate-limited part,
and re-fetching Remotive once per user would be both wasteful and a fast way to
get the host blocked.

The one privileged, cross-tenant query lives here —
``SELECT id FROM users WHERE status = 'active'``. Everything downstream is built
per-tenant through a :class:`~applytrack.db.PollRepo`, so the ``WHERE tenant_id``
choke-point still holds for every read and write. A failure polling one tenant is
isolated (caught, the rest of the run continues) so one bad profile can't starve
the others.
"""

from __future__ import annotations

from collections.abc import Callable, Iterable
from typing import Protocol

import httpx
import psycopg

from applytrack.criteria import AtsBoard, Criteria
from applytrack.db import PollRepo
from applytrack.linkcheck import BROWSER_HEADERS
from applytrack.poll import (
    SOURCE_FETCHERS,
    LeadRepo,
    Listing,
    _select_for_profile,
    make_ats_fetcher,
    score_and_stage,
)


class TenantRepo(LeadRepo, Protocol):
    """A :class:`~applytrack.poll.LeadRepo` that can also load its own profile.

    :class:`~applytrack.db.PollRepo` already satisfies this; the offline worker
    tests pass an in-memory fake shaped the same way.
    """

    def load_profile(self) -> Criteria:
        """Load this tenant's discovery :class:`~applytrack.criteria.Criteria`."""
        ...


def _active_tenant_ids(conn: psycopg.Connection) -> list[int]:
    """The privileged cross-tenant read: ids of every active user, ascending.

    This is the *only* query in either runtime not scoped to a single tenant; it
    exists so the cron worker knows whom to poll. Every read/write it fans out to
    is re-scoped through a per-tenant :class:`~applytrack.db.PollRepo`.
    """
    with conn.cursor() as cur:
        cur.execute("SELECT id FROM users WHERE status = 'active' ORDER BY id")
        return [int(row[0]) for row in cur.fetchall()]


def _gather_by_source(
    profiles: Iterable[Criteria], limit: int
) -> dict[str, list[Listing]]:
    """Fetch every source enabled by *any* profile once, keyed by source id.

    Built-in sources key by name (``"remotive"``); ATS boards key by
    ``"{provider}:{slug}"`` — the same keys :func:`_select_for_profile` routes by.
    Per-source failures yield an empty bucket rather than aborting the gather.
    """
    builtin: set[str] = set()
    boards: dict[str, AtsBoard] = {}
    for profile in profiles:
        for name, on in profile.sources.items():
            if on and name in SOURCE_FETCHERS:
                builtin.add(name)
        for board in profile.ats_boards:
            boards[f"{board.provider}:{board.slug}"] = board

    gathered: dict[str, list[Listing]] = {}
    with httpx.Client(timeout=20.0, follow_redirects=True, headers=BROWSER_HEADERS) as client:
        for name in sorted(builtin):
            try:
                gathered[name] = SOURCE_FETCHERS[name](client, limit)
            except (httpx.HTTPError, ValueError, KeyError):
                gathered[name] = []
        for key, board in boards.items():
            fetcher = make_ats_fetcher(board)
            if fetcher is None:
                continue
            try:
                gathered[key] = fetcher(client, limit)
            except (httpx.HTTPError, ValueError, KeyError):
                gathered[key] = []
    return gathered


def run_all_tenants(
    conn: psycopg.Connection | None = None,
    *,
    limit_per_source: int = 40,
    tenant_ids: Iterable[int] | None = None,
    repo_for: Callable[[int], TenantRepo] | None = None,
    gathered: dict[str, list[Listing]] | None = None,
    verify_links: bool = True,
) -> dict[int, list[str]]:
    """Poll every active tenant, returning ``{tenant_id: [staged slug names]}``.

    Production passes only ``conn`` (autocommit): the active-tenant list and each
    tenant's :class:`~applytrack.db.PollRepo` are derived from it, sources are
    fetched once, and the shared buckets are routed per profile. The ``tenant_ids``
    / ``repo_for`` / ``gathered`` seams let the offline tests drive the same
    fan-out with in-memory fakes and a fixed listing set (no DB, no network).

    Per-tenant failures are isolated: a tenant whose poll raises is recorded with
    an empty result and the run continues.
    """
    if repo_for is None:
        if conn is None:
            raise ValueError("run_all_tenants needs either conn or repo_for")
        bound = conn

        def repo_for(tid: int) -> TenantRepo:
            return PollRepo(bound, tid)

    if tenant_ids is None:
        if conn is None:
            raise ValueError("run_all_tenants needs either conn or tenant_ids")
        tenant_ids = _active_tenant_ids(conn)
    tenant_ids = list(tenant_ids)

    # Build the per-tenant repo + profile up front: this is where each tenant's
    # WHERE tenant_id scoping is fixed for the rest of the run. A tenant whose
    # setup raises is isolated here so the shared gather still runs for the rest.
    repos: dict[int, TenantRepo] = {}
    profiles: dict[int, Criteria] = {}
    results: dict[int, list[str]] = {}
    for tid in tenant_ids:
        try:
            repo = repo_for(tid)
            profiles[tid] = repo.load_profile()
            repos[tid] = repo
        except Exception:  # noqa: BLE001 - one tenant's failure must not abort the rest
            results[tid] = []

    if gathered is None:
        gathered = _gather_by_source(profiles.values(), limit_per_source)

    for tid in tenant_ids:
        if tid not in repos:
            continue  # setup failed above; its empty result is already recorded
        try:
            listings = _select_for_profile(gathered, profiles[tid])
            results[tid] = score_and_stage(
                repos[tid], profiles[tid], listings, verify_links=verify_links
            )
        except Exception:  # noqa: BLE001 - one tenant's failure must not abort the rest
            results[tid] = []
    return results
