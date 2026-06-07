# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Shared HTTP link probing.

One browser-identifying User-Agent and one verdict function, used by both the
poller (to block dead leads before they become entries) and the web UI (to
investigate a suspect link on demand).
"""

from __future__ import annotations

from dataclasses import dataclass
from urllib.parse import urlsplit

import httpx

# Identify as a current Windows 11 desktop Chrome. Windows 11 still reports
# "Windows NT 10.0" in its UA string -- there is no "NT 11.0" -- so this is the
# correct token, not a bug. Bump the Chrome version here to match a real
# browser; the structure matters more than the exact number for getting past
# naive bot filters.
BROWSER_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36"
)
BROWSER_HEADERS = {
    "User-Agent": BROWSER_UA,
    "Accept": (
        "text/html,application/xhtml+xml,application/xml;q=0.9,"
        "image/avif,image/webp,*/*;q=0.8"
    ),
    "Accept-Language": "en-US,en;q=0.9",
}


@dataclass
class LinkStatus:
    """The outcome of probing a single URL."""

    url: str
    ok: bool
    status_code: int | None = None
    final_url: str = ""
    redirected_to_home: bool = False
    generic_listing: bool = False
    error: str = ""

    def summary(self) -> str:
        if self.error:
            return f"unreachable — {self.error}"
        if self.redirected_to_home:
            return f"listing gone — redirected to the site homepage (HTTP {self.status_code})"
        if self.generic_listing:
            return (
                "not a specific posting — looks like a careers/search "
                f"page (HTTP {self.status_code})"
            )
        if self.ok:
            return f"live — HTTP {self.status_code}"
        return f"dead — HTTP {self.status_code}"


def _is_home(url: str) -> bool:
    """True when a URL points at a bare host root (path empty or just '/')."""
    return urlsplit(url).path.strip("/") == ""


# When one of these is the last meaningful path segment, the URL is almost
# certainly a careers landing/search index rather than a single job posting.
# A real posting nearly always ends in something listing-specific: a numeric
# req id or a long hyphenated slug.
_INDEX_LEAVES = frozenset({
    "careers", "career", "jobs", "job", "join-us", "join", "work-with-us",
    "opportunities", "openings", "vacancies", "positions", "search", "all-jobs",
})
# Query keys that carry a specific job id — these rescue an otherwise-generic
# path such as ".../careers?gh_jid=12345".
_JOB_QUERY_KEYS = ("jid", "jobid", "job_id", "gh_jid", "ashby_jid", "lever", "id=")


def _looks_like_index(url: str) -> bool:
    """True when ``url`` resolves to a careers index/search page, not one job."""
    parts = urlsplit(url)
    segs = [s for s in parts.path.split("/") if s]
    if not segs or segs[-1].lower() not in _INDEX_LEAVES:
        return False
    query = parts.query.lower()
    return not any(key in query for key in _JOB_QUERY_KEYS)


def probe(
    url: str, *, client: httpx.Client | None = None, timeout: float = 12.0
) -> LinkStatus:
    """Fetch ``url`` as a browser and judge whether it still resolves to a page.

    A link is considered *not ok* when it is malformed, the connection fails, the
    server answers 4xx/5xx, or a deep URL redirects to the site's homepage -- the
    classic "this listing was pulled" signal. Redirects are followed; the
    verdict is about the final response.
    """
    url = (url or "").strip()
    parts = urlsplit(url)
    if parts.scheme not in ("http", "https"):
        return LinkStatus(url=url, ok=False, error="not an http(s) URL")

    owns_client = client is None
    client = client or httpx.Client(
        timeout=timeout, follow_redirects=True, headers=BROWSER_HEADERS
    )
    try:
        try:
            resp = client.get(url)
        except httpx.HTTPError as exc:
            return LinkStatus(url=url, ok=False, error=type(exc).__name__)
        final_url = str(resp.url)
        started_deep = parts.path.strip("/") != ""
        to_home = started_deep and _is_home(final_url)
        generic = not to_home and _looks_like_index(final_url)
        ok = 200 <= resp.status_code < 400 and not to_home and not generic
        return LinkStatus(
            url=url,
            ok=ok,
            status_code=resp.status_code,
            final_url=final_url,
            redirected_to_home=to_home,
            generic_listing=generic,
        )
    finally:
        if owns_client:
            client.close()


def is_reachable(url: str, *, client: httpx.Client | None = None) -> bool:
    """Convenience wrapper: ``True`` when :func:`probe` deems ``url`` ok."""
    return probe(url, client=client).ok
