# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Tests for the user-editable discovery criteria config."""

from __future__ import annotations

from applytrack.criteria import (
    DEFAULT_KEYWORDS,
    MAX_RSS_FEEDS,
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


def test_to_dict_round_trips_dict() -> None:
    original = Criteria.from_dict(
        {
            "keywords": ["rust", "wasm"],
            "default_lane": "devrel",
            "min_fit_score": 70,
            "remote_only": True,
            "exclude_locations": ["India"],
            "sources": {"jobicy": True},
            "ats_boards": [{"provider": "lever", "slug": "netflix"}],
            "rss_feeds": ["https://hooli.example/careers.rss"],
        }
    )
    assert Criteria.from_dict(original.to_dict()).to_dict() == original.to_dict()


def test_from_dict_keeps_only_http_feed_urls() -> None:
    c = Criteria.from_dict(
        {
            "rss_feeds": [
                "https://hooli.example/jobs.rss",
                "http://boards.example/atom",
                "  https://hooli.example/jobs.rss  ",  # dup after strip
                "file:///etc/passwd",  # non-http scheme dropped
                "javascript:alert(1)",  # ditto
                "/relative/feed.xml",  # no host dropped
                "http://internal.example:8080/feed",  # non-default port dropped
                "",
            ]
        }
    )
    assert c.rss_feeds == ["https://hooli.example/jobs.rss", "http://boards.example/atom"]


def test_from_dict_caps_the_feed_list() -> None:
    many = [f"https://feeds.example/{i}.rss" for i in range(MAX_RSS_FEEDS + 10)]
    assert len(Criteria.from_dict({"rss_feeds": many}).rss_feeds) == MAX_RSS_FEEDS


def test_feeds_default_to_empty() -> None:
    assert Criteria.from_dict({}).rss_feeds == []

# CI smoke test — deliberate ruff F401, reverted immediately.
import json  # noqa-less on purpose
