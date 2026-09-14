# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""MyGreenhouse — Greenhouse's candidate portal — as a discovery source (#218).

Signed in, ``my.greenhouse.io/jobs/search`` lists the newest postings across every
Greenhouse board, searchable and filterable. Sign-in is passwordless: the portal
emails a security code to the candidate's address. The .NET agent's browser does
that sign-in (#221) — the portal never sends the mail for a plain client's request —
and keeps the fortnight-long session cookie sealed on the tenant's board-account
row; this searches with it, and clears it when the portal bounces it.

Everything the portal returns links to the employer's own Greenhouse board
(``job-boards.greenhouse.io/{token}/jobs/{id}``), which the packet builder already
reads through the Job Board API.
"""

from __future__ import annotations

import html
import json
import logging
import re
import urllib.parse
from collections.abc import Iterable
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any

import httpx

from applytrack.poll import Listing

logger = logging.getLogger(__name__)

BASE = "https://my.greenhouse.io"
SOURCE = "mygreenhouse"
#: Board-account hosts that mean the candidate's MyGreenhouse sign-in.
ACCOUNT_HOSTS = ("greenhouse.io", "my.greenhouse.io")
#: How long a fresh session is trusted before it is re-checked against the portal.
SESSION_DAYS = 13
#: Queries per poll from the tenant's keywords (one page each), on top of the
#: unfiltered newest-postings pages.
MAX_KEYWORD_QUERIES = 10
BROWSE_PAGES = 2

_CSRF_RE = re.compile(r'<meta\s+name="csrf-token"\s+content="([^"]+)"', re.IGNORECASE)
_PAGE_RE = re.compile(r'data-page="([^"]+)"')


class NeedsSignIn(Exception):
    """The portal did not accept the session."""


@dataclass
class PortalAccount:
    """The tenant's MyGreenhouse sign-in and the session kept for it."""

    email: str
    session: str = ""
    session_expires_at: datetime | None = None

    @property
    def session_fresh(self) -> bool:
        return bool(self.session) and (
            self.session_expires_at is None or self.session_expires_at > datetime.now(timezone.utc)
        )


class Portal:
    """One tenant's signed-in view of MyGreenhouse over an ``httpx.Client``."""

    def __init__(self, client: httpx.Client, account: PortalAccount) -> None:
        self._client = client
        self._account = account
        self._csrf = ""
        self._version = ""
        if account.session:
            client.cookies.set("_session_id", account.session, domain="my.greenhouse.io")

    @property
    def session(self) -> str:
        return self._client.cookies.get("_session_id", domain="my.greenhouse.io") or ""

    def _page(self, path: str) -> None:
        """Read the CSRF token and the Inertia version off a rendered page."""
        # Followed: signed in, the portal bounces to the last search it remembers.
        r = self._client.get(
            BASE + path,
            headers={"Accept": "text/html,application/xhtml+xml"},
            follow_redirects=True,
        )
        r.raise_for_status()
        # The CSRF token is the XSRF-TOKEN cookie (URL-encoded), the way the portal's own
        # script reads it; a csrf-token meta tag, when there is one, says the same.
        m = _CSRF_RE.search(r.text)
        cookie = (
            self._client.cookies.get("MYGREENHOUSE-XSRF-TOKEN", domain="my.greenhouse.io")
            or self._client.cookies.get("XSRF-TOKEN", domain="my.greenhouse.io")
            or ""
        )
        self._csrf = m.group(1) if m else urllib.parse.unquote(cookie)
        m = _PAGE_RE.search(r.text)
        if m:
            try:
                self._version = str(json.loads(html.unescape(m.group(1))).get("version", ""))
            except (ValueError, AttributeError):
                self._version = ""

    def _inertia_headers(self, **partial: str) -> dict[str, str]:
        h = {
            "Accept": "text/html, application/xhtml+xml",
            "X-Requested-With": "XMLHttpRequest",
            "X-Inertia": "true",
            "X-Inertia-Version": self._version,
            # A browser sends these on every same-site request; the sign-in post is
            # silently ignored without them.
            "Origin": BASE,
            "Referer": BASE + "/users/sign_in",
        }
        if self._csrf:
            h["X-CSRF-Token"] = self._csrf
            h["X-XSRF-TOKEN"] = self._csrf
        h.update(partial)
        return h

    def search(
        self, query: str, *, page: int = 1, remote_only: bool = False
    ) -> tuple[list[dict[str, Any]], bool]:
        """One page of the portal's job search: the raw posts and whether more follow."""
        if not self._version:
            # Signed in, the sign-in page redirects; the search page renders for anyone.
            self._page("/jobs/search")
        pairs: list[tuple[str, str | int | float | bool | None]] = [("page", str(page))]
        if query:
            pairs.insert(0, ("query", query))
        if remote_only:
            pairs.append(("work_type[]", "remote"))
        params = httpx.QueryParams(pairs)
        headers = self._inertia_headers(
            **{
                "X-Inertia-Partial-Component": "jobs",
                "X-Inertia-Partial-Data": "jobPosts,moreResultsAvailable,page",
            }
        )
        r = self._client.get(BASE + "/jobs/search", params=params, headers=headers)
        if r.status_code == 409:  # the app shipped a new build: take its version and retry once
            self._page("/jobs/search")
            headers["X-Inertia-Version"] = self._version
            r = self._client.get(BASE + "/jobs/search", params=params, headers=headers)
        # Redirects are not followed: a bounce to the sign-in page is the session's end.
        if r.status_code in (301, 302, 303, 307, 308) or r.status_code in (401, 403):
            raise NeedsSignIn("the session was not accepted")
        r.raise_for_status()
        try:
            data = r.json()
        except ValueError as exc:
            raise NeedsSignIn("the portal answered with a page, not the search") from exc
        props = data.get("props") or {}
        posts = props.get("jobPosts") or []
        if not isinstance(posts, list):
            posts = []
        return posts, bool(props.get("moreResultsAvailable"))


def to_listing(post: dict[str, Any]) -> Listing | None:
    """A portal job post as the poller's :class:`Listing`, or None when it has no link."""
    link = str(post.get("publicUrl") or "").strip()
    if not link:
        token, jid = post.get("urlToken"), post.get("id")
        if token and jid:
            link = f"https://job-boards.greenhouse.io/{token}/jobs/{jid}"
    if not link:
        return None
    locations = [str(loc).strip() for loc in (post.get("locations") or []) if str(loc).strip()]
    work = str(post.get("workType") or "").lower()
    location = ", ".join(locations)
    if work == "remote":
        location = f"{location} (Remote)" if location else "Remote"
    return Listing(
        company=str(post.get("companyName") or "").strip(),
        role=str(post.get("title") or "").strip(),
        link=link,
        location=location,
        salary=_salary(post.get("payRanges")),
        source=SOURCE,
    )


def _salary(ranges: object) -> str:
    if not isinstance(ranges, list) or not ranges:
        return ""
    first = ranges[0] if isinstance(ranges[0], dict) else {}
    lo, hi = first.get("minCents") or first.get("min"), first.get("maxCents") or first.get("max")
    cur = first.get("currencyType") or first.get("currency") or ""
    parts = []
    for v in (lo, hi):
        if isinstance(v, (int, float)) and v > 0:
            n = v / 100 if "Cents" in json.dumps(first) else v
            parts.append(f"{int(n):,}")
    return (f"{cur} " if cur else "") + " - ".join(parts) if parts else ""


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
    portal: Portal, keywords: Iterable[str], *, remote_only: bool, limit: int
) -> list[Listing]:
    """The newest postings (unfiltered), then one page per keyword; de-duped by link."""
    out: list[Listing] = []
    seen: set[str] = set()

    def take(posts: list[dict[str, Any]]) -> None:
        for post in posts:
            listing = to_listing(post)
            if listing is None or listing.link in seen:
                continue
            seen.add(listing.link)
            out.append(listing)

    for page in range(1, BROWSE_PAGES + 1):
        posts, more = portal.search("", page=page, remote_only=remote_only)
        take(posts)
        if not more:
            break
    for q in queries_for(keywords):
        posts, _ = portal.search(q, page=1, remote_only=remote_only)
        take(posts)
        if len(out) >= limit * 3:
            break
    return out[: max(limit * 3, limit)]


def session_expiry() -> datetime:
    return datetime.now(timezone.utc) + timedelta(days=SESSION_DAYS)
