# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Tests for config-driven scoring, filtering, and the run_poll orchestration."""

from __future__ import annotations

import logging

import httpx
import psycopg
import pytest

from applytrack.criteria import AtsBoard, Criteria
from applytrack.linkcheck import PublicFetchError
from applytrack.poll import (
    Listing,
    _gather,
    _looks_remote,
    _passes_location,
    build_fetchers,
    classify,
    fetch_remoteok,
    fetch_remotive,
    fetch_rss,
    run_poll,
    score_and_stage,
)
from applytrack.store import AppFields, filename_for
from applytrack.worker import _gather_by_source, drain_requests, run_all_tenants


class FakeRepo:
    """In-memory :class:`applytrack.poll.LeadRepo` for offline ``run_poll`` tests.

    Stands in for :class:`applytrack.db.PollRepo` with no Postgres: it records
    staged leads, persists a ``seen`` ledger across runs the way the real table
    does, and mimics the unique-slug constraint the real table enforces. It also
    carries a :class:`~applytrack.criteria.Criteria` so the multi-tenant worker
    can drive it.
    """

    def __init__(
        self,
        *,
        existing: list[tuple[str, str, str]] | None = None,
        blacklist: list[str] | None = None,
        seen: list[tuple[str, str]] | None = None,
        profile: Criteria | None = None,
    ) -> None:
        self._existing = list(existing or [])
        self._blacklist = list(blacklist or [])
        self._seen_urls: set[str] = set()
        self._seen_slugs: set[str] = set()
        for kind, key in seen or []:
            (self._seen_urls if kind == "url" else self._seen_slugs).add(key)
        self._profile = profile if profile is not None else Criteria()
        self.added: list[AppFields] = []
        self._names: set[str] = set()

    def load_profile(self) -> Criteria:
        return self._profile

    def iter_existing(self) -> list[tuple[str, str, str]]:
        return list(self._existing)

    def blacklist_companies(self) -> list[str]:
        return list(self._blacklist)

    def load_seen(self) -> tuple[set[str], set[str]]:
        return set(self._seen_urls), set(self._seen_slugs)

    def mark_seen(self, url_key: str, slug_key: str) -> None:
        if url_key:
            self._seen_urls.add(url_key)
        if slug_key:
            self._seen_slugs.add(slug_key)

    def add_lead(self, fields: AppFields) -> str:
        # Mirror PollRepo.add_lead: suffix -N on a slug-name collision so two
        # genuinely distinct postings can coexist rather than the second failing.
        base = filename_for(fields)
        stem = base[:-3] if base.endswith(".md") else base
        name = base
        n = 1
        while name in self._names:
            n += 1
            name = f"{stem}-{n}.md"
        self._names.add(name)
        self.added.append(fields)
        return name


class FakeCursor:
    def __init__(self, rows: list[tuple[int]]) -> None:
        self.rows = rows
        self.sql = ""

    def __enter__(self) -> FakeCursor:
        return self

    def __exit__(self, exc_type: object, exc: object, traceback: object) -> None:
        return None

    def execute(self, sql: str, params: object = None) -> None:
        self.sql = sql

    def fetchall(self) -> list[tuple[int]]:
        return self.rows


class FakeConnection:
    def __init__(self, rows: list[tuple[int]]) -> None:
        self.rows = rows

    def __enter__(self) -> FakeConnection:
        return self

    def __exit__(self, exc_type: object, exc: object, traceback: object) -> None:
        return None

    def cursor(self) -> FakeCursor:
        return FakeCursor(self.rows)


def test_classify_weighs_title_over_body() -> None:
    score, hits = classify(
        "Senior .NET Engineer",
        "backend microservices",
        [".net", "backend engineer", "microservices"],
    )
    # base 50 + 9 (".net" in title) + 3 ("microservices" body-only) = 62
    assert score == 62
    assert hits == [".net", "microservices"]


def test_classify_no_match_scores_zero() -> None:
    assert classify("Plumber", "fixes pipes", ["ai engineer"]) == (0, [])


