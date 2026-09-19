# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Handshake as a source (#267): the signed-in GraphQL search, the employer's URL behind
an externally-applied posting, a Handshake-hosted posting staged apply-by-hand, the
ledger consulted before staging, the keyword cap that bounds the request budget, and the
failure shapes a real GraphQL endpoint returns — all offline.

The payload shapes are the ones the live endpoint answers with; the field names, the
``applyType`` vocabulary, and the 401 a stale cookie gets were read off a real signed-in
session.
"""

from __future__ import annotations

import json
import logging

import httpx
import pytest

from applytrack import handshake
from applytrack.criteria import Criteria
from applytrack.handshake import Client, HandshakeAccount
from applytrack.poll import is_aggregator_link

SESSION = json.dumps({handshake.SESSION_COOKIE: "hs-session-token"})

EXTERNAL_JOB = {
    "id": "11462823",
    "title": "Software Engineer",
    "description": "<p>Build the thing.</p>",
    "remote": False,
    "locations": [{"displayName": "Pittsburgh, PA"}],
    # Minor units, as the API reports them: 8000000 is $80,000.00.
    "salaryRange": {
        "min": 8000000,
        "max": 10000000,
        "currency": "USD",
        "paySchedule": {"friendlyName": "Per year"},
    },
    "employer": {"name": "Acme"},
    "jobApplySetting": {
        "applyType": "EXTERNAL",
        "externalApplyType": "URL",
        "externalUrl": "https://acme.bamboohr.com/careers/235",
        "alternativeExternalUrl": None,
    },
}

HOSTED_JOB = {
    "id": "11462899",
    "title": "Software Engineer Intern",
    "description": "<p>Intern here.</p>",
    "remote": True,
    "locations": [{"displayName": "Chicago, IL"}],
    "salaryRange": {"min": None, "max": None, "currency": None, "paySchedule": None},
    "employer": {"name": "Globex"},
    "jobApplySetting": {
        "applyType": "HANDSHAKE",
        "externalApplyType": None,
        "externalUrl": None,
        "alternativeExternalUrl": None,
    },
}


def _search(
    jobs: list[dict], *, cursor: str = "Mg==", more: bool = False, total: int | None = None
) -> dict:
    return {
        "data": {
            "jobSearch": {
                "totalCount": len(jobs) if total is None else total,
                "pageInfo": {"hasNextPage": more, "endCursor": cursor},
                "edges": [
                    {"node": {"id": f"job_search_result_{j['id']}|1", "job": j}} for j in jobs
                ],
            }
        }
    }


SEARCH = _search([EXTERNAL_JOB, HOSTED_JOB])


def _handler(
    *,
    cookie_ok: str = "hs-session-token",
    search: dict | None = None,
    rate_limited: bool = False,
    unauthorized: bool = False,
    server_error: bool = False,
):
    """A fake app.joinhandshake.com: the one GraphQL endpoint behind the session cookie."""
    log: list[str] = []

    def handler(req: httpx.Request) -> httpx.Response:
        log.append(f"{req.method} {req.url.path}")
        if req.url.path != "/hs/graphql":
            return httpx.Response(404)
        if unauthorized:
            # What a stale cookie actually gets: a clean 401 with an empty body.
            return httpx.Response(401, json={})
        if f"{handshake.SESSION_COOKIE}={cookie_ok}" not in req.headers.get("cookie", ""):
            return httpx.Response(302, headers={"location": "https://app.joinhandshake.com/access"})
        if rate_limited:
            return httpx.Response(429)
        if server_error:
            return httpx.Response(503)
        return httpx.Response(200, json=search if search is not None else SEARCH)

    return handler, log


def _client(handler, account: HandshakeAccount | None) -> Client:
    http = httpx.Client(transport=httpx.MockTransport(handler))
    return Client(http, account, pace=lambda _s: None)


def _account() -> HandshakeAccount:
    return HandshakeAccount(username="ada@eastern.edu", session=SESSION)


# -- the session ------------------------------------------------------------


def test_the_session_is_one_cookie_and_a_blank_one_is_no_session() -> None:
    assert handshake.parse_session(SESSION) == "hs-session-token"
    assert handshake.parse_session("") == ""
    assert handshake.parse_session("not json") == ""
    assert handshake.parse_session(json.dumps({"other": "x"})) == ""
    assert handshake.parse_session(json.dumps([1, 2])) == ""
    assert _account().session_fresh
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


def test_a_graphql_error_payload_is_an_empty_page_not_a_crash() -> None:
    # The shape a real GraphQL failure returns: `data` is present and NULL, which is not
    # the same as absent — `.get("data", {})` returns None and the next .get raises.
    erroring = {
        "data": None,
        "errors": [{"message": "Cannot query field \"onSite\" on type \"Job\"."}],
    }
    assert handshake.jobs_from_search(erroring) == ([], "", False)
    assert handshake.total_count(erroring) == 0
    assert handshake.employer_url({"jobApplySetting": None}) == ""
    assert handshake.jobs_from_search({}) == ([], "", False)
    assert handshake.jobs_from_search({"data": {"jobSearch": None}}) == ([], "", False)
    assert handshake.jobs_from_search({"data": {}}) == ([], "", False)


def test_the_total_count_is_readable_as_the_schema_tripwire() -> None:
    assert handshake.total_count(SEARCH) == 2
    assert handshake.total_count(_search([], total=72562)) == 72562
    assert handshake.total_count({"data": {"jobSearch": {"totalCount": "nonsense"}}}) == 0


def test_the_employers_url_is_the_evidence_not_the_apply_type() -> None:
    assert handshake.employer_url(EXTERNAL_JOB) == "https://acme.bamboohr.com/careers/235"
    # A posting Handshake hosts itself has no employer URL.
    assert handshake.employer_url(HOSTED_JOB) == ""
    # An applyType this code has never seen still stages the employer's posting: the
    # whitelist would have silently discarded it.
    unseen = {
        "jobApplySetting": {
            "applyType": "SOMETHING_NEW",
            "externalUrl": "https://example.com/jobs/9",
        }
    }
    assert handshake.employer_url(unseen) == "https://example.com/jobs/9"
    # The alternative URL is the fallback.
    alt = {
        "jobApplySetting": {
            "applyType": "EXTERNAL",
            "alternativeExternalUrl": "https://x.example/1",
        }
    }
    assert handshake.employer_url(alt) == "https://x.example/1"
    # A non-http scheme is never the employer's posting.
    bad = {
        "jobApplySetting": {"applyType": "EXTERNAL", "externalUrl": "mailto:jobs@example.com"}
    }
    assert handshake.employer_url(bad) == ""


def test_the_apply_type_is_read_for_the_log_line() -> None:
    assert handshake.apply_type(EXTERNAL_JOB) == "EXTERNAL"
    assert handshake.apply_type(HOSTED_JOB) == "HANDSHAKE"
    assert handshake.apply_type({}) == ""


def test_a_listing_carries_company_role_location_pay_and_description() -> None:
    listing = handshake.listing_from_job(EXTERNAL_JOB)
    assert listing.company == "Acme"
    assert listing.role == "Software Engineer"
    assert listing.link == "https://app.joinhandshake.com/jobs/11462823"
    assert listing.location == "Pittsburgh, PA"
    assert listing.salary == "USD 80,000–100,000 per year"
    assert listing.description == "<p>Build the thing.</p>"
    assert listing.apply_link == "https://acme.bamboohr.com/careers/235"
    assert listing.source == "handshake"


def test_a_handshake_hosted_posting_is_staged_for_hand_apply() -> None:
    listing = handshake.listing_from_job(HOSTED_JOB)
    assert listing.link == "https://app.joinhandshake.com/jobs/11462899"
    # No employer URL: the poller's apply-by-hand lane, not a discarded lead.
    assert listing.apply_link == ""


def test_a_remote_posting_keeps_its_anchor_city() -> None:
    assert handshake.location_text(HOSTED_JOB) == "Remote · Chicago, IL"
    assert handshake.location_text({"remote": True, "locations": []}) == "Remote"
    assert handshake.location_text({"remote": False, "locations": []}) == ""


def test_several_locations_are_joined_and_capped() -> None:
    job = {**EXTERNAL_JOB, "locations": [{"displayName": f"City {i}"} for i in range(5)]}
    assert handshake.location_text(job) == "City 0 · City 1 · City 2"


def test_a_single_flat_salary_prints_once() -> None:
    job = {
        **EXTERNAL_JOB,
        "salaryRange": {
            "min": 2500,
            "max": 2500,
            "currency": "USD",
            "paySchedule": {"friendlyName": "Per hour"},
        },
    }
    assert handshake.salary_text(job) == "USD 25 per hour"


def test_pay_is_read_as_minor_units_not_dollars() -> None:
    # A real security-officer posting reports 1751 with "Per hour" — $17.51, not $1,751.
    job = {
        **EXTERNAL_JOB,
        "salaryRange": {
            "min": 1751,
            "max": 1751,
            "currency": "USD",
            "paySchedule": {"friendlyName": "Hourly Wage"},
        },
    }
    assert handshake.salary_text(job) == "USD 17.51 hourly wage"
    # And a real service-manager posting reports 9000000–12500000 — $90,000–$125,000.
    job = {
        **EXTERNAL_JOB,
        "salaryRange": {
            "min": 9000000,
            "max": 12500000,
            "currency": "USD",
            "paySchedule": {"friendlyName": "Annual Salary"},
        },
    }
    assert handshake.salary_text(job) == "USD 90,000–125,000 annual salary"


# -- the request budget -----------------------------------------------------


def test_the_keyword_searches_are_capped_so_the_budget_is_bounded() -> None:
    keywords = [f"keyword {i}" for i in range(30)]
    queries = handshake.queries_for(keywords)
    assert len(queries) == handshake.MAX_KEYWORD_QUERIES
    assert queries[0] == "keyword 0"
    # De-duped, case-insensitively, and blanks dropped.
    assert handshake.queries_for([".NET", ".net", "  ", "ai"]) == [".NET", "ai"]


def test_a_tenant_with_many_keywords_cannot_walk_the_board() -> None:
    handler, log = _handler()
    listings = handshake.fetch_listings(
        _client(handler, _account()), [f"keyword {i}" for i in range(30)], limit=10
    )
    assert len(listings) <= 20
    # At most one page per capped keyword — never 30 keywords' worth of searching.
    assert len(log) <= handshake.MAX_KEYWORD_QUERIES


# -- the gather -------------------------------------------------------------


def test_the_gather_stages_the_employer_link_and_the_handshake_hosted_posting() -> None:
    handler, log = _handler()
    listings = handshake.fetch_listings(
        _client(handler, _account()), ["software engineer"], limit=10
    )
    by_role = {listing.role: listing for listing in listings}
    assert set(by_role) == {"Software Engineer", "Software Engineer Intern"}
    assert by_role["Software Engineer"].apply_link == "https://acme.bamboohr.com/careers/235"
    assert by_role["Software Engineer Intern"].apply_link == ""
    # One search, and no second call per posting.
    assert log.count("POST /hs/graphql") == 1


def test_the_description_is_matched_too_not_only_the_title() -> None:
    # The server matched the keyword and the description came free in the same bytes; a
    # posting whose title does not carry the word must not be dropped.
    job = {
        **EXTERNAL_JOB,
        "id": "1",
        "title": "Technical Intern",
        "description": "Work on .NET services",
    }
    handler, _ = _handler(search=_search([job]))
    listings = handshake.fetch_listings(_client(handler, _account()), [".NET"], limit=10)
    assert [listing.role for listing in listings] == ["Technical Intern"]


def test_a_posting_that_names_no_keyword_is_dropped() -> None:
    handler, _ = _handler()
    listings = handshake.fetch_listings(
        _client(handler, _account()), ["registered nurse"], limit=10
    )
    assert listings == []


def test_a_posting_the_ledger_knows_is_not_staged() -> None:
    handler, _ = _handler()
    listings = handshake.fetch_listings(
        _client(handler, _account()),
        ["software engineer"],
        limit=10,
        already_seen=lambda _url: True,
    )
    assert listings == []


# -- the failure shapes -----------------------------------------------------


def test_a_stale_cookie_is_a_sign_in_not_a_silent_zero() -> None:
    handler, _ = _handler(unauthorized=True)
    with pytest.raises(handshake.NeedsSignIn):
        handshake.fetch_listings(_client(handler, _account()), ["software engineer"], limit=10)


def test_a_graphql_error_is_loud_rather_than_an_empty_board() -> None:
    # A 200 carrying `errors` is a schema break, not "no jobs": it must not look like one.
    payload = {
        "data": None,
        "errors": [{"message": "Field 'jobSearch' doesn't exist on type 'Query'."}],
    }
    handler, _ = _handler(search=payload)
    with pytest.raises(handshake.SchemaError) as caught:
        handshake.fetch_listings(_client(handler, _account()), ["software engineer"], limit=10)
    assert "JobSearchQuery" in str(caught.value)


def test_an_auth_shaped_graphql_error_asks_for_a_sign_in_instead() -> None:
    payload = {"data": None, "errors": [{"message": "You are not signed in."}]}
    handler, _ = _handler(search=payload)
    with pytest.raises(handshake.NeedsSignIn):
        handshake.fetch_listings(_client(handler, _account()), ["software engineer"], limit=10)


def test_a_matched_count_with_nothing_parsed_is_warned_about(caplog) -> None:
    # The tripwire for a renamed nested field: the search says it matched, the parse
    # finds nothing. That must not pass as an empty board.
    handler, _ = _handler(search=_search([], total=72562))
    with caplog.at_level(logging.WARNING):
        listings = handshake.fetch_listings(
            _client(handler, _account()), ["software engineer"], limit=10
        )
    assert listings == []
    assert any("none parsed" in record.getMessage() for record in caplog.records)


def test_a_429_ends_the_poll_with_what_was_gathered() -> None:
    handler, _ = _handler(rate_limited=True)
    client = _client(handler, _account())
    assert handshake.fetch_listings(client, ["software engineer"], limit=10) == []


def test_a_server_error_does_not_throw_away_what_was_gathered() -> None:
    # A 503 must behave like a 429: log it and keep the leads already in hand.
    handler, _ = _handler(server_error=True)
    client = _client(handler, _account())
    assert handshake.fetch_listings(client, ["software engineer"], limit=10) == []


def test_no_session_means_not_signed_in_and_no_cookie_is_sent() -> None:
    handler, log = _handler(cookie_ok="")
    client = _client(handler, None)
    assert client.signed_in is False
    with pytest.raises(handshake.NeedsSignIn):
        handshake.fetch_listings(client, ["software engineer"], limit=10)
    assert log == ["POST /hs/graphql"]


def test_the_session_travels_as_a_header_not_in_a_shared_jar() -> None:
    # The cookie is a property of this tenant's call: a client the poller reuses must not
    # end up carrying one tenant's session into another's request.
    handler, _ = _handler()
    http = httpx.Client(transport=httpx.MockTransport(handler))
    Client(http, _account(), pace=lambda _s: None).search("software engineer")
    assert not http.cookies


def test_a_handshake_posting_link_is_an_aggregator_link() -> None:
    # The #191 rule resolves it (or marks it apply-by-hand) rather than treating the
    # Handshake page as the employer's form.
    assert is_aggregator_link("https://app.joinhandshake.com/jobs/11462823")
    assert is_aggregator_link("https://joinhandshake.com/find-jobs/remote/")


def test_the_source_is_off_by_default_and_selectable() -> None:
    criteria = Criteria()
    assert "handshake" in criteria.sources
    assert criteria.sources["handshake"] is False
