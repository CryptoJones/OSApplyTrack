# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Offline parsing tests for every source fetcher, using httpx.MockTransport.

Each test stubs the HTTP layer with canned payloads (the real wire formats) and
asserts the fetcher maps them onto Listing objects correctly — no network.
"""

from __future__ import annotations

from collections.abc import Callable

import httpx

from applytrack.poll import (
    fetch_arbeitnow,
    fetch_greenhouse,
    fetch_hn_whoishiring,
    fetch_jobicy,
    fetch_lever,
    fetch_remoteok,
    fetch_remotive,
    fetch_weworkremotely,
)

Handler = Callable[[httpx.Request], httpx.Response]


def _client(handler: Handler) -> httpx.Client:
    return httpx.Client(transport=httpx.MockTransport(handler))


def test_fetch_remoteok_skips_legal_notice_and_parses() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json=[
                {"legal": "notice, not a job"},
                {
                    "company": "Acme",
                    "position": "Backend Engineer",
                    "url": "https://remoteok.com/jobs/1",
                    "location": "Remote",
                    "salary_min": 100000,
                    "salary_max": 150000,
                    "description": "<p>Build things</p>",
                },
            ],
        )

    out = fetch_remoteok(_client(handler), 40)
    assert len(out) == 1
    job = out[0]
    assert job.company == "Acme"
    assert job.role == "Backend Engineer"
    assert job.salary == "$100,000–$150,000"
    assert job.description == "Build things"
    assert job.source == "remoteok"


def test_fetch_arbeitnow_parses() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "data": [
                    {
                        "company_name": "Globex",
                        "title": "AI Engineer",
                        "url": "https://arbeitnow.com/jobs/1",
                        "location": "",
                        "remote": True,
                        "tags": ["python"],
                        "job_types": ["full-time"],
                        "description": "<p>LLMs</p>",
                    }
                ]
            },
        )

    out = fetch_arbeitnow(_client(handler), 40)
    assert len(out) == 1
    assert out[0].company == "Globex"
    assert out[0].location == "Remote"  # blank location + remote flag
    assert "python" in out[0].description and "LLMs" in out[0].description


def test_fetch_jobicy_parses() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "jobs": [
                    {
                        "companyName": "Initech",
                        "jobTitle": "Developer Advocate",
                        "url": "https://jobicy.com/jobs/1",
                        "jobGeo": "Anywhere",
                        "annualSalaryMin": "120000",
                        "jobExcerpt": "<p>DevRel</p>",
                    }
                ]
            },
        )

    out = fetch_jobicy(_client(handler), 40)
    assert len(out) == 1
    assert out[0].company == "Initech"
    assert out[0].role == "Developer Advocate"
    assert out[0].salary == "120000"
    assert out[0].description == "DevRel"


def test_fetch_weworkremotely_parses_rss_title() -> None:
    rss = b"""<?xml version="1.0"?>
    <rss><channel>
      <item>
        <title>Hooli: Senior Platform Engineer</title>
        <link>https://weworkremotely.com/jobs/1</link>
        <region>Remote</region>
        <description>&lt;p&gt;Scale it&lt;/p&gt;</description>
      </item>
    </channel></rss>"""

    def handler(request: httpx.Request) -> httpx.Response:
        # Only the first feed needs data; second returns empty channel.
        if "programming" in request.url.path:
            return httpx.Response(200, content=rss)
        return httpx.Response(200, content=b"<rss><channel></channel></rss>")

    out = fetch_weworkremotely(_client(handler), 40)
    assert len(out) == 1
    assert out[0].company == "Hooli"
    assert out[0].role == "Senior Platform Engineer"
    assert out[0].location == "Remote"
    assert out[0].description == "Scale it"


def test_fetch_greenhouse_parses() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "jobs": [
                    {
                        "title": "Staff Engineer",
                        "absolute_url": "https://boards.greenhouse.io/stripe/jobs/1",
                        "location": {"name": "Remote - US"},
                        "content": "<p>Payments</p>",
                    }
                ]
            },
        )

    out = fetch_greenhouse(_client(handler), 40, "stripe")
    assert len(out) == 1
    assert out[0].company == "Stripe"  # _ats_label(slug)
    assert out[0].location == "Remote - US"
    assert out[0].source == "greenhouse:stripe"


def test_fetch_lever_parses() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json=[
                {
                    "text": "Senior Frontend Engineer",
                    "hostedUrl": "https://jobs.lever.co/netflix/1",
                    "categories": {"location": "Remote", "team": "Streaming"},
                    "descriptionPlain": "Build UIs",
                }
            ],
        )

    out = fetch_lever(_client(handler), 40, "netflix")
    assert len(out) == 1
    assert out[0].company == "Netflix"
    assert out[0].location == "Remote"
    assert out[0].source == "lever:netflix"
    assert out[0].description.startswith("Streaming")


def test_fetch_hn_whoishiring_parses_pipe_header() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("/search"):
            return httpx.Response(
                200,
                json={
                    "hits": [
                        {
                            "title": "Ask HN: Who is hiring? (June 2026)",
                            "created_at_i": 1000,
                            "objectID": "42",
                        }
                    ]
                },
            )
        return httpx.Response(
            200,
            json={
                "children": [
                    {
                        "text": 'Acme | Backend Engineer | Remote | '
                        '<a href="https://acme.co/jobs">apply</a>'
                    },
                    {"text": "just some prose with no pipe delimiters"},
                ]
            },
        )

    out = fetch_hn_whoishiring(_client(handler), 40)
    assert len(out) == 1
    assert out[0].company == "Acme"
    assert out[0].role == "Backend Engineer"
    assert out[0].location == "Remote"
    assert out[0].link == "https://acme.co/jobs"


def test_fetch_remotive_scrapes_blob_and_dedupes() -> None:
    blob = (
        "<html><script>window.__INITIAL_SEARCH_RESULTS__ = "
        '{"results":[{"hits":[{"id":7,"company_name":"Remotive Co",'
        '"title":"ML Engineer","url":"https://remotive.com/jobs/7",'
        '"locations":[{"name":"Worldwide"}],"salary":"$160k",'
        '"skills":["pytorch"],"category":"Data"}]}]};'
        "</script></html>"
    )

    def handler(request: httpx.Request) -> httpx.Response:
        # Same blob on every category page — fetcher must dedupe by id to one.
        return httpx.Response(200, text=blob)

    out = fetch_remotive(_client(handler), 40)
    assert len(out) == 1
    assert out[0].company == "Remotive Co"
    assert out[0].role == "ML Engineer"
    assert out[0].location == "Worldwide"
    assert "pytorch" in out[0].description
