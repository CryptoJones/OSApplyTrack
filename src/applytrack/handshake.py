# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Handshake as a discovery source (#267) — campus recruiting, signed in.

Handshake is where US colleges run their campus recruiting. Its official API is not
usable here: the EDU API is read-only, scoped to one institution, and gated to Career
Services partners. So this takes the route the LinkedIn source takes (#233) — sign in
with the candidate's own account and call the endpoint Handshake's own web app calls
(``POST /hs/graphql``).

**One query does the whole job.** ``job.jobApplySetting`` is selectable on the search
result itself, so the search returns how each posting is applied to alongside its copy:
``externalUrl`` is the employer's own posting — the ATS Handshake tracked it from
(iCIMS, BambooHR, …) — and that becomes the lead's ``apply_link`` under the #191 rule.
A posting with no employer URL applies inside Handshake and is staged with the Handshake
posting as its link and **no** ``apply_link``, which is the poller's apply-by-hand lane.
Those are the postings that exist nowhere else — the school-scoped ones this source is
for — so they are staged rather than discarded.

**The session is one cookie.** ``hss-global`` (httpOnly, ~90 days) is the whole session;
a GraphQL call carrying only that cookie answers as the browser does. The .NET agent's
browser signs in with the board account saved for ``joinhandshake.com`` and seals it.

Every request is the tenant's own; nothing here is shared across tenants.
"""

from __future__ import annotations

import json
import logging
import time
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from datetime import datetime, timezone
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

GRAPHQL_URL = BASE + "/hs/graphql"
JOB_URL = BASE + "/jobs"

#: The app's own page size.
PAGE_SIZE = 25
#: How deep one keyword is walked. A board this size has more than any poll needs.
MAX_PAGES = 4
#: How many of the tenant's keywords are worth a search. LinkedIn caps the same way;
#: without it the request budget is whatever the operator typed into the keyword box.
MAX_KEYWORD_QUERIES = 8

#: One request at a time, politely. Handshake is a third party and this is a poll.
PACE_SECONDS = 0.5

#: The channel the web app searches on.
CHANNEL = "NL_SEARCH_CHANNEL"

#: The app's own search document, trimmed to the fields a Listing needs and the one
#: that decides the employer link. GraphQL lets the client choose the fields, so a
#: field this does not ask for cannot break the walk by being renamed.
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
          remote
          locations { displayName }
          salaryRange { min max currency paySchedule { friendlyName } }
          employer { name }
          jobApplySetting { applyType externalApplyType externalUrl alternativeExternalUrl }
        }
      }
    }
  }
}
""".strip()


class NeedsSignIn(Exception):
    """Handshake bounced the kept session: it has to be renewed through the browser."""


class RateLimited(Exception):
    """Handshake answered 429; the rest of the poll waits for the next pass."""


class SchemaError(Exception):
    """Handshake rejected the query document — almost always a schema change.

    Deliberately distinct from an empty result: a source that quietly returns no leads
    forever is the hardest kind of break for an operator to notice.
    """


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

    The shape is the cross-runtime contract with the renewer's ``Encode``; anything
    without the cookie is no session.
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


# -- reading the payloads ---------------------------------------------------


def jobs_from_search(payload: dict[str, Any]) -> tuple[list[dict[str, Any]], str, bool]:
    """``(jobs, end_cursor, has_next)`` from a ``JobSearchQuery`` response.

    A thin or erroring payload is an empty page, not a crash — ``data`` is present and
    null on a GraphQL error, which is not the same as absent.
    """
    data = (payload or {}).get("data") or {}
    search = data.get("jobSearch") or {}
    jobs: list[dict[str, Any]] = []
    for edge in search.get("edges") or []:
        job = ((edge or {}).get("node") or {}).get("job")
        if isinstance(job, dict) and str(job.get("id") or "").strip():
            jobs.append(job)
    page = search.get("pageInfo") or {}
    cursor = str(page.get("endCursor") or "").strip()
    return jobs, cursor, bool(page.get("hasNextPage"))


def total_count(payload: dict[str, Any]) -> int:
    """How many postings the search matched — the tripwire for a silent schema break."""
    data = (payload or {}).get("data") or {}
    search = data.get("jobSearch") or {}
    try:
        return int(search.get("totalCount") or 0)
    except (TypeError, ValueError):
        return 0


def employer_url(job: dict[str, Any]) -> str:
    """The employer's own posting, or ``""`` when the apply happens inside Handshake.

    The URL is the evidence, not ``applyType``: an unrecognized type that still carries
    an employer URL is the employer's posting, and only "no URL at all" means Handshake
    hosts the apply. Whitelisting the enum would silently discard every posting whose
    type Handshake adds later.
    """
    setting = job.get("jobApplySetting") or {}
    for key in ("externalUrl", "alternativeExternalUrl"):
        url = str(setting.get(key) or "").strip()
        if url.startswith("http://") or url.startswith("https://"):
            return url
    return ""


def apply_type(job: dict[str, Any]) -> str:
    """The posting's apply type, upper-cased, for the log line only."""
    return str((job.get("jobApplySetting") or {}).get("applyType") or "").strip().upper()


def job_link(job: dict[str, Any]) -> str:
    """The Handshake posting's own URL — the listing's link, and its dedupe key."""
    return f"{JOB_URL}/{str(job.get('id') or '').strip()}"


def location_text(job: dict[str, Any]) -> str:
    """Where the posting is, as one line. A remote posting keeps its anchor city."""
    places: list[str] = []
    for place in job.get("locations") or []:
        name = str((place or {}).get("displayName") or "").strip()
        if name and name not in places:
            places.append(name)
    where = " · ".join(places[:3])
    if job.get("remote"):
        return f"Remote · {where}" if where else "Remote"
    return where


def salary_text(job: dict[str, Any]) -> str:
    """The posting's pay as one line, or ``""`` when it names none.

    ``salaryRange`` reports **minor units**: a security officer's hourly rate arrives as
    ``1751`` for $17.51, and a service manager's salary as ``9000000`` for $90,000.
    Rendering the raw number would overstate every posting by a factor of a hundred.
    """
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
            number = float(value) / 100  # type: ignore[arg-type]
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


def listing_from_job(job: dict[str, Any]) -> Listing:
    """A search result as the poller's :class:`Listing`.

    The Handshake posting is the link (the dedupe key, and an aggregator link the #191
    rule resolves). The employer's URL, when there is one, rides as ``apply_link`` and
    is what gets stored; when there is not, the lead is apply-by-hand.
    """
    return Listing(
        company=str(((job.get("employer") or {}).get("name")) or "").strip(),
        role=str(job.get("title") or "").strip(),
        link=job_link(job),
        location=location_text(job),
        salary=salary_text(job),
        source=SOURCE,
        description=str(job.get("description") or ""),
        apply_link=employer_url(job),
    )


def queries_for(keywords: Iterable[str]) -> list[str]:
    """The keyword searches worth a page each: the tenant's keywords, de-duped and
    capped at :data:`MAX_KEYWORD_QUERIES` so the request budget is bounded."""
    out: list[str] = []
    seen: set[str] = set()
    for keyword in keywords:
        query = " ".join(str(keyword).split()).strip()
        if query and query.lower() not in seen:
            seen.add(query.lower())
            out.append(query)
        if len(out) >= MAX_KEYWORD_QUERIES:
            break
    return out


# -- the client -------------------------------------------------------------


class Client:
    """One tenant's view of Handshake's job search over an ``httpx.Client``.

    The session travels as a request header rather than in the shared cookie jar: the
    cookie is a property of this tenant's call, not of a client the poller may reuse.
    """

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

    @property
    def signed_in(self) -> bool:
        return bool(self._cookie)

    def _post(self, operation: str, query: str, variables: dict[str, Any]) -> dict[str, Any]:
        self._pace(PACE_SECONDS)
        headers = {
            "content-type": "application/json",
            "accept": "application/json",
            "referer": BASE + "/job-search",
            "origin": BASE,
        }
        if self._cookie:
            headers["cookie"] = f"{SESSION_COOKIE}={self._cookie}"
        r = self._client.post(
            GRAPHQL_URL,
            json={"operationName": operation, "query": query, "variables": variables},
            headers=headers,
        )
        self._check(r)
        try:
            payload = r.json()
        except ValueError as exc:
            raise NeedsSignIn("Handshake answered with a page, not JSON") from exc
        # A GraphQL error is a 200 with an `errors` array, not an HTTP failure: read it,
        # or a renamed field looks exactly like a board with no jobs.
        errors = (payload or {}).get("errors") if isinstance(payload, dict) else None
        if errors:
            message = str((errors[0] or {}).get("message") or "unknown error")[:200]
            if _looks_like_auth(message):
                raise NeedsSignIn(f"Handshake rejected the session: {message}")
            raise SchemaError(f"Handshake rejected the {operation} document: {message}")
        return payload  # type: ignore[no-any-return]

    def _check(self, r: httpx.Response) -> None:
        if r.status_code == 429:
            raise RateLimited("Handshake answered 429")
        # A stale cookie is a clean 401 with an empty body — verified against the live
        # site, not assumed. A redirect to the sign-in surface is the same news.
        if r.status_code in (401, 403) or (
            r.status_code in (301, 302, 303, 307, 308)
            and "/access" in r.headers.get("location", "")
        ):
            raise NeedsSignIn(f"Handshake answered {r.status_code}")
        if r.status_code in (301, 302, 303, 307, 308):
            raise NeedsSignIn("Handshake redirected the request")
        r.raise_for_status()

    def search(
        self, query: str, *, after: str | None = None, first: int = PAGE_SIZE
    ) -> dict[str, Any]:
        """One page of the signed-in job search for ``query``, newest first.

        ``after`` is the page cursor; ``None`` asks for the first page, which the endpoint
        accepts — no assumption about how a cursor is encoded.
        """
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


def _looks_like_auth(message: str) -> bool:
    lowered = message.lower()
    return any(word in lowered for word in ("unauthor", "not signed in", "forbidden", "session"))


def fetch_listings(
    client: Client,
    keywords: Iterable[str],
    *,
    limit: int,
    already_seen: Callable[[str], bool] = lambda _url: False,
) -> list[Listing]:
    """Search each keyword and stage what matches, newest first.

    The employer's URL, when the posting has one, becomes the lead's ``apply_link``; a
    posting that applies inside Handshake is staged with the Handshake posting as its
    link and no ``apply_link``, which is the poller's apply-by-hand lane. Nothing is
    written to the ledger here — a skip is a decision that can change, and the ledger is
    for a URL processed under a rule that will not.
    """
    keyword_list = list(keywords)
    out: list[Listing] = []
    done: set[str] = set()
    hosted = 0
    try:
        for query in queries_for(keyword_list):
            after: str | None = None
            for _ in range(MAX_PAGES):
                page = client.search(query, after=after)
                jobs, cursor, has_next = jobs_from_search(page)
                if not jobs:
                    # A positive count with nothing to show is a schema break wearing a
                    # "no jobs" costume; say so rather than returning an empty poll.
                    if total_count(page) > 0:
                        logger.warning(
                            "handshake: %r matched %d postings but none parsed — the search "
                            "document may no longer match Handshake's schema",
                            query, total_count(page),
                        )
                    break
                for job in jobs:
                    job_id = str(job.get("id") or "").strip()
                    if not job_id or job_id in done:
                        continue
                    done.add(job_id)
                    if already_seen(job_link(job)):
                        continue
                    if not classify(
                        str(job.get("title") or ""), str(job.get("description") or ""), keyword_list
                    )[1]:
                        continue
                    if not employer_url(job):
                        hosted += 1
                    out.append(listing_from_job(job))
                    if len(out) >= limit * 3:
                        return out
                if not has_next or not cursor:
                    break
                after = cursor
    except RateLimited:
        logger.warning(
            "handshake: rate-limited (429) after %d listings; the rest waits for the next poll",
            len(out),
        )
    except httpx.HTTPError as exc:
        # A 500 or a read timeout must not throw away what was already gathered.
        logger.warning(
            "handshake: %s after %d listings; the rest waits for the next poll",
            type(exc).__name__, len(out),
        )
    if hosted:
        logger.info(
            "handshake: %d of %d staged leads apply inside Handshake (apply by hand)",
            hosted, len(out),
        )
    return out
