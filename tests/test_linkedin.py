# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""LinkedIn as a source (#233): the signed-in search and posting, the guest fallback,
Easy Apply skipped, the ledger consulted before a posting is read, the per-tenant
gather in the worker, and the employer's link stored — all offline."""

from __future__ import annotations

import json

import httpx
import pytest

from applytrack import linkedin
from applytrack.criteria import Criteria
from applytrack.linkedin import Card, Client, LinkedInAccount
from applytrack.poll import Listing, is_aggregator_link, score_and_stage
from applytrack.worker import run_all_tenants

SESSION = json.dumps({"li_at": "AQEDA-token", "JSESSIONID": '"ajax:123"'})


def _card(job_id: int, title: str, company: str, location: str) -> dict:
    return {
        "$type": "com.linkedin.voyager.dash.jobs.JobPostingCard",
        "jobPostingUrn": f"urn:li:fsd_jobPosting:{job_id}",
        "title": {"text": f"{title}  "},
        "primaryDescription": {"text": company},
        "secondaryDescription": {"text": location},
    }


SEARCH = {
    "data": {"paging": {"total": 2}},
    "included": [
        _card(4464862474, "Software Engineer, Cloud", "Stryker", "Chicago, IL (Remote)"),
        {
            "$type": "com.linkedin.voyager.dash.jobs.JobPostingCard",
            "jobPostingUrn": "urn:li:fsd_jobPosting:1",
        },
        _card(4436625592, ".NET Developer", "Jobot", "United States (Remote)"),
        _card(4400000001, "Registered Nurse", "Hospital", "Omaha, NE"),
        {"$type": "com.linkedin.voyager.dash.organization.Company", "entityUrn": "urn:li:c:1"},
    ],
}

STRYKER = "https://careers.stryker.com/software-engineer-cloud/job/62F1?source=LinkedIn"
OFFSITE = {
    "title": "Software Engineer, Cloud",
    "description": {"text": "Build cloud services in C# and .NET on Azure."},
    "applyMethod": {
        "com.linkedin.voyager.jobs.OffsiteApply": {
            "companyApplyUrl": STRYKER,
            "inPageOffsiteApply": False,
        }
    },
}
EASY_APPLY = {
    "title": ".NET Developer",
    "description": {"text": "Easy Apply role."},
    "applyMethod": {"com.linkedin.voyager.jobs.SimpleOnsiteApply": {"unifyApplyEnabled": True}},
}

GUEST_CARDS = """
<li><div class="base-card" data-entity-urn="urn:li:jobPosting:4458832735">
  <h3 class="base-search-card__title">
        Sr. Software Engineer (.NET / PHP)
  </h3>
  <h4 class="base-search-card__subtitle"><a class="hidden-nested-link" href="x">
            Jobot &amp; Co
  </a></h4>
  <span class="job-search-card__location">
            Indianapolis, IN
  </span></div></li>
<li><div data-entity-urn="urn:li:jobPosting:4462847517">
  <h3 class="base-search-card__title">Nurse</h3></div></li>
"""
GUEST_POSTING = (
    '<html><body><code id="applyUrl" style="display: none"><!--"https://www.linkedin.com/jobs/view/'
    'externalApply/4458832735?url=https%3A%2F%2Fjobs%2Eexample%2Ecom%2Fjob%2F42%3Fsrc%3DLinkedIn'
    '&urlHash=abc"--></code></body></html>'
)