def test_looks_remote() -> None:
    assert _looks_remote(Listing(company="A", role="Eng", location=""))
    assert _looks_remote(Listing(company="A", role="Eng", location="Remote"))
    assert _looks_remote(Listing(company="A", role="Eng", location="Anywhere"))
    assert not _looks_remote(Listing(company="A", role="Eng", location="New York, NY"))


def test_passes_location_remote_only() -> None:
    c = Criteria(remote_only=True)
    assert _passes_location(Listing(company="A", role="Eng", location="Remote"), c)
    assert not _passes_location(Listing(company="A", role="Eng", location="Onsite NYC"), c)


def test_passes_location_blocklist() -> None:
    c = Criteria(exclude_locations=["brazil"])
    assert not _passes_location(
        Listing(company="A", role="Eng", location="São Paulo, Brazil"), c
    )
    assert _passes_location(Listing(company="A", role="Eng", location="Remote"), c)


def _lead(company: str, role: str, **kw: str) -> Listing:
    return Listing(company=company, role=role, **kw)


def test_run_poll_stages_matches_with_default_lane() -> None:
    repo = FakeRepo()
    c = Criteria(keywords=["engineer"], default_lane="devrel", min_fit_score=55)
    added = run_poll(
        repo,
        c,
        listings=[_lead("Acme", "Backend Engineer", link="https://acme.co/1")],
    )
    assert len(added) == 1
    fields = repo.added[0]
    assert fields.lane == "devrel"
    assert fields.status == "lead"
    assert fields.company == "Acme"


def test_run_poll_dedupes_same_url() -> None:
    # The same posting seen twice (identical URL) stages exactly once.
    repo = FakeRepo()
    c = Criteria(keywords=["engineer"])
    added = run_poll(
        repo,
        c,
        listings=[
            _lead("Acme", "Backend Engineer", link="https://acme.co/1"),
            _lead("Acme", "Backend Engineer", link="https://acme.co/1"),
        ],
    )
    assert len(added) == 1


def test_run_poll_keeps_distinct_urls_for_role_variants() -> None:
    # Per-city variants of one role have distinct URLs -> distinct leads. The old
    # behavior stripped the parenthetical, collapsed them, and dropped all but one.
    repo = FakeRepo()
    c = Criteria(keywords=["engineer"])
    added = run_poll(
        repo,
        c,
        listings=[
            _lead("Acme", "Backend Engineer (Campinas)", link="https://acme.co/1"),
            _lead("Acme", "Backend Engineer (São Paulo)", link="https://acme.co/2"),
        ],
    )
    assert len(added) == 2


def test_score_and_stage_unique_names_for_same_title_distinct_urls() -> None:
    # Same role title (location only in a separate field) -> same slug stem but
    # distinct URLs. Each must get its own row via a -N suffix, not be dropped.
    repo = FakeRepo()
    c = Criteria(keywords=["engineer"])
    added = score_and_stage(
        repo,
        c,
        listings=[
            _lead("Acme", "Backend Engineer", link="https://acme.co/1", location="NYC"),
            _lead("Acme", "Backend Engineer", link="https://acme.co/2", location="SF"),
        ],
    )
    assert len(added) == 2
    assert added == ["acme-backend-engineer.md", "acme-backend-engineer-2.md"]


def test_run_poll_dedupes_urlless_by_slug() -> None:
    # With no URL, the slug is the only key, so identical url-less posts collapse.
    repo = FakeRepo()
    c = Criteria(keywords=["engineer"])
    added = run_poll(
        repo,
        c,
        listings=[
            _lead("Acme", "Backend Engineer", link=""),
            _lead("Acme", "Backend Engineer", link=""),
        ],
    )
    assert len(added) == 1


