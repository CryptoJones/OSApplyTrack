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

import logging
from collections.abc import Callable, Iterable
from typing import Protocol

import httpx
import psycopg

from applytrack import linkedin, mygreenhouse
from applytrack.criteria import AtsBoard, Criteria
from applytrack.db import PollRepo
from applytrack.linkcheck import BROWSER_HEADERS
from applytrack.poll import (
    SOURCE_FETCHERS,
    LeadRepo,
    Listing,
    _select_for_profile,
    make_ats_fetcher,
    make_rss_fetcher,
    score_and_stage,
)

logger = logging.getLogger(__name__)

_TENANT_POLL_LOCK_PREFIX = "applytrack:tenant-poll:"


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


def _try_tenant_poll_lock(conn: psycopg.Connection, tenant_id: int) -> bool:
    """Acquire this tenant's PostgreSQL session advisory lock without waiting."""
    with conn.cursor() as cur:
        cur.execute(
            "SELECT pg_try_advisory_lock(hashtextextended(%s, 0))",
            (f"{_TENANT_POLL_LOCK_PREFIX}{tenant_id}",),
        )
        row = cur.fetchone()
    return bool(row and row[0])


def _release_tenant_poll_lock(conn: psycopg.Connection, tenant_id: int) -> None:
    """Release a tenant poll lock acquired by :func:`_try_tenant_poll_lock`."""
    with conn.cursor() as cur:
        cur.execute(
            "SELECT pg_advisory_unlock(hashtextextended(%s, 0))",
            (f"{_TENANT_POLL_LOCK_PREFIX}{tenant_id}",),
        )


def _gather_by_source(
    profiles: Iterable[Criteria], limit: int, *, ats_only: bool = False
) -> dict[str, list[Listing]]:
    """Fetch every source enabled by *any* profile once, keyed by source id.

    Built-in sources key by name (``"remotive"``); ATS boards key by
    ``"{provider}:{slug}"`` and custom RSS feeds by ``"rss:{url}"`` — the same keys
    :func:`_select_for_profile` routes by, so two tenants following the same feed
    cost one request. A failing source yields an empty bucket and is logged at
    WARNING (rather than aborting the gather or vanishing silently), so a
    dead/renamed source is visible in the logs instead of looking like an empty
    market for every tenant.

    ``ats_only`` gathers *only* the tenants' ATS boards (Greenhouse/Lever/Ashby and
    the like), skipping the built-in aggregators and custom RSS feeds. It is the
    freshness fast lane: ATS boards close fastest, and hitting only the per-employer
    board APIs on a short cadence does not touch the shared aggregators' rate limits,
    so this can run far more often than the full hourly poll.
    """
    builtin: set[str] = set()
    boards: dict[str, AtsBoard] = {}
    feeds: dict[str, str] = {}
    for profile in profiles:
        for name, on in profile.sources.items():
            if on and name in SOURCE_FETCHERS:
                builtin.add(name)
        for board in profile.ats_boards:
            boards[f"{board.provider}:{board.slug}"] = board
        for url in profile.rss_feeds:
            feeds[f"rss:{url}"] = url
    if ats_only:
        builtin.clear()
        feeds.clear()

    gathered: dict[str, list[Listing]] = {}
    with httpx.Client(timeout=20.0, follow_redirects=True, headers=BROWSER_HEADERS) as client:
        for name in sorted(builtin):
            try:
                gathered[name] = SOURCE_FETCHERS[name](client, limit)
            except Exception:  # noqa: BLE001 - one bad source must not abort the gather
                gathered[name] = []
                logger.warning("poll source %s failed", name, exc_info=True)
        for key, board in boards.items():
            fetcher = make_ats_fetcher(board)
            if fetcher is None:
                continue
            try:
                gathered[key] = fetcher(client, limit)
            except Exception:  # noqa: BLE001 - one bad source must not abort the gather
                gathered[key] = []
                logger.warning("poll source %s failed", key, exc_info=True)
        for key, url in feeds.items():
            try:
                # Ignores `client` by design — a user-supplied feed URL is fetched
                # through the SSRF-guarded transport (see poll.fetch_rss).
                gathered[key] = make_rss_fetcher(url)(client, limit)
            except Exception:  # noqa: BLE001 - one bad feed must not abort the gather
                gathered[key] = []
                logger.warning("poll source %s failed", key, exc_info=True)
    return gathered


