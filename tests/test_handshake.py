# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Handshake as a source (#267): the signed-in GraphQL search, the employer's URL behind
an externally-applied posting, a Handshake-hosted posting skipped and remembered, the
ledger consulted before a detail call, and the per-tenant gather — all offline.

The payload shapes are the ones the live endpoint answers with; the field names and the
``applyType`` vocabulary were read off a real signed-in session.
"""

from __future__ import annotations

import json

import httpx

from applytrack import handshake
from applytrack.criteria import Criteria
from applytrack.handshake import Client, HandshakeAccount
from applytrack.poll import is_aggregator_link

SESSION = json.dumps({handshake.SESSION_COOKIE: "hs-session-token"})

EXTERNAL_JOB = {
    "id": "11462823",
    "title": "Software Engineer",
    "description": "<p>Build the thing.</p>",
    "createdAt": "2026-09-18T04:02:24-05:00",
    "expirationDate": "2026-10-18T04:02:24-05:00",
    "remote": False,
    "onSite": True,
    "hybrid": False,
    "locations": [{"displayName": "Pittsburgh, PA"}],
    "employmentType": {"name": "Full-Time"},
    "jobType": {"name": "Job"},
    "salaryRange": {
        "min": 80000,
        "max": 100000,
        "currency": "USD",
        "paySchedule": {"friendlyName": "Per year"},
    },
    "employer": {"name": "Acme", "industry": {"name": "Software"}},
}

HANDSHAKE_JOB = {
    "id": "11462899",
    "title": "Software Engineer Intern",
    "description": "<p>Intern here.</p>",
    "createdAt": "2026-09-17T04:02:24-05:00",
    "expirationDate": "2026-10-17T04:02:24-05:00",
    "remote": True,
    "onSite": False,
    "hybrid": False,
    "locations": [],
    "employmentType": {"name": "Internship"},
    "jobType": {"name": "Internship"},
    "salaryRange": {"min": None, "max": None, "currency": None, "paySchedule": None},
    "employer": {"name": "Globex", "industry": {"name": "Software"}},
}


def _search(jobs: list[dict], *, cursor: str = "Mg==", more: bool = False) -> dict:
    return {
        "data": {
            "jobSearch": {
                "totalCount": len(jobs),
                "pageInfo": {"hasNextPage": more, "endCursor": cursor},
                "edges": [
                    {"node": {"id": f"job_search_result_{j['id']}|1", "job": j}} for j in jobs
                ],
            }
        }
    }


SEARCH = _search([EXTERNAL_JOB, HANDSHAKE_JOB])

EXTERNAL_DETAIL = {
    "data": {
        "job": {
            "id": "11462823",
            "atsProvider": "XML_FEED",
            "jobApplySetting": {
                "applyType": "EXTERNAL",
                "externalApplyType": "URL",
                "externalUrl": "https://acme.bamboohr.com/careers/235",
                "alternativeExternalUrl": None,
            },
        }
    }
}

HANDSHAKE_DETAIL = {
    "data": {
        "job": {
            "id": "11462899",
            "atsProvider": None,
            "jobApplySetting": {
                "applyType": "HANDSHAKE",
                "externalApplyType": None,
                "externalUrl": None,
                "alternativeExternalUrl": None,
            },
        }
    }
}


def _handler(
    *,
    cookie_ok: str = "hs-session-token",
    search: dict | None = None,
    details: dict[str, dict] | None = None,
    rate_limited: bool = False,
):
    """A fake app.joinhandshake.com: the GraphQL endpoint behind the session cookie."""
    log: list[str] = []
    if details is None:
        details = {"11462823": EXTERNAL_DETAIL, "11462899": HANDSHAKE_DETAIL}

    def handler(req: httpx.Request) -> httpx.Response:
        log.append(f"{req.method} {req.url.path}")
        if req.url.path != "/hs/graphql":
            return httpx.Response(404)
        if f"{handshake.SESSION_COOKIE}={cookie_ok}" not in req.headers.get("cookie", ""):
            return httpx.Response(302, headers={"location": "https://app.joinhandshake.com/access"})
        if rate_limited:
            return httpx.Response(429)
        body = json.loads(req.content)
        if body.get("operationName") == "JobSearchQuery":
            return httpx.Response(200, json=search if search is not None else SEARCH)
        if body.get("operationName") == "JobApplySetting":
            jid = body["variables"]["id"]
            return httpx.Response(200, json=details.get(jid, {"data": {"job": None}}))
        return httpx.Response(400)

    return handler, log


def _client(handler, account: HandshakeAccount | None) -> Client:
    http = httpx.Client(transport=httpx.MockTransport(handler))
    return Client(http, account, pace=lambda _s: None)


# -- the session ------------------------------------------------------------


def test_the_session_is_one_cookie_and_a_blank_one_is_no_session() -> None:
    assert handshake.parse_session(SESSION) == "hs-session-token"
    assert handshake.parse_session("") == ""
    assert handshake.parse_session("not json") == ""
    assert handshake.parse_session(json.dumps({"other": "x"})) == ""
    assert HandshakeAccount(username="a", session=SESSION).session_fresh
    assert not HandshakeAccount(username="a").session_fresh


def test_an_expired_session_is_not_fresh() -> None:
    from datetime import datetime, timedelta, timezone

    stale = HandshakeAccount(
        username="a",
        session=SESSION,
        session_expires_at=datetime.now(timezone.utc) - timedelta(days=1),
    )
    assert not stale.session_fresh


# -- reading the payloads ---------------------------------------------------


def test_the_search_payload_yields_its_jobs_and_the_next_cursor() -> None:
    jobs, cursor, more = handshake.jobs_from_search(SEARCH)
    assert [j["id"] for j in jobs] == ["11462823", "11462899"]
    assert cursor == "Mg=="
    assert more is False
    assert handshake.jobs_from_search({}) == ([], "", False)
    assert handshake.jobs_from_search({"data": {"jobSearch": None}}) == ([], "", False)


def test_the_employer_url_is_taken_only_for_an_external_apply() -> None:
    assert handshake.external_url(EXTERNAL_DETAIL) == "https://acme.bamboohr.com/careers/235"
    # A posting Handshake hosts itself has no employer URL to stage.
    assert handshake.external_url(HANDSHAKE_DETAIL) == ""
    assert handshake.external_url({}) == ""


def test_handshake_and_external_is_still_the_employers_posting() -> None:
    payload = {
        "data": {
            "job": {
                "jobApplySetting": {
                    "applyType": "HANDSHAKE_AND_EXTERNAL",
                    "externalUrl": "https://jbenton.bamboohr.com/careers/235",
                }
            }
        }
    }
    assert handshake.external_url(payload) == "https://jbenton.bamboohr.com/careers/235"


def test_the_alternative_url_is_the_fallback() -> None:
    payload = {
        "data": {
            "job": {
                "jobApplySetting": {
                    "applyType": "EXTERNAL",
                    "externalUrl": None,
                    "alternativeExternalUrl": "https://example.com/jobs/1",
                }
            }
        }
    }
    assert handshake.external_url(payload) == "https://example.com/jobs/1"


def test_a_non_http_employer_url_is_refused() -> None:
    payload = {
        "data": {
            "job": {
                "jobApplySetting": {"applyType": "EXTERNAL", "externalUrl": "javascript:alert(1)"}
            }
        }
    }
    assert handshake.external_url(payload) == ""


def test_a_listing_carries_company_role_location_pay_and_description() -> None:
    listing = handshake.listing_from_job(EXTERNAL_JOB, "https://acme.bamboohr.com/careers/235")
    assert listing.company == "Acme"
    assert listing.role == "Software Engineer"
    assert listing.link == "https://app.joinhandshake.com/jobs/11462823"
    assert listing.location == "Pittsburgh, PA"
    assert listing.salary == "USD 80,000–100,000 per year"
    assert listing.description == "<p>Build the thing.</p>"
    assert listing.apply_link == "https://acme.bamboohr.com/careers/235"
    assert listing.source == "handshake"


def test_a_remote_posting_with_no_city_says_remote() -> None:
    listing = handshake.listing_from_job(HANDSHAKE_JOB, "https://x.example/1")
    assert listing.location == "Remote"
    assert listing.salary == ""


def test_several_locations_are_joined_and_capped() -> None:
    job = {**EXTERNAL_JOB, "locations": [{"displayName": f"City {i}"} for i in range(5)]}
    assert handshake.location_text(job) == "City 0 · City 1 · City 2"


def test_a_single_flat_salary_prints_once() -> None:
    job = {
        **EXTERNAL_JOB,
        "salaryRange": {
            "min": 25,
            "max": 25,
            "currency": "USD",
            "paySchedule": {"friendlyName": "Per hour"},
        },
    }
    assert handshake.salary_text(job) == "USD 25 per hour"


# -- the search and the gather ----------------------------------------------


def test_fetch_listings_stages_the_employer_link_and_drops_the_handshake_hosted_one() -> None:
    handler, log = _handler()
    remembered: list[str] = []
    listings = handshake.fetch_listings(
        _client(handler, HandshakeAccount(username="a", session=SESSION)),
        ["software engineer"],
        limit=10,
        remember=remembered.append,
    )
    assert [listing.role for listing in listings] == ["Software Engineer"]
    assert listings[0].apply_link == "https://acme.bamboohr.com/careers/235"
    # The Handshake-hosted posting is remembered, never staged as a dead end.
    assert remembered == ["https://app.joinhandshake.com/jobs/11462899"]
    # One search, then one detail call per posting that named a keyword.
    assert log.count("POST /hs/graphql") == 3


def test_a_title_that_names_no_keyword_costs_no_detail_call() -> None:
    handler, log = _handler()
    listings = handshake.fetch_listings(
        _client(handler, HandshakeAccount(username="a", session=SESSION)),
        ["registered nurse"],
        limit=10,
    )
    assert listings == []
    assert log == ["POST /hs/graphql"]  # the search only


def test_a_posting_the_ledger_knows_costs_no_detail_call() -> None:
    handler, log = _handler()
    listings = handshake.fetch_listings(
        _client(handler, HandshakeAccount(username="a", session=SESSION)),
        ["software engineer"],
        limit=10,
        already_seen=lambda _url: True,
    )
    assert listings == []
    # Every posting was known to the ledger, so only the search was paid for.
    assert log == ["POST /hs/graphql"]


def test_a_bounced_session_asks_for_a_renewal() -> None:
    handler, _ = _handler(cookie_ok="something-else")
    client = _client(handler, HandshakeAccount(username="a", session=SESSION))
    try:
        handshake.fetch_listings(client, ["software engineer"], limit=10)
        raise AssertionError("expected NeedsSignIn")
    except handshake.NeedsSignIn:
        pass


def test_a_429_ends_the_poll_with_what_was_gathered() -> None:
    handler, _ = _handler(rate_limited=True)
    listings = handshake.fetch_listings(
        _client(handler, HandshakeAccount(username="a", session=SESSION)),
        ["software engineer"],
        limit=10,
    )
    assert listings == []


def test_no_session_means_not_signed_in_and_no_cookie_is_sent() -> None:
    handler, log = _handler(cookie_ok="")
    client = _client(handler, None)
    assert client.signed_in is False
    try:
        handshake.fetch_listings(client, ["software engineer"], limit=10)
        raise AssertionError("expected NeedsSignIn")
    except handshake.NeedsSignIn:
        pass
    assert log == ["POST /hs/graphql"]


def test_a_handshake_posting_link_is_an_aggregator_link() -> None:
    # The #191 rule resolves it (or marks it apply-by-hand) rather than treating the
    # Handshake page as the employer's form.
    assert is_aggregator_link("https://app.joinhandshake.com/jobs/11462823")
    assert is_aggregator_link("https://joinhandshake.com/find-jobs/remote/")


def test_the_source_is_off_by_default_and_selectable() -> None:
    criteria = Criteria()
    assert "handshake" in criteria.sources
    assert criteria.sources["handshake"] is False