def test_score_and_stage_skips_database_duplicate_slug() -> None:
    class DuplicateSlugRepo(FakeRepo):
        def add_lead(self, fields: AppFields) -> str:
            raise psycopg.errors.UniqueViolation("duplicate slug")

    repo = DuplicateSlugRepo()
    c = Criteria(keywords=["engineer"])
    added = score_and_stage(
        repo,
        c,
        listings=[_lead("Acme", "Backend Engineer", link="https://acme.co/1")],
    )
    assert added == []
    assert repo.added == []


def test_score_and_stage_propagates_unexpected_stage_failure() -> None:
    class BoomRepo(FakeRepo):
        def add_lead(self, fields: AppFields) -> str:
            raise RuntimeError("db down")

    repo = BoomRepo()
    c = Criteria(keywords=["engineer"])
    with pytest.raises(RuntimeError, match="db down"):
        score_and_stage(
            repo,
            c,
            listings=[_lead("Acme", "Backend Engineer", link="https://acme.co/1")],
        )


def test_run_poll_skips_roles_already_in_db() -> None:
    repo = FakeRepo(existing=[("https://acme.co/1", "Acme", "Backend Engineer")])
    c = Criteria(keywords=["engineer"])
    added = run_poll(
        repo,
        c,
        listings=[_lead("Acme", "Backend Engineer", link="https://acme.co/1")],
    )
    assert added == []
    assert repo.added == []


def test_run_poll_skips_blacklisted_company() -> None:
    repo = FakeRepo(blacklist=["badco"])
    c = Criteria(keywords=["engineer"])
    added = run_poll(
        repo,
        c,
        listings=[_lead("BadCo", "Backend Engineer", link="https://bad.co/1")],
    )
    assert added == []


def test_run_poll_enforces_min_score() -> None:
    repo = FakeRepo()
    c = Criteria(keywords=["engineer"], min_fit_score=90)
    added = run_poll(
        repo,
        c,
        listings=[_lead("Acme", "Backend Engineer", link="https://acme.co/1")],
    )
    assert added == []


def test_run_poll_applies_location_filter() -> None:
    repo = FakeRepo()
    c = Criteria(keywords=["engineer"], remote_only=True)
    onsite = _lead("Acme", "Backend Engineer", link="https://acme.co/1", location="Onsite NYC")
    added = run_poll(repo, c, listings=[onsite])
    assert added == []


def test_gather_logs_and_continues_on_source_failure(
    caplog: pytest.LogCaptureFixture,
) -> None:
    # A failing source is skipped but logged by name; the others still contribute.
    def boom(client: httpx.Client, limit: int) -> list[Listing]:
        raise ValueError("upstream schema changed")

    boom.__name__ = "fetch_boom"

    def ok(client: httpx.Client, limit: int) -> list[Listing]:
        return [_lead("Acme", "Engineer", link="https://acme.co/1")]

    ok.__name__ = "fetch_ok"

    with caplog.at_level(logging.WARNING):
        listings = _gather([boom, ok], 10)

    assert [item.company for item in listings] == ["Acme"]
    assert any("boom" in r.getMessage() for r in caplog.records)


def test_build_fetchers_includes_sources_and_boards() -> None:
    c = Criteria(ats_boards=[AtsBoard(provider="greenhouse", slug="stripe")])
    fetchers = build_fetchers(c)
    assert fetchers[0] is fetch_remotive
    assert fetchers[1] is fetch_remoteok
    assert len(fetchers) == 3  # two default sources + one ATS board


def test_build_fetchers_includes_custom_rss_feeds() -> None:
    c = Criteria(rss_feeds=["https://hooli.example/careers.rss"])
    fetchers = build_fetchers(c)
    assert len(fetchers) == 3  # two default sources + one feed
    # Named after the feed so a failing one is identifiable in the poll logs.
    assert fetchers[-1].__name__ == "rss:https://hooli.example/careers.rss"