def run_all_tenants(
    conn: psycopg.Connection | None = None,
    *,
    limit_per_source: int = 40,
    tenant_ids: Iterable[int] | None = None,
    repo_for: Callable[[int], TenantRepo] | None = None,
    gathered: dict[str, list[Listing]] | None = None,
    verify_links: bool = True,
    locked_tenant_ids: set[int] | None = None,
    ats_only: bool = False,
) -> dict[int, list[str]]:
    """Poll every active tenant, returning ``{tenant_id: [staged slug names]}``.

    Production passes only ``conn`` (autocommit): the active-tenant list and each
    tenant's :class:`~applytrack.db.PollRepo` are derived from it, sources are
    fetched once, and the shared buckets are routed per profile. The ``tenant_ids``
    / ``repo_for`` / ``gathered`` seams let the offline tests drive the same
    fan-out with in-memory fakes and a fixed listing set (no DB, no network).

    Production also holds a PostgreSQL session advisory lock for each tenant from
    profile setup through source gathering and staging. A concurrent drain,
    scheduled run, or second poller container skips an already-locked tenant
    without waiting. ``locked_tenant_ids`` lets the queue drainer re-enqueue those
    skipped requests for a later attempt.

    Per-tenant failures are isolated: a tenant whose poll raises is recorded with
    an empty result and the run continues.

    ``ats_only`` restricts the shared gather to the tenants' ATS boards — the
    freshness fast lane (see :func:`_gather_by_source`). It is ignored when a
    ready-made ``gathered`` is supplied.
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

    acquired_locks: list[int] = []
    pollable_tenant_ids: list[int] = []
    for tid in tenant_ids:
        if conn is None:
            pollable_tenant_ids.append(tid)
            continue
        try:
            if _try_tenant_poll_lock(conn, tid):
                acquired_locks.append(tid)
                pollable_tenant_ids.append(tid)
                continue
            if locked_tenant_ids is not None:
                locked_tenant_ids.add(tid)
            logger.info(
                "poll skipped for tenant %s: another poll is already active",
                tid,
            )
        except Exception:  # noqa: BLE001 - one lock failure must not abort the run
            logger.warning("poll lock acquisition failed for tenant %s", tid, exc_info=True)

    # Build the per-tenant repo + profile up front: this is where each tenant's
    # WHERE tenant_id scoping is fixed for the rest of the run. A tenant whose
    # setup raises is isolated here so the shared gather still runs for the rest.
    repos: dict[int, TenantRepo] = {}
    profiles: dict[int, Criteria] = {}
    results: dict[int, list[str]] = {
        tid: [] for tid in tenant_ids if tid not in pollable_tenant_ids
    }
    try:
        for tid in pollable_tenant_ids:
            try:
                repo = repo_for(tid)
                profiles[tid] = repo.load_profile()
                repos[tid] = repo
            except Exception:  # noqa: BLE001 - isolate one tenant's failure
                results[tid] = []
                logger.warning("poll setup failed for tenant %s", tid, exc_info=True)

        if gathered is None:
            gathered = _gather_by_source(profiles.values(), limit_per_source, ats_only=ats_only)

        for tid in pollable_tenant_ids:
            if tid not in repos:
                continue  # setup failed above; its empty result is already recorded
            try:
                listings = _select_for_profile(gathered, profiles[tid])
                # A signed-in source is the tenant's own, never shared: gathered here,
                # under a per-tenant key, only for a tenant that turned it on (#218).
                if profiles[tid].sources.get(mygreenhouse.SOURCE) and not ats_only:
                    key = f"{mygreenhouse.SOURCE}:{tid}"
                    if key not in gathered:
                        gathered[key] = _gather_portal(repos[tid], profiles[tid], limit_per_source)
                    listings = [*listings, *gathered[key]]
                if profiles[tid].sources.get(linkedin.SOURCE) and not ats_only:
                    key = f"{linkedin.SOURCE}:{tid}"
                    if key not in gathered:
                        gathered[key] = _gather_linkedin(
                            repos[tid], profiles[tid], limit_per_source
                        )
                    listings = [*listings, *gathered[key]]
                results[tid] = score_and_stage(
                    repos[tid], profiles[tid], listings, verify_links=verify_links
                )
            except Exception:  # noqa: BLE001 - isolate one tenant's failure
                results[tid] = []
                logger.warning("poll failed for tenant %s", tid, exc_info=True)
        return results
    finally:
        if conn is not None:
            for tid in acquired_locks:
                try:
                    _release_tenant_poll_lock(conn, tid)
                except Exception:  # noqa: BLE001 - release the remaining locks
                    logger.warning("poll lock release failed for tenant %s", tid, exc_info=True)


def _gather_portal(repo: TenantRepo, profile: Criteria, limit: int) -> list[Listing]:
    """One tenant's MyGreenhouse listings (#218), searched with the session the agent's
    browser keeps on the board-account row (#221). The poller never signs in itself: the
    portal answers a plain client's request for a security code with a redirect and no
    mail, so a missing or bounced session is cleared here and left for the agent to
    renew. Every failure is logged and yields an empty bucket — the rest of the poll goes on."""
    account_of = getattr(repo, "portal_account", None)
    if account_of is None:
        return []
    try:
        account = account_of()
        if account is None:
            logger.warning(
                "mygreenhouse is on but no board account for greenhouse.io is saved — "
                "add one under Settings · Agent · Board accounts"
            )
            return []
        keep = getattr(repo, "save_portal_session", None)
        if not account.session_fresh:
            logger.info(
                "mygreenhouse: no live session for %s — waiting for the agent's browser to "
                "sign in and keep one (#221)",
                account.email,
            )
            return []
        with httpx.Client(timeout=30.0, follow_redirects=False, headers=BROWSER_HEADERS) as client:
            portal = mygreenhouse.Portal(client, account)
            try:
                return mygreenhouse.fetch_listings(
                    portal, profile.keywords, remote_only=profile.remote_only, limit=limit
                )
            except mygreenhouse.NeedsSignIn:
                logger.info(
                    "mygreenhouse: the kept session for %s was bounced; cleared for the "
                    "agent's browser to renew (#221)",
                    account.email,
                )
                if keep is not None:
                    keep("", None)
                return []
    except Exception:  # noqa: BLE001 - one tenant's portal must not abort the poll
        logger.warning("poll source mygreenhouse failed", exc_info=True)
        return []


def _gather_linkedin(repo: TenantRepo, profile: Criteria, limit: int) -> list[Listing]:
    """One tenant's LinkedIn listings (#233): the offsite-apply postings behind the
    keyword searches, with the employer's link. Signed in with the session the agent's
    browser keeps on the board-account row when there is a live one; LinkedIn's guest
    search otherwise. A bounced session is cleared for the agent to renew. Every failure
    is logged and yields an empty bucket — the rest of the poll goes on."""
    account_of = getattr(repo, "linkedin_account", None)
    try:
        account = account_of() if account_of is not None else None
        keep = getattr(repo, "save_linkedin_session", None)
        if account is None:
            logger.info(
                "linkedin is on with no board account for linkedin.com — searching as a guest "
                "(rate-limited); save one under Settings · Agent · Board accounts"
            )
        elif not account.session_fresh:
            logger.info(
                "linkedin: no live session for %s — searching as a guest until the agent's "
                "browser signs in and keeps one",
                account.email,
            )
            account = None
        seen_url = getattr(repo, "seen_url", None) or (lambda _url: False)

        def remember(url: str) -> None:
            from applytrack.poll import _norm_url

            repo.mark_seen(_norm_url(url), "")

        with httpx.Client(timeout=30.0, follow_redirects=False, headers=BROWSER_HEADERS) as client:
            li = linkedin.Client(client, account)
            try:
                return linkedin.fetch_listings(
                    li, profile.keywords, remote_only=profile.remote_only, limit=limit,
                    already_seen=seen_url, remember=remember,
                )
            except linkedin.NeedsSignIn:
                logger.info(
                    "linkedin: the kept session for %s was bounced; cleared for the agent's "
                    "browser to renew",
                    account.email if account else "?",
                )
                if keep is not None and account is not None:
                    keep("", None)
                return []
    except Exception:  # noqa: BLE001 - one tenant's LinkedIn must not abort the poll
        logger.warning("poll source linkedin failed", exc_info=True)
        return []


def drain_requests(
    conn: psycopg.Connection,
    *,
    limit_per_source: int = 40,
    repo_for: Callable[[int], TenantRepo] | None = None,
) -> dict[int, list[str]]:
    """Service the on-demand poll queue: claim every request and poll those tenants.

    ``POST /api/poll`` (the SPA's button) enqueues a ``poll_requests`` row; a fast
    cron runs this — typically every minute — so the button doesn't wait for the
    hourly pass. Rows are claimed with ``DELETE ... RETURNING``: atomic, so a crash
    or a second drainer can't double-process, and each distinct tenant is polled
    once. The connection must be in **autocommit** mode so the claim commits before
    the (slower) poll runs. This global claim is privileged like
    :func:`_active_tenant_ids`; everything it fans out to is re-scoped per tenant
    through :func:`run_all_tenants`.
    """
    with conn.cursor() as cur:
        cur.execute("DELETE FROM poll_requests RETURNING tenant_id")
        tenant_ids = sorted({int(row[0]) for row in cur.fetchall()})
    if not tenant_ids:
        return {}
    locked_tenant_ids: set[int] = set()
    results = run_all_tenants(
        conn,
        limit_per_source=limit_per_source,
        tenant_ids=tenant_ids,
        repo_for=repo_for,
        locked_tenant_ids=locked_tenant_ids,
    )
    if locked_tenant_ids:
        with conn.cursor() as cur:
            for tenant_id in sorted(locked_tenant_ids):
                cur.execute(
                    "INSERT INTO poll_requests (tenant_id) VALUES (%s)",
                    (tenant_id,),
                )
        logger.info(
            "requeued %s tenant poll request(s) blocked by active polls",
            len(locked_tenant_ids),
        )
    return results
