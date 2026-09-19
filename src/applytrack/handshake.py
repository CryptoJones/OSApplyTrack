# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Handshake as a discovery source (#267) — campus recruiting, signed in.

Handshake is where US colleges run their campus recruiting: employers post, schools
approve, students search. It is a large slice of entry-level and internship hiring that
no other source here reaches.

**Why the tenant's own session, not an API key.** Handshake does publish an official
API, but the EDU API is read-only, scoped to one institution, and gated to Career
Services partners through a developer portal Handshake runs — a self-hosted tenant
cannot obtain one. The alternative is the one the LinkedIn source already takes (#233):
sign in with the candidate's own account and use the endpoint Handshake's own web app
calls (``POST /hs/graphql``), which is a GraphQL endpoint with named operations.

**A posting here is a listing of the employer's.** Handshake's job search returns the
posting's own copy, but ``job.jobApplySetting`` says how it is applied to:
``externalUrl`` is the employer's own posting — the ATS Handshake tracked it from
(iCIMS, BambooHR, Workday …) — and ``applyType`` is ``EXTERNAL`` or
``HANDSHAKE_AND_EXTERNAL``. That employer URL is the lead's ``apply_link`` under the
#191 rule, exactly as an offsite-apply LinkedIn posting's is. A posting Handshake hosts
itself (``applyType: HANDSHAKE``, no employer URL) is remembered through ``remember``
and skipped rather than staged as a dead end, which is what LinkedIn's Easy Apply gets.

**The kept session is one cookie.** ``hss-global`` (httpOnly, ~90 days) is the whole
session: a GraphQL call carrying only that cookie answers exactly as the browser does.
The .NET agent's browser signs in with the board account saved for ``joinhandshake.com``
and seals it on that row; this reads it.

Every request is the tenant's own; nothing here is shared across tenants.
"""

from __future__ import annotations

import json
import logging
import time
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any

import httpx

from applytrack.poll import Listing, classify

logger = logging.getLogger(__name__)

BASE = "https://app.joinhandshake.com"
SOURCE = "handshake"

#: Board-account hosts that mean "this tenant's Handshake sign-in". Mirrors the .NET
#: side's list, which normalizes the same way on the write path.
ACCOUNT_HOSTS = ("joinhandshake.com", "app.joinhandshake.com")

#: The whole session: one httpOnly cookie, which is what the renewer seals.
SESSION_COOKIE = "hss-global"

#: ``hss-global`` is issued for about ninety days; renew well inside that.
SESSION_DAYS = 90

GRAPHQL_URL = BASE + "/hs/graphql"
JOB_URL = BASE + "/jobs"

#: The app's own page size. Capped so one keyword cannot walk the whole board.
PAGE_SIZE = 25
MAX_PAGES = 4

#: One request at a time, politely. Handshake is a third party and this is a poll.
PACE_SECONDS = 0.5

#: The channel the web app searches on; without it the endpoint answers for another surface.
CHANNEL = "NL_SEARCH_CHANNEL"

# The app's own search document, trimmed to the fields a Listing needs. GraphQL lets the
# client choose, so this asks for the description (the fit judge's input), the employer,
# the location, the pay and the dates — and nothing else. ``filter.query`` is the
# server-side keyword search: "software engineer" narrows 72,562 postings to 5,517.
SEARCH_QUERY = """
query JobSearchQuery($first: Int, $after: String, $input: JobSearchInput) {
  jobSearch(first: $first, after: $after, input: $input) {
    totalCount
    pageInfo { hasNextPage endCursor }
    edges {
      node {
        id
        job {
          id
          title
          description
          createdAt
          expirationDate
          remote
          onSite
          hybrid
          locations { displayName }
          employmentType { name }
          jobType { name }
          salaryRange { min max currency paySchedule { friendlyName } }
          employer { name industry { name } }
        }
      }
    }
  }
}
""".strip()

# The employer's own URL lives on the detail type, not on the search result, so each
# candidate that names a keyword costs one more call. It is small and carries only what
# the #191 decision needs.
APPLY_QUERY = """
query JobApplySetting($id: ID!) {
  job(id: $id) {
    id
    atsProvider
    jobApplySetting { applyType externalApplyType externalUrl alternativeExternalUrl }
  }
}
""".strip()

#: ``applyType`` values that mean the employer takes the application themselves. Anything
#: else is Handshake's own apply flow, which this source does not stage.
EXTERNAL_APPLY_TYPES = frozenset({"EXTERNAL", "HANDSHAKE_AND_EXTERNAL"})


class NeedsSignIn(Exception):
    """Handshake bounced the kept session: it has to be renewed through the browser."""


class RateLimited(Exception):
    """Handshake answered 429; the rest of the poll waits for the next pass."""


@dataclass
class HandshakeAccount:
    """The tenant's Handshake sign-in and the session kept for it."""

    username: str
    session: str = ""
    session_expires_at: datetime | None = None

    @property
    def session_fresh(self) -> bool:
        return bool(self.cookie) and (
            self.session_expires_at is None
            or self.session_expires_at > datetime.now(timezone.utc)
        )

    @property
    def cookie(self) -> str:
        """The kept session's cookie value, or nothing."""
        return parse_session(self.session)


def parse_session(session: str) -> str:
    """The sealed session's plaintext — JSON ``{"hss-global": …}`` — as a cookie value.

    Anything without the cookie is no session. The JSON shape (rather than the bare
    value) matches the LinkedIn session, and leaves room for a second cookie if
    Handshake ever needs one.
    """
    if not session:
        return ""
    try:
        data = json.loads(session)
    except ValueError:
        return ""
    if not isinstance(data, dict):
        return ""
    return str(data.get(SESSION_COOKIE) or "").strip()


def encode_session(cookie: str) -> str:
    """The plaintext the renewer seals: the session cookie as JSON."""
    return json.dumps({SESSION_COOKIE: cookie})


def session_expiry() -> datetime:
    return datetime.now(timezone.utc) + timedelta(days=SESSION_DAYS)


# -- reading the payloads ---------------------------------------------------


def jobs_from_search(payload: dict[str, Any]) -> tuple[list[dict[str, Any]], str, bool]:
    """``(jobs, end_cursor, has_next)`` from a ``JobSearchQuery`` response.

    A malformed or erroring payload is an empty page, not a crash: one bad response must
    not abort a tenant's poll.
    """
    search = (payload or {}).get("data", {}).get("jobSearch") or {}
    jobs: list[dict[str, Any]] = []
    for edge in search.get("edges") or []:
        job = ((edge or {}).get("node") or {}).get("job")
        if isinstance(job, dict) and str(job.get("id") or "").strip():
            jobs.append(job)
    page = search.get("pageInfo") or {}
    cursor = str(page.get("endCursor") or "").strip()
    return jobs, cursor, bool(page.get("hasNextPage"))


def external_url(payload: dict[str, Any]) -> str:
    """The employer's own posting from a ``JobApplySetting`` response, or ``""``.

    Only an external apply counts: a posting Handshake hosts itself has no employer URL
    to hand the agent, and staging it would produce a lead that cannot be filled.
    """
    job = (payload or {}).get("data", {}).get("job") or {}
    setting = job.get("jobApplySetting") or {}
    if str(setting.get("applyType") or "").upper() not in EXTERNAL_APPLY_TYPES:
        return ""
    for key in ("externalUrl", "alternativeExternalUrl"):
        url = str(setting.get(key) or "").strip()
        if url.startswith("http://") or url.startswith("https://"):
            return url
    return ""


def job_link(job: dict[str, Any]) -> str:
    """The Handshake posting's own URL — the listing's link, and its dedupe key."""
    return f"{JOB_URL}/{str(job.get('id') or '').strip()}"


def location_text(job: dict[str, Any]) -> str:
    """Where the posting is, as one line. A remote posting says so even with no city."""
    if job.get("remote"):
        return "Remote"
    places: list[str] = []
    for place in job.get("locations") or []:
        name = str((place or {}).get("displayName") or "").strip()
        if name and name not in places:
            places.append(name)
    if places:
        return " · ".join(places[:3])
    if job.get("hybrid"):
        return "Hybrid"
    return ""


def salary_text(job: dict[str, Any]) -> str:
    """The posting's pay as one line, or ``""`` when it names none."""
    rng = job.get("salaryRange") or {}
    low, high = rng.get("min"), rng.get("max")
    if low is None and high is None:
        return ""
    currency = str(rng.get("currency") or "").strip()
    period = str(((rng.get("paySchedule") or {}).get("friendlyName")) or "").strip()

    def amount(value: object) -> str:
        if value is None:
            return ""
        try:
            number = float(value)  # type: ignore[arg-type]
        except (TypeError, ValueError):
            return str(value)
        return f"{number:,.0f}" if number >= 1000 else f"{number:,.2f}".rstrip("0").rstrip(".")

    if low is not None and high is not None and amount(low) != amount(high):
        body = f"{amount(low)}–{amount(high)}"
    else:
        body = amount(low if low is not None else high)
    parts = [p for p in (currency, body) if p]
    text = " ".join(parts)
    return f"{text} {period.lower()}" if period else text


def listing_from_job(job: dict[str, Any], employer_link: str) -> Listing:
    """A search result whose Apply leads to ``employer_link`` as the poller's :class:`Listing`.

    The Handshake posting stays the listing's link (the dedupe key, and an aggregator
    link the #191 rule resolves); the employer's URL rides as ``apply_link`` and is what
    gets stored — the same split LinkedIn's offsite postings use.
    """
    return Listing(
        company=str(((job.get("employer") or {}).get("name")) or "").strip(),
        role=str(job.get("title") or "").strip(),
        link=job_link(job),
        location=location_text(job),
        salary=salary_text(job),
        source=SOURCE,
        description=str(job.get("description") or ""),
        apply_link=employer_link,
    )


def queries_for(keywords: Iterable[str]) -> list[str]:
    """The keyword searches worth a page each: the tenant's keywords, de-duped and capped."""
    out: list[str] = []
    seen: set[str] = set()
    for keyword in keywords:
        query = " ".join(str(keyword).split()).strip()
        if query and query.lower() not in seen:
            seen.add(query.lower())
            out.append(query)
    return out


# -- the client -------------------------------------------------------------


class Client:
    """One tenant's view of Handshake's job search over an ``httpx.Client``."""

    def __init__(
        self,
        client: httpx.Client,
        account: HandshakeAccount | None = None,
        *,
        pace: Callable[[float], None] = time.sleep,
    ) -> None:
        self._client = client
        self._pace = pace
        self._cookie = account.cookie if account else ""
        if self._cookie:
            client.cookies.set(SESSION_COOKIE, self._cookie, domain=".joinhandshake.com")

    @property
    def signed_in(self) -> bool:
        return bool(self._cookie)

    def _post(self, operation: str, query: str, variables: dict[str, Any]) -> dict[str, Any]:
        # The app names the operation; the endpoint is happy without it but this is what
        # the web client sends, and it is what makes a failed call identifiable.
        r = self._client.post(
            GRAPHQL_URL,
            json={"operationName": operation, "query": query, "variables": variables},
            headers={
                "content-type": "application/json",
                "accept": "application/json",
                "referer": BASE + "/job-search",
                "origin": BASE,
            },
        )
        self._check(r)
        try:
            return r.json()  # type: ignore[no-any-return]
        except ValueError as exc:
            raise NeedsSignIn("Handshake answered with a page, not JSON") from exc

    def _check(self, r: httpx.Response) -> None:
        if r.status_code == 429:
            raise RateLimited("Handshake answered 429")
        # A signed-in call that lands on the sign-in surface is the session's end.
        if r.status_code in (401, 403) or (
            r.status_code in (301, 302, 303, 307, 308)
            and "/access" in r.headers.get("location", "")
        ):
            raise NeedsSignIn(f"Handshake answered {r.status_code}")
        if r.status_code in (301, 302, 303, 307, 308):
            raise NeedsSignIn("Handshake redirected the request")
        r.raise_for_status()

    def search(self, query: str, *, after: str = "MA==", first: int = PAGE_SIZE) -> dict[str, Any]:
        """One page of the signed-in job search for ``query``, newest first."""
        return self._post(
            "JobSearchQuery",
            SEARCH_QUERY,
            {
                "first": first,
                "after": after,
                "input": {
                    "filter": {"query": query} if query else {},
                    "sort": {"direction": "DESC", "field": "POST_DATE"},
                    "channel": CHANNEL,
                },
            },
        )

    def apply_setting(self, job_id: str) -> dict[str, Any]:
        """How the posting is applied to, and the employer's URL when there is one."""
        return self._post("JobApplySetting", APPLY_QUERY, {"id": job_id})


def fetch_listings(
    client: Client,
    keywords: Iterable[str],
    *,
    limit: int,
    already_seen: Callable[[str], bool] = lambda _url: False,
    remember: Callable[[str], None] = lambda _url: None,
) -> list[Listing]:
    """Search each keyword, then read the employer's URL for every new posting that names
    one. A posting Handshake hosts itself is remembered through ``remember`` and dropped;
    ``already_seen`` (by the Handshake posting URL) keeps a listing the ledger knows from
    costing another call. A 429 ends the poll with what was gathered.

    The keyword check runs on the title before the detail call, so a page of 25 costs 25
    searches' worth of one response and only the matching postings cost a second call.
    """
    keyword_list = list(keywords)
    out: list[Listing] = []
    done: set[str] = set()
    try:
        for query in queries_for(keyword_list):
            after = "MA=="
            for _ in range(MAX_PAGES):
                page = client.search(query, after=after)
                jobs, cursor, has_next = jobs_from_search(page)
                if not jobs:
                    break
                for job in jobs:
                    job_id = str(job.get("id") or "").strip()
                    if not job_id or job_id in done or already_seen(job_link(job)):
                        continue
                    done.add(job_id)
                    if not classify(str(job.get("title") or ""), "", keyword_list)[1]:
                        continue
                    client._pace(PACE_SECONDS)
                    employer_link = external_url(client.apply_setting(job_id))
                    if not employer_link:
                        logger.info(
                            "handshake: %s — %s applies on Handshake itself; skipped",
                            (job.get("employer") or {}).get("name"),
                            job.get("title"),
                        )
                        remember(job_link(job))
                        continue
                    out.append(listing_from_job(job, employer_link))
                    if len(out) >= limit * 3:
                        return out
                if not has_next or not cursor:
                    break
                after = cursor
                client._pace(PACE_SECONDS)
    except RateLimited:
        logger.warning(
            "handshake: rate-limited (429) after %d listings; the rest waits for the next poll",
            len(out),
        )
    return out