def test_fetch_rss_goes_through_the_ssrf_guard_not_the_shared_client(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # The shared client _gather hands every fetcher is NOT SSRF-safe, so a
    # user-supplied feed URL must be fetched via linkcheck.fetch_public instead.
    requested: list[str] = []

    def fake_fetch_public(url: str) -> bytes:
        requested.append(url)
        return (
            b'<?xml version="1.0"?><rss><channel><title>Hooli Jobs</title>'
            b"<item><title>Backend Engineer</title>"
            b"<link>https://hooli.example/j/1</link></item></channel></rss>"
        )

    monkeypatch.setattr("applytrack.poll.fetch_public", fake_fetch_public)

    def explode(*args: object, **kwargs: object) -> None:
        raise AssertionError("the shared client must not be used for a custom feed")

    shared = httpx.Client(transport=httpx.MockTransport(explode))
    out = fetch_rss(shared, 40, "https://hooli.example/careers.rss")

    assert requested == ["https://hooli.example/careers.rss"]
    assert (out[0].company, out[0].role) == ("Hooli", "Backend Engineer")
    assert out[0].source == "rss:hooli.example"


def test_run_poll_dedupes_across_runs_via_seen_ledger() -> None:
    # The seen-key the first run persists blocks a re-ping on the next run, even
    # though FakeRepo's iter_existing still reports no stored application — this is
    # the seen table outliving its lead, not the existing-rows dedup.
    repo = FakeRepo()
    c = Criteria(keywords=["engineer"])
    listing = _lead("Acme", "Backend Engineer", link="https://acme.co/1")
    assert len(run_poll(repo, c, listings=[listing])) == 1
    assert run_poll(repo, c, listings=[listing]) == []
    assert len(repo.added) == 1


def test_run_poll_honors_preloaded_seen_url() -> None:
    # A URL already in the seen table (e.g. from a since-deleted lead) is skipped.
    repo = FakeRepo(seen=[("url", "acme.co/1")])
    c = Criteria(keywords=["engineer"])
    added = run_poll(
        repo, c, listings=[_lead("Acme", "Backend Engineer", link="https://acme.co/1")]
    )
    assert added == []
    assert repo.added == []


def test_run_poll_preloaded_slug_blocks_only_urlless() -> None:
    # The slug is the fallback key: a preloaded slug blocks a url-less listing, but
    # a listing carrying a distinct URL is judged by URL and still staged.
    c = Criteria(keywords=["engineer"])

    urlless = FakeRepo(seen=[("slug", "acme-backend-engineer")])
    assert run_poll(urlless, c, listings=[_lead("Acme", "Backend Engineer", link="")]) == []

    with_url = FakeRepo(seen=[("slug", "acme-backend-engineer")])
    added = run_poll(
        with_url, c, listings=[_lead("Acme", "Backend Engineer", link="https://acme.co/9")]
    )
    assert len(added) == 1


def test_run_all_tenants_routes_sources_per_profile() -> None:
    # Two tenants enable different sources; each must only ever see roles from the
    # sources it turned on, from one shared per-source gather.
    repo_a = FakeRepo(
        profile=Criteria(keywords=["engineer"], sources={"remotive": True, "remoteok": False})
    )
    repo_b = FakeRepo(
        profile=Criteria(keywords=["engineer"], sources={"remotive": False, "remoteok": True})
    )
    repos = {1: repo_a, 2: repo_b}
    gathered = {
        "remotive": [_lead("Acme", "Backend Engineer", link="https://acme.co/1")],
        "remoteok": [_lead("Globex", "Platform Engineer", link="https://globex.co/2")],
    }
    results = run_all_tenants(
        tenant_ids=[1, 2],
        repo_for=lambda tid: repos[tid],
        gathered=gathered,
        verify_links=False,
    )
    assert [f.company for f in repo_a.added] == ["Acme"]
    assert [f.company for f in repo_b.added] == ["Globex"]
    assert {tid: len(names) for tid, names in results.items()} == {1: 1, 2: 1}


def test_run_all_tenants_routes_custom_feeds_per_profile() -> None:
    # Custom feeds route by "rss:{url}" like a source id, so a feed only reaches the
    # tenants that follow it — and one shared fetch serves all of them.
    shared_feed = "https://board.example/feed.rss"
    repo_a = FakeRepo(
        profile=Criteria(
            keywords=["engineer"],
            sources={"remotive": False, "remoteok": False},
            rss_feeds=[shared_feed, "https://hooli.example/careers.rss"],
        )
    )
    repo_b = FakeRepo(
        profile=Criteria(
            keywords=["engineer"],
            sources={"remotive": False, "remoteok": False},
            rss_feeds=[shared_feed],
        )
    )
    repos = {1: repo_a, 2: repo_b}
    gathered = {
        f"rss:{shared_feed}": [_lead("Acme", "Backend Engineer", link="https://acme.co/1")],
        "rss:https://hooli.example/careers.rss": [
            _lead("Hooli", "Platform Engineer", link="https://hooli.example/2")
        ],
    }
    run_all_tenants(
        tenant_ids=[1, 2],
        repo_for=lambda tid: repos[tid],
        gathered=gathered,
        verify_links=False,
    )
    assert sorted(f.company for f in repo_a.added) == ["Acme", "Hooli"]
    assert [f.company for f in repo_b.added] == ["Acme"]


def test_gather_by_source_fetches_a_shared_feed_once(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    calls: list[str] = []

    def fake_fetch_public(url: str) -> bytes:
        calls.append(url)
        return (
            b'<?xml version="1.0"?><rss><channel><title>Board</title>'
            b"<item><title>Acme: Backend Engineer</title>"
            b"<link>https://acme.co/1</link></item></channel></rss>"
        )

    monkeypatch.setattr("applytrack.poll.fetch_public", fake_fetch_public)
    feed = "https://board.example/feed.rss"
    off = {"remotive": False, "remoteok": False}
    gathered = _gather_by_source(
        [Criteria(sources=off, rss_feeds=[feed]), Criteria(sources=off, rss_feeds=[feed])],
        40,
    )

    assert calls == [feed]  # two tenants, one request
    assert [item.company for item in gathered[f"rss:{feed}"]] == ["Acme"]


def test_gather_by_source_isolates_a_failing_feed(
    monkeypatch: pytest.MonkeyPatch, caplog: pytest.LogCaptureFixture
) -> None:
    def boom(url: str) -> bytes:
        raise PublicFetchError("refused non-public or unpinned address")

    monkeypatch.setattr("applytrack.poll.fetch_public", boom)
    off = {"remotive": False, "remoteok": False}
    with caplog.at_level(logging.WARNING):
        gathered = _gather_by_source(
            [Criteria(sources=off, rss_feeds=["https://evil.example/feed"])], 40
        )

    assert gathered["rss:https://evil.example/feed"] == []
    assert any("evil.example" in r.getMessage() for r in caplog.records)


def test_run_all_tenants_isolates_per_tenant_failure() -> None:
    # A tenant whose repo raises is recorded empty; the others are still polled.
    class BoomRepo(FakeRepo):
        def load_seen(self) -> tuple[set[str], set[str]]:
            raise RuntimeError("db down for this tenant")

    bad = BoomRepo(profile=Criteria(keywords=["engineer"], sources={"remotive": True}))
    good = FakeRepo(profile=Criteria(keywords=["engineer"], sources={"remotive": True}))
    repos: dict[int, FakeRepo] = {1: bad, 2: good}
    gathered = {"remotive": [_lead("Acme", "Backend Engineer", link="https://acme.co/1")]}
    results = run_all_tenants(
        tenant_ids=[1, 2],
        repo_for=lambda tid: repos[tid],
        gathered=gathered,
        verify_links=False,
    )
    assert results[1] == []
    assert len(results[2]) == 1
    assert [f.company for f in good.added] == ["Acme"]


def test_drain_requests_empty_queue_is_noop() -> None:
    # An empty poll queue must short-circuit before any gather/network is attempted.
    assert drain_requests(FakeConnection([])) == {}


def test_drain_requests_claims_queued_tenants_once(monkeypatch: pytest.MonkeyPatch) -> None:
    calls: dict[str, object] = {}

    def fake_run_all_tenants(
        conn: FakeConnection,
        *,
        limit_per_source: int,
        tenant_ids: list[int],
        repo_for: object,
        locked_tenant_ids: set[int],
    ) -> dict[int, list[str]]:
        calls["limit"] = limit_per_source
        calls["tenant_ids"] = tenant_ids
        calls["repo_for"] = repo_for
        calls["locked_tenant_ids"] = locked_tenant_ids
        return {1: ["acme.md"], 2: ["globex.md"]}

    monkeypatch.setattr("applytrack.worker.run_all_tenants", fake_run_all_tenants)

    result = drain_requests(FakeConnection([(1,), (1,), (2,)]), limit_per_source=7)

    assert result == {1: ["acme.md"], 2: ["globex.md"]}
    assert calls == {
        "limit": 7,
        "tenant_ids": [1, 2],
        "repo_for": None,
        "locked_tenant_ids": set(),
    }


def test_run_all_tenants_skips_locked_tenant_and_releases_acquired_lock(
    caplog: pytest.LogCaptureFixture,
) -> None:
    class LockCursor:
        def __init__(self, connection: LockConnection) -> None:
            self.connection = connection
            self.row: tuple[bool] | None = None

        def __enter__(self) -> LockCursor:
            return self

        def __exit__(self, exc_type: object, exc: object, traceback: object) -> None:
            return None

        def execute(self, sql: str, params: tuple[str]) -> None:
            tenant_id = int(params[0].rsplit(":", 1)[1])
            if "pg_try_advisory_lock" in sql:
                self.row = (tenant_id not in self.connection.locked,)
            else:
                self.connection.released.append(tenant_id)

        def fetchone(self) -> tuple[bool] | None:
            return self.row

    class LockConnection:
        def __init__(self) -> None:
            self.locked = {1}
            self.released: list[int] = []

        def cursor(self) -> LockCursor:
            return LockCursor(self)

    connection = LockConnection()
    repos = {
        1: FakeRepo(profile=Criteria(keywords=["engineer"])),
        2: FakeRepo(profile=Criteria(keywords=["engineer"])),
    }
    locked_tenant_ids: set[int] = set()
    with caplog.at_level(logging.INFO):
        results = run_all_tenants(
            connection,  # type: ignore[arg-type]
            tenant_ids=[1, 2],
            repo_for=lambda tid: repos[tid],
            gathered={},
            verify_links=False,
            locked_tenant_ids=locked_tenant_ids,
        )

    assert results == {1: [], 2: []}
    assert locked_tenant_ids == {1}
    assert connection.released == [2]
    assert any("another poll is already active" in r.getMessage() for r in caplog.records)


def test_drain_requests_requeues_tenant_with_active_poll(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    class QueueCursor:
        def __init__(self, connection: QueueConnection) -> None:
            self.connection = connection

        def __enter__(self) -> QueueCursor:
            return self

        def __exit__(self, exc_type: object, exc: object, traceback: object) -> None:
            return None

        def execute(self, sql: str, params: tuple[int] | None = None) -> None:
            if sql.startswith("INSERT"):
                assert params is not None
                self.connection.requeued.append(params[0])

        def fetchall(self) -> list[tuple[int]]:
            return [(7,)]

    class QueueConnection:
        def __init__(self) -> None:
            self.requeued: list[int] = []

        def cursor(self) -> QueueCursor:
            return QueueCursor(self)

    def fake_run_all_tenants(
        conn: QueueConnection,
        *,
        limit_per_source: int,
        tenant_ids: list[int],
        repo_for: object,
        locked_tenant_ids: set[int],
    ) -> dict[int, list[str]]:
        locked_tenant_ids.add(tenant_ids[0])
        return {tenant_ids[0]: []}

    monkeypatch.setattr("applytrack.worker.run_all_tenants", fake_run_all_tenants)
    connection = QueueConnection()

    assert drain_requests(connection) == {7: []}  # type: ignore[arg-type]
    assert connection.requeued == [7]
