# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Tests for the user-editable discovery criteria config."""

from __future__ import annotations

from pathlib import Path

from applytrack.criteria import (
    DEFAULT_KEYWORDS,
    Criteria,
)


def test_from_dict_clamps_score() -> None:
    assert Criteria.from_dict({"min_fit_score": 200}).min_fit_score == 100
    assert Criteria.from_dict({"min_fit_score": -5}).min_fit_score == 0
    assert Criteria.from_dict({"min_fit_score": "nope"}).min_fit_score == 55


def test_from_dict_validates_lane() -> None:
    assert Criteria.from_dict({"default_lane": "marketing"}).default_lane == "ai"
    assert Criteria.from_dict({"default_lane": "DOTNET"}).default_lane == "dotnet"


def test_from_dict_filters_unknown_sources() -> None:
    c = Criteria.from_dict({"sources": {"remotive": False, "bogus": True}})
    assert c.sources["remotive"] is False
    assert c.sources["remoteok"] is True  # untouched default
    assert "bogus" not in c.sources


def test_from_dict_dedupes_and_validates_boards() -> None:
    c = Criteria.from_dict(
        {
            "ats_boards": [
                {"provider": "greenhouse", "slug": "stripe"},
                {"provider": "greenhouse", "slug": "Stripe"},  # case-insensitive dup
                {"provider": "bogus", "slug": "x"},  # unknown provider dropped
                {"provider": "lever", "slug": ""},  # empty slug dropped
            ]
        }
    )
    assert [(b.provider, b.slug) for b in c.ats_boards] == [("greenhouse", "stripe")]


def test_from_dict_dedupes_keywords_preserving_order() -> None:
    c = Criteria.from_dict({"keywords": ["a", " a ", "B", "b"]})
    assert c.keywords == ["a", "B"]


def test_empty_keywords_fall_back_to_defaults() -> None:
    assert Criteria.from_dict({"keywords": []}).keywords == list(DEFAULT_KEYWORDS)


def test_save_load_round_trip(tmp_path: Path) -> None:
    path = tmp_path / ".criteria.json"
    original = Criteria.from_dict(
        {
            "keywords": ["rust", "wasm"],
            "default_lane": "devrel",
            "min_fit_score": 70,
            "remote_only": True,
            "exclude_locations": ["India"],
            "sources": {"jobicy": True},
            "ats_boards": [{"provider": "lever", "slug": "netflix"}],
        }
    )
    original.save(path)
    assert Criteria.load(path).to_dict() == original.to_dict()


def test_load_missing_or_corrupt_returns_defaults(tmp_path: Path) -> None:
    assert Criteria.load(tmp_path / "absent.json").to_dict() == Criteria().to_dict()
    bad = tmp_path / "bad.json"
    bad.write_text("{not json", encoding="utf-8")
    assert Criteria.load(bad).to_dict() == Criteria().to_dict()
