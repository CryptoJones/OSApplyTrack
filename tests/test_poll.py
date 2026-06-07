# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Tests for config-driven scoring, filtering, and the run_poll orchestration."""

from __future__ import annotations

import json
from pathlib import Path

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
from applytrack.store import AppStore


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


def test_run_poll_stages_matches_with_default_lane(tmp_path: Path) -> None:
    c = Criteria(keywords=["engineer"], default_lane="devrel", min_fit_score=55)
    added = run_poll(
        tmp_path,
        criteria=c,
        listings=[_lead("Acme", "Backend Engineer", link="https://acme.co/1")],
    )
    assert len(added) == 1
    fields = AppStore(tmp_path).read_fields(added[0])
    assert fields.lane == "devrel"
    assert fields.status == "lead"
    assert fields.company == "Acme"


def test_run_poll_dedupes_same_role(tmp_path: Path) -> None:
    c = Criteria(keywords=["engineer"])
    added = run_poll(
        tmp_path,
        criteria=c,
        listings=[
            _lead("Acme", "Backend Engineer", link="https://acme.co/1"),
            _lead("Acme", "Backend Engineer (Remote)", link="https://acme.co/2"),
        ],
    )
    assert len(added) == 1


def test_run_poll_skips_blacklisted_company(tmp_path: Path) -> None:
    (tmp_path / ".blacklist.json").write_text(
        json.dumps({"companies": ["BadCo"]}), encoding="utf-8"
    )
    c = Criteria(keywords=["engineer"])
    added = run_poll(
        tmp_path,
        criteria=c,
        listings=[_lead("BadCo", "Backend Engineer", link="https://bad.co/1")],
    )
    assert added == []


def test_run_poll_enforces_min_score(tmp_path: Path) -> None:
    c = Criteria(keywords=["engineer"], min_fit_score=90)
    added = run_poll(
        tmp_path,
        criteria=c,
        listings=[_lead("Acme", "Backend Engineer", link="https://acme.co/1")],
    )
    assert added == []


def test_run_poll_applies_location_filter(tmp_path: Path) -> None:
    c = Criteria(keywords=["engineer"], remote_only=True)
    onsite = _lead("Acme", "Backend Engineer", link="https://acme.co/1", location="Onsite NYC")
    added = run_poll(tmp_path, criteria=c, listings=[onsite])
    assert added == []


def test_build_fetchers_includes_sources_and_boards() -> None:
    c = Criteria(ats_boards=[AtsBoard(provider="greenhouse", slug="stripe")])
    fetchers = build_fetchers(c)
    assert fetchers[0] is fetch_remotive
    assert fetchers[1] is fetch_remoteok
    assert len(fetchers) == 3  # two default sources + one ATS board
