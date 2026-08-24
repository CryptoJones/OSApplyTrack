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
    fetch_remotefirstjobs,
    fetch_remoteok,
    fetch_remotive,
    fetch_weworkremotely,
    fetch_workanywhere,
    parse_job_feed,
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
        # Only the all-programming feed carries data; the other categories return
        # an empty channel. Match the exact path -- four of the five WWR feed URLs
        # contain "programming", so a substring match serves this item four times.
        if request.url.path == "/categories/remote-programming-jobs.rss":
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


# -- custom RSS / Atom feeds -------------------------------------------------


def test_parse_job_feed_splits_company_colon_role() -> None:
    rss = b"""<?xml version="1.0"?>
    <rss><channel>
      <title>Nomad Board</title>
      <item>
        <title>Hooli: Senior Platform Engineer</title>
        <link>https://board.example/jobs/1</link>
        <location>Remote (EU)</location>
        <description>&lt;p&gt;Scale &amp;amp; ship&lt;/p&gt;</description>
      </item>
    </channel></rss>"""

    out = parse_job_feed(rss, "https://www.board.example/feed.rss", 40)
    assert len(out) == 1
    assert out[0].company == "Hooli"
    assert out[0].role == "Senior Platform Engineer"
    assert out[0].link == "https://board.example/jobs/1"
    assert out[0].location == "Remote (EU)"
    assert out[0].description == "Scale & ship"
    # www. is stripped so two spellings of one host share a source label.
    assert out[0].source == "rss:board.example"


def test_parse_job_feed_splits_role_at_company() -> None:
    rss = b"""<?xml version="1.0"?>
    <rss><channel>
      <title>Job Feed</title>
      <item><title>Staff Engineer at Data at Scale</title>
        <guid isPermaLink="true">https://board.example/jobs/9</guid></item>
    </channel></rss>"""

    out = parse_job_feed(rss, "https://board.example/feed", 40)
    # The FIRST " at " wins, so the multi-word company survives intact.
    assert (out[0].company, out[0].role) == ("Data at Scale", "Staff Engineer")
    # No <link>, so the permalink guid is the posting URL.
    assert out[0].link == "https://board.example/jobs/9"


def test_parse_job_feed_falls_back_to_the_feed_title_as_company() -> None:
    rss = b"""<?xml version="1.0"?>
    <rss><channel>
      <title>Jobs at Hooli</title>
      <item><title>Backend Engineer</title><link>https://hooli.example/j/1</link></item>
    </channel></rss>"""

    out = parse_job_feed(rss, "https://hooli.example/feed", 40)
    assert (out[0].company, out[0].role) == ("Hooli", "Backend Engineer")


def test_parse_job_feed_strips_careers_boilerplate_from_the_feed_title() -> None:
    rss = b"""<?xml version="1.0"?>
    <rss><channel>
      <title>Globex Careers</title>
      <item><title>SRE</title><link>https://globex.example/j/2</link></item>
    </channel></rss>"""

    assert parse_job_feed(rss, "https://globex.example/feed", 40)[0].company == "Globex"


def test_parse_job_feed_reads_atom_entries() -> None:
    atom = b"""<?xml version="1.0"?>
    <feed xmlns="http://www.w3.org/2005/Atom">
      <title>Hooli Jobs</title>
      <entry>
        <title>Developer Advocate</title>
        <link rel="edit" href="https://hooli.example/api/3" />
        <link rel="alternate" href="https://hooli.example/jobs/3" />
        <summary type="html">&lt;p&gt;Talk to developers&lt;/p&gt;</summary>
      </entry>
    </feed>"""

    out = parse_job_feed(atom, "https://hooli.example/atom", 40)
    assert len(out) == 1
    assert (out[0].company, out[0].role) == ("Hooli", "Developer Advocate")
    # rel="alternate" is the human-facing page, not the API edit link.
    assert out[0].link == "https://hooli.example/jobs/3"
    assert out[0].description == "Talk to developers"


def test_parse_job_feed_honors_the_limit_and_skips_untitled_items() -> None:
    items = "".join(
        f"<item><title>Corp {i}: Engineer</title><link>https://b.example/{i}</link></item>"
        for i in range(5)
    )
    rss = f'<?xml version="1.0"?><rss><channel><title>B</title><item><title></title>' \
          f"</item>{items}</channel></rss>"

    out = parse_job_feed(rss.encode(), "https://b.example/feed", 3)
    assert [item.company for item in out] == ["Corp 0", "Corp 1", "Corp 2"]


def test_parse_job_feed_returns_nothing_for_a_document_it_cannot_parse() -> None:
    assert parse_job_feed(b"<html>not a feed", "https://b.example/feed", 40) == []
    assert parse_job_feed(b"", "https://b.example/feed", 40) == []


def _feed(items: str) -> bytes:
    return f'<?xml version="1.0"?><rss><channel>{items}</channel></rss>'.encode()


def _item(title: str, link: str) -> str:
    return f"<item><title>{title}</title><link>{link}</link><region>Remote</region></item>"


