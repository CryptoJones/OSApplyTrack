# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Shared HTTP link probing.

One browser-identifying User-Agent and one verdict function, used by both the
poller (to block dead leads before they become entries) and the web UI (to
investigate a suspect link on demand).
"""

from __future__ import annotations

import ipaddress
import socket
from dataclasses import dataclass
from urllib.parse import urljoin, urlsplit

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


# How many redirect hops to walk before giving up. We follow them by hand (rather
# than letting httpx auto-follow) so every hop's host can be SSRF-checked first.
_MAX_REDIRECTS = 5


def _ip_is_public(ip: str) -> bool:
    """True only for a globally-routable unicast address.

    Rejects loopback, RFC-1918/ULA private, link-local (incl. the
    169.254.169.254 cloud-metadata endpoint), reserved, multicast, and the
    unspecified address. IPv4-mapped IPv6 is unwrapped so ``::ffff:127.0.0.1``
    can't sneak a loopback past the check.
    """
    try:
        addr = ipaddress.ip_address(ip)
    except ValueError:
        return False
    if isinstance(addr, ipaddress.IPv6Address) and addr.ipv4_mapped is not None:
        addr = addr.ipv4_mapped
    return not (
        addr.is_private
        or addr.is_loopback
        or addr.is_link_local
        or addr.is_reserved
        or addr.is_multicast
        or addr.is_unspecified
    )


def _host_is_public(host: str) -> bool:
    """True only when every address ``host`` resolves to is a public IP.

    The SSRF gate: a lead URL (or a redirect it chains to) is attacker-influenced,
    and the poller fetches it server-side, so a link pointing at ``localhost`` or
    an internal/metadata IP must be refused. An IP literal is checked directly; a
    name is resolved and *all* of its A/AAAA records must be public, so a public
    hostname that maps to an internal address is still rejected. (A determined
    attacker could still race DNS between this check and the connect; closing that
    fully needs connect-by-IP, out of scope for v1's self-host threat model.)
    """
    host = (host or "").strip().strip("[]")  # tolerate bracketed IPv6 literals
    if not host:
        return False
    try:
        ipaddress.ip_address(host)
        return _ip_is_public(host)
    except ValueError:
        pass  # not a literal — resolve it below
    try:
        infos = socket.getaddrinfo(host, None)
    except socket.gaierror:
        return False
    return bool(infos) and all(_ip_is_public(str(info[4][0])) for info in infos)


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
    classic "this listing was pulled" signal. Redirects are followed by hand so
    each hop is SSRF-checked before we connect; the verdict is about the final
    response.
    """
    url = (url or "").strip()
    parts = urlsplit(url)
    if parts.scheme not in ("http", "https"):
        return LinkStatus(url=url, ok=False, error="not an http(s) URL")
    if not _host_is_public(parts.hostname or ""):
        return LinkStatus(url=url, ok=False, error="refused non-public address")

    owns_client = client is None
    client = client or httpx.Client(
        timeout=timeout, follow_redirects=False, headers=BROWSER_HEADERS
    )
    try:
        current = url
        try:
            for _ in range(_MAX_REDIRECTS + 1):
                # follow_redirects=False on the call overrides whatever default the
                # caller's shared client carries, so the per-hop guard can't be
                # bypassed by an auto-following client.
                resp = client.get(current, follow_redirects=False)
                if not (resp.is_redirect and "location" in resp.headers):
                    break
                current = urljoin(current, resp.headers["location"])
                hop = urlsplit(current)
                if hop.scheme not in ("http", "https"):
                    return LinkStatus(url=url, ok=False, error="redirect to non-http(s) URL")
                if not _host_is_public(hop.hostname or ""):
                    return LinkStatus(url=url, ok=False, error="redirect to non-public address")
            else:
                return LinkStatus(url=url, ok=False, error="too many redirects")
        except httpx.HTTPError as exc:
            return LinkStatus(url=url, ok=False, error=type(exc).__name__)
        final_url = current
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
