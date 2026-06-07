# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Tests for config-driven scoring, filtering, and the run_poll orchestration."""

from __future__ import annotations

from applytrack.criteria import AtsBoard, Criteria
from applytrack.poll import (
    Listing,
    _looks_remote,
    _passes_location,
    build_fetchers,
    classify,
    fetch_remoteok,
    fetch_remotive,
    run_poll,
)
from applytrack.store import AppFields, filename_for


class FakeRepo:
    """In-memory :class:`applytrack.poll.LeadRepo` for offline ``run_poll`` tests.

    Stands in for :class:`applytrack.db.PollRepo` with no Postgres: it records
    staged leads and mimics the unique-slug constraint the real table enforces.
    """

    def __init__(
        self,
        *,
        existing: list[tuple[str, str, str]] | None = None,
        blacklist: list[str] | None = None,
    ) -> None:
        self._existing = list(existing or [])
        self._blacklist = list(blacklist or [])
        self.added: list[AppFields] = []

    def iter_existing(self) -> list[tuple[str, str, str]]:
        return list(self._existing)

    def blacklist_companies(self) -> list[str]:
        return list(self._blacklist)

    def add_lead(self, fields: AppFields) -> str:
        name = filename_for(fields)
        if any(filename_for(f) == name for f in self.added):
            raise ValueError(f"an application named {name!r} already exists")
        self.added.append(fields)
        return name


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


def test_run_poll_dedupes_same_role() -> None:
    repo = FakeRepo()
    c = Criteria(keywords=["engineer"])
    added = run_poll(
        repo,
        c,
        listings=[
            _lead("Acme", "Backend Engineer", link="https://acme.co/1"),
            _lead("Acme", "Backend Engineer (Remote)", link="https://acme.co/2"),
        ],
    )
    assert len(added) == 1


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


def test_build_fetchers_includes_sources_and_boards() -> None:
    c = Criteria(ats_boards=[AtsBoard(provider="greenhouse", slug="stripe")])
    fetchers = build_fetchers(c)
    assert fetchers[0] is fetch_remotive
    assert fetchers[1] is fetch_remoteok
    assert len(fetchers) == 3  # two default sources + one ATS board