def _handler(*, session_ok: str = "AQEDA-token", postings: dict[str, dict] | None = None,
             guest_429: bool = False):
    """A fake www.linkedin.com: the Voyager search and postings behind the session, the
    guest search and posting pages for anyone."""
    log: list[str] = []
    if postings is None:
        postings = {"4464862474": OFFSITE, "4436625592": EASY_APPLY}

    def handler(req: httpx.Request) -> httpx.Response:
        log.append(f"{req.method} {req.url.path}?{req.url.query.decode()}")
        path = req.url.path
        if path.startswith("/voyager/"):
            cookie = req.headers.get("cookie", "")
            if f"li_at={session_ok}" not in cookie:
                return httpx.Response(302, headers={"location": "https://www.linkedin.com/uas/login"})
            if req.headers.get("csrf-token") != "ajax:123":
                return httpx.Response(403)
            if path == "/voyager/api/voyagerJobsDashJobCards":
                return httpx.Response(200, json=SEARCH)
            if path.startswith("/voyager/api/jobs/jobPostings/"):
                jid = path.rsplit("/", 1)[-1]
                if jid in postings:
                    return httpx.Response(200, json=postings[jid])
                return httpx.Response(404)
        if path == "/jobs-guest/jobs/api/seeMoreJobPostings/search":
            return httpx.Response(429) if guest_429 else httpx.Response(200, text=GUEST_CARDS)
        if path.startswith("/jobs-guest/jobs/api/jobPosting/"):
            jid = path.rsplit("/", 1)[-1]
            easy = "<html>Easy Apply</html>"
            return httpx.Response(200, text=GUEST_POSTING if jid == "4458832735" else easy)
        return httpx.Response(404)

    return handler, log


def _client(handler, account: LinkedInAccount | None) -> Client:
    http = httpx.Client(transport=httpx.MockTransport(handler))
    return Client(http, account, pace=lambda _s: None)


def test_the_session_is_the_cookie_pair_and_a_blank_one_is_no_session() -> None:
    assert linkedin.parse_session(SESSION) == {"li_at": "AQEDA-token", "JSESSIONID": '"ajax:123"'}
    assert linkedin.parse_session("") == {}
    assert linkedin.parse_session("not json") == {}
    assert linkedin.parse_session(json.dumps({"JSESSIONID": "x"})) == {}
    assert LinkedInAccount(email="a@b.c", session=SESSION).session_fresh
    assert not LinkedInAccount(email="a@b.c").session_fresh


def test_search_cards_are_the_titled_job_cards_and_the_posting_names_the_apply_method() -> None:
    cards = linkedin.cards_from_search(SEARCH)
    assert [c.job_id for c in cards] == ["4464862474", "4436625592", "4400000001"]
    assert cards[0] == Card(
        "4464862474", "Software Engineer, Cloud", "Stryker", "Chicago, IL (Remote)"
    )
    assert cards[0].view_url == "https://www.linkedin.com/jobs/view/4464862474"
    assert linkedin.apply_link(OFFSITE) == STRYKER
    assert linkedin.apply_link(EASY_APPLY) is None
    assert linkedin.apply_link({}) is None


def test_guest_pages_parse_the_way_jobspy_reads_them() -> None:
    cards = linkedin.cards_from_guest_html(GUEST_CARDS)
    assert cards == [
        Card("4458832735", "Sr. Software Engineer (.NET / PHP)", "Jobot & Co", "Indianapolis, IN"),
        Card("4462847517", "Nurse", "", ""),
    ]
    assert (
        linkedin.guest_apply_link_from_html(GUEST_POSTING)
        == "https://jobs.example.com/job/42?src=LinkedIn"
    )
    assert linkedin.guest_apply_link_from_html("<html>Easy Apply</html>") is None


def test_easy_apply_postings_are_staged_only_when_the_tenant_switched_it_on() -> None:
    # Easy Apply was always skipped; with the tenant's switch on it is staged on the posting's
    # own link, flagged so the agent's browser drives LinkedIn's dialog instead of reading the
    # link as a listing (#278).
    handler, _ = _handler()
    client = _client(handler, LinkedInAccount(email="a@b.c", session=SESSION))
    remembered: list[str] = []
    listings = linkedin.fetch_listings(
        client, [".net", "cloud", "nurse"], remote_only=True, limit=40,
        already_seen=lambda url: url.endswith("/4400000001"), remember=remembered.append,
        easy_apply=True,
    )
    easy = [item for item in listings if item.source == linkedin.EASY_SOURCE]
    assert [item.link for item in easy] == ["https://www.linkedin.com/jobs/view/4436625592"]
    assert easy[0].apply_link == ""
    # Staged, so it is the stager's to remember — not dropped into the ledger unread.
    assert remembered == []
    # The offsite posting is untouched by the switch.
    assert [item.apply_link for item in listings if item.source == "linkedin"] == [STRYKER]