def test_fetch_remotefirstjobs_reads_role_at_company_titles() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("python.rss"):
            return httpx.Response(
                200,
                content=_feed(
                    _item("Machine Learning Engineer at Talent Inc.", "https://rfj.test/1")
                ),
            )
        return httpx.Response(200, content=_feed(""))

    out = fetch_remotefirstjobs(_client(handler), 40)
    assert len(out) == 1
    assert out[0].company == "Talent Inc."
    assert out[0].role == "Machine Learning Engineer"
    assert out[0].source == "remotefirstjobs"


def test_feed_set_splits_the_cap_evenly_so_one_busy_feed_cannot_starve_the_rest() -> None:
    """A first-come budget let WWR's ~118-item full-stack feed eat the whole cap."""
    busy = _feed("".join(_item(f"Hooli: Engineer {i}", f"https://wwr.test/{i}") for i in range(50)))

    def handler(request: httpx.Request) -> httpx.Response:
        if "full-stack" in request.url.path:
            return httpx.Response(200, content=busy)
        # A distinct posting per category — a shared URL would now be deduped.
        return httpx.Response(
            200, content=_feed(_item("Acme: Platform Engineer", f"https://w{request.url.path}"))
        )

    out = fetch_weworkremotely(_client(handler), 40)
    # 5 feeds, cap 40 -> 8 apiece; the busy feed is held to its share, and the
    # four one-item feeds still contribute rather than being cut off.
    assert sum(1 for lst in out if lst.company == "Hooli") == 8
    assert sum(1 for lst in out if lst.company == "Acme") == 4


def test_feed_set_survives_one_dead_category_feed() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if "front-end" in request.url.path:
            return httpx.Response(503)
        return httpx.Response(
            200, content=_feed(_item("Acme: Engineer", f"https://w{request.url.path}"))
        )

    out = fetch_weworkremotely(_client(handler), 40)
    assert len(out) == 4  # the other four feeds still land
    assert all(lst.source == "weworkremotely" for lst in out)


def test_fetch_workanywhere_recovers_the_employer_from_em_dash_titles() -> None:
    """Its titles are "Role - Company" with an em dash, which the shared splitter
    doesn't know; without the retitle hook every lead lands under the feed's own
    name and keeps the employer buried in the role."""
    feed = (
        '<?xml version="1.0"?><rss><channel>'
        "<title>WorkAnywhere.pro — Developer Remote Jobs</title>"
        "<item><title>Developer Advocate - AI &amp; Developer Experiences — Snowflake</title>"
        "<link>https://workanywhere.pro/jobs/1</link></item>"
        "</channel></rss>"
    ).encode()

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("developer.xml"):
            return httpx.Response(200, content=feed)
        return httpx.Response(200, content=_feed(""))

    out = fetch_workanywhere(_client(handler), 40)
    assert len(out) == 1
    assert out[0].company == "Snowflake"
    # The hyphen inside the role must survive; only the em dash splits.
    assert out[0].role == "Developer Advocate - AI & Developer Experiences"
    assert out[0].source == "workanywhere"


def test_workanywhere_retitle_leaves_a_dashless_title_alone() -> None:
    feed = (
        b'<?xml version="1.0"?><rss><channel><title>WorkAnywhere.pro</title>'
        b"<item><title>Acme: Platform Engineer</title>"
        b"<link>https://workanywhere.pro/jobs/2</link></item></channel></rss>"
    )

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("developer.xml"):
            return httpx.Response(200, content=feed)
        return httpx.Response(200, content=_feed(""))

    out = fetch_workanywhere(_client(handler), 40)
    assert (out[0].company, out[0].role) == ("Acme", "Platform Engineer")


def test_feed_set_takes_a_cross_listed_posting_once() -> None:
    """A job in three categories must not spend three feeds' worth of budget."""
    shared = _item("Acme: Staff Engineer", "https://wwr.test/shared")

    def handler(request: httpx.Request) -> httpx.Response:
        # Every category carries the same posting plus one of its own.
        own = _item(f"Hooli: Engineer {request.url.path}", f"https://wwr.test{request.url.path}")
        return httpx.Response(200, content=_feed(shared + own))

    out = fetch_weworkremotely(_client(handler), 40)
    links = [lst.link for lst in out]
    assert links.count("https://wwr.test/shared") == 1
    # 5 feeds: the shared posting once, plus each category's own listing.
    assert len(out) == 6


def test_feed_set_dedup_does_not_shrink_a_feeds_share() -> None:
    """The duplicate is skipped over, not counted against the feed's slots."""
    dupe = _item("Acme: Staff Engineer", "https://wwr.test/shared")

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/categories/remote-programming-jobs.rss":
            return httpx.Response(200, content=_feed(dupe))
        # This feed leads with the already-taken posting, then its own eight.
        own = "".join(
            _item(f"Hooli: Engineer {i}", f"https://wwr.test/b{i}") for i in range(8)
        )
        return httpx.Response(200, content=_feed(dupe + own))

    out = fetch_weworkremotely(_client(handler), 40)
    # 40 // 5 = 8 per feed. The four later feeds each still deliver a full 8.
    assert sum(1 for lst in out if lst.company == "Hooli") == 8
    assert sum(1 for lst in out if lst.link == "https://wwr.test/shared") == 1
