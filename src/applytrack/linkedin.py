# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""LinkedIn as a discovery source (#233) — the employer's posting, never Easy Apply.

LinkedIn's postings come in two kinds. **Easy Apply** keeps the application inside
LinkedIn; **offsite apply** sends the candidate to the employer's own careers site or
ATS. Only the second kind is wanted here: the poller finds the posting on LinkedIn,
reads where its Apply leads, and stages the lead with the **employer's posting** as
its link — the same #191 rule every aggregator gets. Easy Apply-only postings are
skipped and remembered, so they are never read twice.

Two ways in, same listings:

* **Signed in** — the candidate's own account. The .NET agent's browser signs in with
  the board account saved for ``linkedin.com`` (#221-style) and keeps the session
  cookies sealed on that row; this searches with them through the endpoints
  LinkedIn's own web app calls (``/voyager/api/…``). The job-detail call names the
  apply method outright: ``OffsiteApply`` with the employer's ``companyApplyUrl``,
  or an onsite (Easy Apply) method. LinkedIn's official API (OAuth) only opens job
  search to approved partners, which is why the site's own endpoints are used.
* **Guest** — no account. LinkedIn's public job search (``/jobs-guest/…``) renders
  cards for anyone and a posting page that carries the offsite apply link in a
  hidden ``<code id="applyUrl">`` element. Rate-limited hard (HTTP 429 after a few
  dozen calls), so it is the fallback while no session is kept. The approach — and
  the ``?url=`` extraction — follows JobSpy (MIT, https://github.com/speedyapply/JobSpy).

Every request is the tenant's own; nothing here is shared across tenants.
"""

from __future__ import annotations

import json
import logging
import re
import time
import urllib.parse
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from html import unescape
from typing import Any

import httpx

from applytrack.poll import Listing, classify

logger = logging.getLogger(__name__)

BASE = "https://www.linkedin.com"
SOURCE = "linkedin"
#: Board-account hosts that mean the candidate's LinkedIn sign-in.
ACCOUNT_HOSTS = ("linkedin.com", "www.linkedin.com")
#: How long a kept session is trusted before the agent renews it. The cookie itself
#: lasts a year; a renewal well inside that keeps a lapse from costing a poll.
SESSION_DAYS = 300
#: Keyword searches per poll (one page each). Every card that survives the keyword
#: check costs one job-detail call, so the cap is the account's request budget.
MAX_KEYWORD_QUERIES = 8
PAGE_SIZE = 25
#: Only postings listed within this window: the poller runs hourly and the ledger
#: remembers what it has read, so a day is plenty and keeps the detail calls few.
POSTED_WITHIN_SECONDS = 86_400
#: United States, LinkedIn's geo id; the search is remote-filtered when the profile is.
DEFAULT_GEO_ID = "103644278"
#: A pause between calls, the way a person's browser paces them.
PACE_SECONDS = 0.6

_OFFSITE = "com.linkedin.voyager.jobs.OffsiteApply"
_CARD_TYPE = "com.linkedin.voyager.dash.jobs.JobPostingCard"
_URN_ID_RE = re.compile(r"(\d+)\s*$")

# Guest HTML: the card list and the posting page (JobSpy's selectors, as regexes).
_GUEST_CARD_RE = re.compile(r'data-entity-urn="urn:li:jobPosting:(\d+)"(.*?)</li>', re.DOTALL)
_GUEST_TITLE_RE = re.compile(r'class="base-search-card__title[^"]*"[^>]*>(.*?)</h3>', re.DOTALL)
_GUEST_COMPANY_RE = re.compile(
    r'class="base-search-card__subtitle[^"]*"[^>]*>(.*?)</h4>', re.DOTALL
)
_GUEST_LOCATION_RE = re.compile(
    r'class="job-search-card__location[^"]*"[^>]*>(.*?)</span>', re.DOTALL
)
_GUEST_APPLY_RE = re.compile(r'<code\s+id="applyUrl"[^>]*>(.*?)</code>', re.DOTALL)
_GUEST_APPLY_URL_RE = re.compile(r"(?<=\?url=)[^\"&]+")
_TAG_RE = re.compile(r"<[^>]+>")
_WS_RE = re.compile(r"\s+")


class NeedsSignIn(Exception):
    """LinkedIn did not accept the kept session."""


class RateLimited(Exception):
    """LinkedIn answered 429: stop for this poll."""


@dataclass
class LinkedInAccount:
    """The tenant's LinkedIn sign-in and the session kept for it."""

    email: str
    session: str = ""
    session_expires_at: datetime | None = None

    @property
    def session_fresh(self) -> bool:
        return bool(self.cookies) and (
            self.session_expires_at is None or self.session_expires_at > datetime.now(timezone.utc)
        )

    @property
    def cookies(self) -> dict[str, str]:
        """The kept session as cookies: ``li_at`` and ``JSESSIONID``, or nothing."""
        return parse_session(self.session)


def parse_session(session: str) -> dict[str, str]:
    """The sealed session's plaintext — JSON ``{"li_at": …, "JSESSIONID": …}`` — as cookies.
    Anything without a ``li_at`` is no session."""
    if not session:
        return {}
    try:
        data = json.loads(session)
    except ValueError:
        return {}
    if not isinstance(data, dict) or not str(data.get("li_at") or "").strip():
        return {}
    out = {"li_at": str(data["li_at"]).strip()}
    js = str(data.get("JSESSIONID") or "").strip()
    out["JSESSIONID"] = js or 'ajax:0000000000000000000'
    return out


@dataclass
class Card:
    """One search result, before its posting is read."""

    job_id: str
    title: str
    company: str
    location: str

    @property
    def view_url(self) -> str:
        return f"{BASE}/jobs/view/{self.job_id}"


def _text(html: str) -> str:
    return _WS_RE.sub(" ", _TAG_RE.sub(" ", unescape(html or ""))).strip()


def _job_id(urn: object) -> str:
    m = _URN_ID_RE.search(str(urn or ""))
    return m.group(1) if m else ""


class Client:
    """One tenant's view of LinkedIn's job search over an ``httpx.Client``: signed in when
    the account has a session, the guest endpoints when it has none."""

    def __init__(self, client: httpx.Client, account: LinkedInAccount | None = None,
                 *, pace: Callable[[float], None] = time.sleep) -> None:
        self._client = client
        self._pace = pace
        self._cookies = account.cookies if account else {}
        for name, value in self._cookies.items():
            client.cookies.set(name, value, domain=".www.linkedin.com")

    @property
    def signed_in(self) -> bool:
        return bool(self._cookies)

    def _voyager_headers(self) -> dict[str, str]:
        # The web app sends its JSESSIONID back as the CSRF token, quotes stripped.
        return {
            "csrf-token": self._cookies.get("JSESSIONID", "").strip('"'),
            "x-restli-protocol-version": "2.0.0",
            "accept": "application/vnd.linkedin.normalized+json+2.1",
            "x-li-lang": "en_US",
            "referer": BASE + "/jobs/search/",
        }

    def _check(self, r: httpx.Response) -> None:
        if r.status_code == 429:
            raise RateLimited("LinkedIn answered 429")
        # A signed-in call that bounces to the sign-in page is the session's end.
        if r.status_code in (401, 403) or (
            r.status_code in (301, 302, 303, 307, 308)
            and any(s in r.headers.get("location", "") for s in ("/login", "/uas/", "/checkpoint"))
        ):
            raise NeedsSignIn(f"LinkedIn answered {r.status_code}")
        if r.status_code in (301, 302, 303, 307, 308):
            raise NeedsSignIn("LinkedIn redirected the request")
        r.raise_for_status()

    # -- signed in --------------------------------------------------------------

    def search(self, keywords: str, *, remote_only: bool, start: int = 0) -> list[Card]:
        """One page of the signed-in job search for ``keywords``."""
        filters = [f"timePostedRange:List(r{POSTED_WITHIN_SECONDS})"]
        if remote_only:
            filters.insert(0, "workplaceType:List(2)")
        query = (
            "(origin:JOB_SEARCH_PAGE_JOB_FILTER,"
            f"keywords:{urllib.parse.quote(keywords, safe='')},"
            f"locationUnion:(geoId:{DEFAULT_GEO_ID}),"
            f"selectedFilters:({','.join(filters)}),spellCorrectionEnabled:true)"
        )
        url = (
            f"{BASE}/voyager/api/voyagerJobsDashJobCards"
            "?decorationId=com.linkedin.voyager.dash.deco.jobs.search.JobSearchCardsCollection-220"
            f"&count={PAGE_SIZE}&q=jobSearch&query={query}&start={start}"
        )
        r = self._client.get(url, headers=self._voyager_headers())
        self._check(r)
        try:
            data = r.json()
        except ValueError as exc:
            raise NeedsSignIn("LinkedIn answered with a page, not the search") from exc
        return cards_from_search(data)

    def posting(self, job_id: str) -> dict[str, Any]:
        """The signed-in job posting: title, description, apply method."""
        r = self._client.get(
            f"{BASE}/voyager/api/jobs/jobPostings/{job_id}",
            headers={**self._voyager_headers(), "accept": "application/json"},
        )
        self._check(r)
        try:
            data = r.json()
        except ValueError as exc:
            raise NeedsSignIn("LinkedIn answered with a page, not the posting") from exc
        return data if isinstance(data, dict) else {}

    # -- guest ------------------------------------------------------------------

    def guest_search(self, keywords: str, *, remote_only: bool, start: int = 0) -> list[Card]:
        """One page of the public job search, rendered for anyone."""
        params: list[tuple[str, str | int | float | bool | None]] = [
            ("keywords", keywords), ("location", "United States"),
            ("f_TPR", f"r{POSTED_WITHIN_SECONDS}"), ("start", str(start)),
        ]
        if remote_only:
            params.append(("f_WT", "2"))
        r = self._client.get(
            f"{BASE}/jobs-guest/jobs/api/seeMoreJobPostings/search", params=params,
            headers={"accept": "text/html"},
        )
        if r.status_code == 429:
            raise RateLimited("LinkedIn answered 429")
        r.raise_for_status()
        return cards_from_guest_html(r.text)

    def guest_apply_link(self, job_id: str) -> str | None:
        """Where the public posting page's Apply leads: the employer's URL, or ``None`` for
        Easy Apply (no such link on the page)."""
        r = self._client.get(
            f"{BASE}/jobs-guest/jobs/api/jobPosting/{job_id}", headers={"accept": "text/html"}
        )
        if r.status_code == 429:
            raise RateLimited("LinkedIn answered 429")
        r.raise_for_status()
        return guest_apply_link_from_html(r.text)


# -- parsing ------------------------------------------------------------------


def cards_from_search(data: dict[str, Any]) -> list[Card]:
    """The job cards in a signed-in search response, in order, skipping cards with no
    title (ads and the state records that ride along)."""
    out: list[Card] = []
    included = data.get("included") if isinstance(data, dict) else None
    for item in included or []:
        if not isinstance(item, dict) or item.get("$type") != _CARD_TYPE:
            continue
        job_id = _job_id(item.get("jobPostingUrn"))
        title = (item.get("title") or {}).get("text") if isinstance(item.get("title"), dict) else ""
        title = " ".join(str(title or "").split()).strip()
        if not job_id or not title:
            continue
        company = str((item.get("primaryDescription") or {}).get("text") or "").strip()
        location = str((item.get("secondaryDescription") or {}).get("text") or "").strip()
        out.append(Card(job_id=job_id, title=title, company=company, location=location))
    return out


def apply_link(posting: dict[str, Any]) -> str | None:
    """The employer's apply URL from a signed-in posting, or ``None`` when the posting
    is Easy Apply (any onsite method) or says nothing."""
    method = posting.get("applyMethod")
    if not isinstance(method, dict):
        return None
    offsite = method.get(_OFFSITE)
    if not isinstance(offsite, dict):
        return None
    url = str(offsite.get("companyApplyUrl") or "").strip()
    return url if url.startswith(("http://", "https://")) else None


def cards_from_guest_html(html: str) -> list[Card]:
    out: list[Card] = []
    for job_id, body in _GUEST_CARD_RE.findall(html):
        title = _GUEST_TITLE_RE.search(body)
        company = _GUEST_COMPANY_RE.search(body)
        location = _GUEST_LOCATION_RE.search(body)
        if not title:
            continue
        out.append(Card(
            job_id=job_id,
            title=_text(title.group(1)),
            company=_text(company.group(1)) if company else "",
            location=_text(location.group(1)) if location else "",
        ))
    return out


def guest_apply_link_from_html(html: str) -> str | None:
    m = _GUEST_APPLY_RE.search(html)
    if not m:
        return None
    hit = _GUEST_APPLY_URL_RE.search(unescape(m.group(1)).strip().strip('"'))
    if not hit:
        return None
    url = urllib.parse.unquote(hit.group(0))
    return url if url.startswith(("http://", "https://")) else None


def _description(posting: dict[str, Any]) -> str:
    desc = posting.get("description")
    if isinstance(desc, dict):
        return " ".join(str(desc.get("text") or "").split())
    return " ".join(str(desc or "").split())


def to_listing(
    card: Card, employer_link: str, description: str = "", *, remote: bool = False
) -> Listing:
    """A card whose Apply leads to ``employer_link`` as the poller's :class:`Listing`: the
    LinkedIn posting stays the listing's link (the dedupe key, and an aggregator link the
    #191 rule resolves), the employer's URL rides as ``apply_link`` and is what gets stored.
    ``remote`` says the search was remote-filtered, which a card's location may not."""
    location = card.location
    if remote and "remote" not in location.lower():
        location = f"{location} (Remote)" if location else "Remote"
    return Listing(
        company=card.company,
        role=card.title,
        link=card.view_url,
        location=location,
        source=SOURCE,
        description=description,
        apply_link=employer_link,
    )


def queries_for(keywords: Iterable[str]) -> list[str]:
    """The keyword searches worth a page each: the tenant's keywords, de-duped and capped."""
    out: list[str] = []
    for k in keywords:
        q = " ".join(str(k).split()).strip()
        if q and q.lower() not in {x.lower() for x in out}:
            out.append(q)
        if len(out) >= MAX_KEYWORD_QUERIES:
            break
    return out


def fetch_listings(
    client: Client,
    keywords: Iterable[str],
    *,
    remote_only: bool,
    limit: int,
    already_seen: Callable[[str], bool] = lambda _url: False,
    remember: Callable[[str], None] = lambda _url: None,
) -> list[Listing]:
    """One page per keyword, then the posting of every card that is new and names a
    keyword in its title (the fit score itself is the stager's, with the description);
    offsite-apply postings become listings with the employer's link, Easy Apply
    ones are remembered through ``remember`` and dropped. ``already_seen`` (by the
    LinkedIn posting URL) keeps a card the ledger knows from costing another call.
    A 429 ends the poll with what was gathered."""
    keyword_list = list(keywords)
    out: list[Listing] = []
    done: set[str] = set()
    try:
        for q in queries_for(keyword_list):
            cards = (client.search if client.signed_in else client.guest_search)(
                q, remote_only=remote_only
            )
            client._pace(PACE_SECONDS)
            for card in cards:
                if card.job_id in done or already_seen(card.view_url):
                    continue
                done.add(card.job_id)
                if not classify(card.title, "", keyword_list)[1]:
                    continue
                if client.signed_in:
                    posting = client.posting(card.job_id)
                    link = apply_link(posting)
                    description = _description(posting)
                else:
                    link = client.guest_apply_link(card.job_id)
                    description = ""
                client._pace(PACE_SECONDS)
                if not link:
                    logger.info(
                        "linkedin: %s — %s is Easy Apply only; skipped", card.company, card.title
                    )
                    remember(card.view_url)
                    continue
                out.append(to_listing(card, link, description, remote=remote_only))
                if len(out) >= limit * 3:
                    return out
    except RateLimited:
        logger.warning(
            "linkedin: rate-limited (429) after %d listings; the rest waits for the next poll",
            len(out),
        )
    return out


def session_expiry() -> datetime:
    return datetime.now(timezone.utc) + timedelta(days=SESSION_DAYS)