def test_the_easy_apply_source_is_the_one_the_agent_looks_for() -> None:
    # "auto:" + this is AtsProvider.LinkedInEasySource on the .NET side; the schema is the contract.
    assert f"auto:{linkedin.EASY_SOURCE}" == "auto:linkedin:easy"


def test_signed_in_fetch_keeps_offsite_skips_easy_apply_and_asks_the_ledger_first() -> None:
    handler, log = _handler()
    client = _client(handler, LinkedInAccount(email="a@b.c", session=SESSION))
    remembered: list[str] = []
    listings = linkedin.fetch_listings(
        client, [".net", "cloud", "nurse"], remote_only=True, limit=40,
        already_seen=lambda url: url.endswith("/4400000001"), remember=remembered.append,
    )
    assert listings == [
        Listing(
            company="Stryker", role="Software Engineer, Cloud",
            link="https://www.linkedin.com/jobs/view/4464862474", location="Chicago, IL (Remote)",
            source="linkedin", description="Build cloud services in C# and .NET on Azure.",
            apply_link=STRYKER,
        )
    ]
    # The Easy Apply posting is remembered so it is never read again; the one the ledger
    # already knows was never read at all.
    assert remembered == ["https://www.linkedin.com/jobs/view/4436625592"]
    read = [e for e in log if "/jobs/jobPostings/" in e]
    assert len(read) == 2 and not any("4400000001" in e for e in read)
    searches = [e for e in log if "voyagerJobsDashJobCards" in e]
    assert len(searches) == 3  # one page per keyword
    assert "workplaceType:List(2)" in searches[0] and "timePostedRange" in searches[0]


def test_a_bounced_session_raises_needs_sign_in_and_a_429_ends_the_poll_early() -> None:
    handler, _ = _handler()
    stale = json.dumps({"li_at": "stale", "JSESSIONID": '"ajax:123"'})
    client = _client(handler, LinkedInAccount(email="a@b.c", session=stale))
    with pytest.raises(linkedin.NeedsSignIn):
        linkedin.fetch_listings(client, [".net"], remote_only=False, limit=10)
    handler, _ = _handler(guest_429=True)
    guest = _client(handler, None)
    assert linkedin.fetch_listings(guest, [".net"], remote_only=False, limit=10) == []


def test_without_a_session_the_guest_search_is_used() -> None:
    handler, log = _handler()
    listings = linkedin.fetch_listings(_client(handler, None), [".net"], remote_only=True, limit=10)
    assert [(item.company, item.apply_link) for item in listings] == [
        ("Jobot & Co", "https://jobs.example.com/job/42?src=LinkedIn")
    ]
    assert all(e.startswith("GET /jobs-guest/") for e in log)
    assert "f_WT=2" in log[0]


class _Repo:
    """An in-memory tenant with a LinkedIn account and the session kept for it."""

    def __init__(self, tenant_id: int, session: str = "", account: bool = True) -> None:
        self.tenant_id = tenant_id
        self.saved: list[tuple[str, object]] = []
        self.staged: list[str] = []
        self.marked: list[tuple[str, str]] = []
        self._session = session
        self._account = account

    def load_profile(self) -> Criteria:
        return Criteria(keywords=[".net", "cloud"], sources={"linkedin": True}, remote_only=True)

    def linkedin_account(self) -> LinkedInAccount | None:
        return LinkedInAccount(email="a@b.c", session=self._session) if self._account else None

    def save_linkedin_session(self, session: str, expires_at: object) -> None:
        self.saved.append((session, expires_at))

    def seen_url(self, url: str) -> bool:
        return False

    # LeadRepo
    def iter_existing(self):
        return iter(())

    def blacklist_companies(self) -> list[str]:
        return []

    def load_seen(self) -> tuple[set[str], set[str]]:
        return set(), set()

    def mark_seen(self, url_key: str, slug_key: str) -> None:
        self.marked.append((url_key, slug_key))

    def add_lead(self, fields) -> str:  # type: ignore[no-untyped-def]
        self.staged.append(fields.link)
        return f"{fields.company}.md"


def _run(monkeypatch, repo: _Repo, handler):  # type: ignore[no-untyped-def]
    real_client = httpx.Client

    def fake_client(**kw):  # type: ignore[no-untyped-def]
        kw.pop("transport", None)
        return real_client(transport=httpx.MockTransport(handler), **kw)

    monkeypatch.setattr("applytrack.worker.httpx.Client", fake_client)
    monkeypatch.setattr("applytrack.linkedin.time.sleep", lambda _s: None)
    return run_all_tenants(
        tenant_ids=[repo.tenant_id], repo_for=lambda _: repo, gathered={}, verify_links=False
    )


def test_the_worker_searches_linkedin_with_the_kept_session(monkeypatch) -> None:  # type: ignore[no-untyped-def]
    handler, log = _handler()
    repo = _Repo(7, session=SESSION)
    results = _run(monkeypatch, repo, handler)
    assert results[7] == ["Stryker.md"]
    assert repo.saved == []
    assert any("/voyager/" in e for e in log) and not any("/jobs-guest/" in e for e in log)
    # The Easy Apply posting went into the ledger by its LinkedIn URL, with no slug.
    assert ("linkedin.com/jobs/view/4436625592", "") in repo.marked


def test_a_bounced_session_is_cleared_for_the_agent_to_renew(monkeypatch) -> None:  # type: ignore[no-untyped-def]
    handler, _ = _handler()
    repo = _Repo(7, session=json.dumps({"li_at": "stale", "JSESSIONID": "ajax:123"}))
    assert _run(monkeypatch, repo, handler)[7] == []
    assert repo.saved == [("", None)]


def test_without_an_account_or_session_the_worker_searches_as_a_guest(
    monkeypatch,  # type: ignore[no-untyped-def]
) -> None:
    for repo in (_Repo(7, account=False), _Repo(8)):
        handler, log = _handler()
        assert _run(monkeypatch, repo, handler)[repo.tenant_id] == ["Jobot & Co.md"]
        assert all("/jobs-guest/" in e for e in log)
        assert repo.saved == []


def test_a_linkedin_lead_is_stored_with_the_employers_link(monkeypatch) -> None:  # type: ignore[no-untyped-def]
    from applytrack.linkcheck import LinkStatus

    assert is_aggregator_link("https://www.linkedin.com/jobs/view/4464862474")
    assert is_aggregator_link("https://jobright.ai/jobs/info/b2b_1769670052353_604")  # #238
    assert is_aggregator_link("https://www.sundayy.com/job-description/us1E83")  # #243
    assert is_aggregator_link("https://us.thebigjobsite.com/redirectjob?id=1E83")  # #243
    assert is_aggregator_link("https://lensa.com/software-engineer-jobs-in-lafayette-co")  # #243
    assert is_aggregator_link("https://www.fetchjobs.co/job-description-usb/DBE262F9")  # #251

    class _Client:
        def close(self) -> None:
            return None

    monkeypatch.setattr("applytrack.poll.ssrf_safe_client", lambda **_: _Client())
    monkeypatch.setattr("applytrack.poll.is_reachable", lambda url, *, client=None: True)
    monkeypatch.setattr(
        "applytrack.poll.probe",
        lambda url, *, client=None, timeout=12.0: LinkStatus(
            url=url, ok=True, status_code=200, final_url=url
        ),
    )
    repo = _Repo(7)
    listing = linkedin.to_listing(
        Card("4464862474", "Software Engineer, Cloud", "Stryker", "Chicago, IL (Remote)"),
        "https://careers.stryker.com/job/62F1",
    )
    profile = repo.load_profile()
    assert score_and_stage(repo, profile, [listing], verify_links=True) == ["Stryker.md"]
    assert repo.staged == ["https://careers.stryker.com/job/62F1"]
    assert ("linkedin.com/jobs/view/4464862474", "stryker-software-engineer-cloud") in repo.marked
