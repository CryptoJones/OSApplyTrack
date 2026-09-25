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
import ssl
import time
import zlib
from collections.abc import Iterable, Iterator
from concurrent.futures import ThreadPoolExecutor
from concurrent.futures import TimeoutError as FutureTimeout
from contextlib import contextmanager
from dataclasses import dataclass
from typing import Any
from urllib.parse import SplitResult, urljoin, urlsplit

import httpcore
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


def _embedded_ipv4(addr: ipaddress.IPv6Address) -> list[ipaddress.IPv4Address]:
    """The IPv4 targets carried inside a transitional IPv6 address, if any.

    IPv4-mapped (``::ffff:0:0/96``), NAT64 (``64:ff9b::/96``), 6to4 (``2002::/16``),
    Teredo (``2001::/32`` -- both its server and its client) and IPv4-compatible
    (``::a.b.c.d``) all route to an IPv4 host, so that host is what gets judged --
    the same unwrapping as the API's ``JobPageFetcher.IsBlockedAddress`` (#339).
    """
    if addr.ipv4_mapped is not None:
        return [addr.ipv4_mapped]
    if addr.teredo is not None:
        return list(addr.teredo)
    if addr.sixtofour is not None:
        return [addr.sixtofour]
    packed = addr.packed
    if packed[:12] == bytes(12) and int(addr) > 1:
        return [ipaddress.IPv4Address(packed[12:])]  # IPv4-compatible, not :: or ::1
    if packed[:12] == b"\x00\x64\xff\x9b" + bytes(8):
        return [ipaddress.IPv4Address(packed[12:])]  # NAT64 well-known prefix
    return []


def _addr_is_public(addr: ipaddress.IPv4Address | ipaddress.IPv6Address) -> bool:
    if isinstance(addr, ipaddress.IPv6Address):
        embedded = _embedded_ipv4(addr)
        if embedded:
            return all(_addr_is_public(v4) for v4 in embedded)
    # is_global is the gate: it alone rejects CGNAT 100.64/10 (Tailscale's
    # 100.100.100.100 included), benchmarking, TEST-NETs and the rest of the IANA
    # special-purpose registry (#339). The explicit checks are belt and braces for
    # older interpreters whose registry is thinner.
    return addr.is_global and not (
        addr.is_private
        or addr.is_loopback
        or addr.is_link_local
        or addr.is_reserved
        or addr.is_multicast
        or addr.is_unspecified
    )


def _ip_is_public(ip: str) -> bool:
    """True only for a globally-routable unicast address.

    Rejects anything not ``is_global`` -- loopback, RFC-1918/ULA private, CGNAT,
    link-local (incl. the 169.254.169.254 cloud-metadata endpoint), reserved,
    multicast, unspecified -- after unwrapping every IPv6 form that embeds an IPv4
    target, so ``::ffff:127.0.0.1`` or ``64:ff9b::a9fe:a9fe`` can't sneak past.
    """
    try:
        addr = ipaddress.ip_address(ip)
    except ValueError:
        return False
    return _addr_is_public(addr)


def _normalize_host(host: str) -> str:
    """Canonical key shared by URL parsing, DNS pinning, and httpcore."""
    return (host or "").strip().strip("[]").rstrip(".").casefold()


# getaddrinfo has no timeout of its own; lookups made under a fetch deadline run
# here so the fetch can give up on a stalled resolver (#338). A lookup abandoned
# that way finishes on its own thread within the system resolver's timeout.
_DNS_POOL = ThreadPoolExecutor(max_workers=4, thread_name_prefix="linkcheck-dns")


def _getaddrinfo(host: str, timeout: float | None) -> list[str]:
    def lookup() -> list[str]:
        infos = socket.getaddrinfo(host, None, type=socket.SOCK_STREAM)
        return list(dict.fromkeys(str(info[4][0]) for info in infos))

    if timeout is None:
        return lookup()
    try:
        return _DNS_POOL.submit(lookup).result(timeout=max(timeout, 0.0))
    except FutureTimeout as exc:
        raise OSError("DNS lookup exceeded the fetch deadline") from exc


class _PinnedResolver:
    """Resolve each hostname once, reject mixed/private answers, and retain one IP."""

    def __init__(self) -> None:
        self._addresses: dict[str, str] = {}

    def pin(self, host: str, timeout: float | None = None) -> bool:
        key = _normalize_host(host)
        if not key:
            return False
        if key in self._addresses:
            return True

        try:
            ipaddress.ip_address(key)
            addresses = [key]
        except ValueError:
            try:
                addresses = _getaddrinfo(key, timeout)
            except OSError:
                return False

        if not addresses or not all(_ip_is_public(address) for address in addresses):
            return False
        self._addresses[key] = addresses[0]
        return True

    def address_for(self, host: str) -> str | None:
        """Return only an address previously accepted by :meth:`pin`."""
        return self._addresses.get(_normalize_host(host))


class _Deadline:
    """One wall-clock deadline shared by a client's sockets, armed per fetch (#338).

    httpx's ``timeout`` is per socket operation, so a server that trickles a byte
    every few seconds -- in the headers or the body -- never trips it. Every pinned
    socket read, write, connect and TLS handshake is clamped to what is left of
    this deadline instead, so one fetch can't outlive it however the server paces.
    """

    def __init__(self) -> None:
        self.at: float | None = None

    def clamp(self, timeout: float | None, exc: type[Exception]) -> float | None:
        if self.at is None:
            return timeout
        remaining = self.at - time.monotonic()
        if remaining <= 0:
            raise exc("fetch deadline exceeded")
        return remaining if timeout is None else min(timeout, remaining)


class _DeadlineStream(httpcore.NetworkStream):
    """A network stream whose every blocking call is bounded by a :class:`_Deadline`."""

    def __init__(self, inner: httpcore.NetworkStream, deadline: _Deadline) -> None:
        self._inner = inner
        self._deadline = deadline

    def read(self, max_bytes: int, timeout: float | None = None) -> bytes:
        return self._inner.read(max_bytes, self._deadline.clamp(timeout, httpcore.ReadTimeout))

    def write(self, buffer: bytes, timeout: float | None = None) -> None:
        self._inner.write(buffer, self._deadline.clamp(timeout, httpcore.WriteTimeout))

    def close(self) -> None:
        self._inner.close()

    def start_tls(
        self,
        ssl_context: ssl.SSLContext,
        server_hostname: str | None = None,
        timeout: float | None = None,
    ) -> httpcore.NetworkStream:
        inner = self._inner.start_tls(
            ssl_context, server_hostname, self._deadline.clamp(timeout, httpcore.ConnectTimeout)
        )
        return _DeadlineStream(inner, self._deadline)

    def get_extra_info(self, info: str) -> Any:
        return self._inner.get_extra_info(info)


class _PinnedNetworkBackend(httpcore.SyncBackend):
    """Connect httpcore's TCP socket to the pinned IP while preserving TLS SNI."""

    def __init__(self, resolver: _PinnedResolver, deadline: _Deadline | None = None) -> None:
        super().__init__()
        self._resolver = resolver
        self._deadline = deadline or _Deadline()

    def connect_tcp(
        self,
        host: str,
        port: int,
        timeout: float | None = None,
        local_address: str | None = None,
        socket_options: Iterable[httpcore.SOCKET_OPTION] | None = None,
    ) -> httpcore.NetworkStream:
        address = self._resolver.address_for(host)
        if address is None:
            raise httpcore.ConnectError(f"refused unpinned host: {host}")
        timeout = self._deadline.clamp(timeout, httpcore.ConnectTimeout)
        stream = super().connect_tcp(address, port, timeout, local_address, socket_options)
        return _DeadlineStream(stream, self._deadline)


class _PinnedTransport(httpx.HTTPTransport):
    """HTTPX transport whose connection pool cannot perform a second DNS lookup."""

    def __init__(self, resolver: _PinnedResolver, deadline: _Deadline) -> None:
        super().__init__(trust_env=False)
        self._pool.close()
        self._pool = httpcore.ConnectionPool(
            ssl_context=httpx.create_ssl_context(trust_env=False),
            network_backend=_PinnedNetworkBackend(resolver, deadline),
        )


class _PinnedClient(httpx.Client):
    """HTTP client that connects only to addresses pinned by its resolver."""

    def __init__(self, *, timeout: float) -> None:
        self._resolver = _PinnedResolver()
        self._deadline = _Deadline()
        super().__init__(
            timeout=timeout,
            follow_redirects=False,
            headers=BROWSER_HEADERS,
            transport=_PinnedTransport(self._resolver, self._deadline),
            trust_env=False,
        )

    def pin(self, host: str, timeout: float | None = None) -> bool:
        return self._resolver.pin(host, timeout)


def ssrf_safe_client(*, timeout: float = 12.0) -> httpx.Client:
    """Return a reusable client that pins every validated hostname to one public IP."""
    return _PinnedClient(timeout=timeout)


def _host_is_public(host: str) -> bool:
    """True only when every address ``host`` resolves to is a public IP.

    The SSRF gate: a lead URL (or a redirect it chains to) is attacker-influenced,
    and the poller fetches it server-side, so a link pointing at ``localhost`` or
    an internal/metadata IP must be refused. An IP literal is checked directly; a
    name is resolved and *all* of its A/AAAA records must be public. Production
    requests additionally use :func:`ssrf_safe_client`, whose transport connects to
    the selected validated IP without resolving the hostname again.
    """
    return _PinnedResolver().pin(host)


def _pin_for_request(client: httpx.Client, host: str, deadline_at: float | None = None) -> bool:
    """Fail closed when a caller supplies an ordinary client for a DNS hostname."""
    if isinstance(client, _PinnedClient):
        timeout = None if deadline_at is None else deadline_at - time.monotonic()
        return client.pin(host, timeout)
    normalized = _normalize_host(host)
    try:
        ipaddress.ip_address(normalized)
    except ValueError:
        return False
    return _ip_is_public(normalized)


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


# Wall-clock ceiling on one fetch or probe -- every redirect hop, the headers and
# the body together (#338), mirroring the API's scrape budget (#266). 15s is
# generous for one real page (a full-cap body needs ~140 KB/s) and still fails
# fast on a server that stalls or streams forever, so one hostile lead can't hold
# the poller -- and every tenant's advisory lock -- hostage.
FETCH_DEADLINE = 15.0


@contextmanager
def _deadline(client: httpx.Client, seconds: float) -> Iterator[float]:
    """Arm a wall-clock deadline for one fetch; yields its ``time.monotonic()`` value.

    A pinned client also clamps every socket operation to it; an injected ordinary
    client is still bounded between redirect hops and body chunks.
    """
    at = time.monotonic() + seconds
    if not isinstance(client, _PinnedClient):
        yield at
        return
    previous, client._deadline.at = client._deadline.at, at
    try:
        yield at
    finally:
        client._deadline.at = previous


def _standard_port(parts: SplitResult) -> bool:
    """True when a URL names no port, or 80/443 -- where a redirect may send us.

    A tenant's feed URL is already held to a default port (``criteria._clean_feeds``);
    a redirect hop is held to the web ports too, so a public host can't bounce the
    poller onto some other service's port (#339).
    """
    try:
        return parts.port in (None, 80, 443)
    except ValueError:
        return False


def _walk(
    url: str,
    client: httpx.Client,
    deadline_at: float,
    headers: dict[str, str] | None = None,
) -> tuple[httpx.Response | None, str, str]:
    """Follow up to :data:`_MAX_REDIRECTS` hops, SSRF-checking every one.

    Returns ``(response, final_url, error)``; ``response`` is ``None`` exactly when
    ``error`` is non-empty. Redirects are walked by hand rather than by httpx so
    each hop's host is validated *before* we connect to it — the shared core of
    :func:`probe` and :func:`fetch_public`. The response is *streamed*: its body
    has not been read, and the caller must close it (#338).
    """
    parts = urlsplit(url)
    if parts.scheme not in ("http", "https"):
        return None, url, "not an http(s) URL"
    if not _pin_for_request(client, parts.hostname or "", deadline_at):
        if time.monotonic() >= deadline_at:
            return None, url, "deadline exceeded"
        return None, url, "refused non-public or unpinned address"

    current = url
    try:
        for _ in range(_MAX_REDIRECTS + 1):
            if time.monotonic() >= deadline_at:
                return None, current, "deadline exceeded"
            # follow_redirects=False on the call overrides whatever default the
            # caller's shared client carries, so the per-hop guard can't be
            # bypassed by an auto-following client.
            resp = client.send(
                client.build_request("GET", current, headers=headers),
                stream=True,
                follow_redirects=False,
            )
            if not (resp.is_redirect and "location" in resp.headers):
                return resp, current, ""
            location = resp.headers["location"]
            resp.close()
            current = urljoin(current, location)
            hop = urlsplit(current)
            if hop.scheme not in ("http", "https"):
                return None, current, "redirect to non-http(s) URL"
            if not _standard_port(hop):
                return None, current, "redirect to non-standard port"
            if not _pin_for_request(client, hop.hostname or "", deadline_at):
                if time.monotonic() >= deadline_at:
                    return None, current, "deadline exceeded"
                return None, current, "redirect to non-public or unpinned address"
        return None, current, "too many redirects"
    except httpx.HTTPError as exc:
        return None, current, type(exc).__name__


class PublicFetchError(RuntimeError):
    """A server-side fetch of a user-supplied URL was refused or failed."""


# Ceiling on a fetched document. Big enough for any real job feed, small enough
# that a hostile or runaway URL can't exhaust the poller's memory.
MAX_FETCH_BYTES = 2 * 1024 * 1024


# The only encodings fetch_public accepts, and it asks for no others: each is
# inflated here in bounded steps, because httpx's own decoder inflates a whole
# network chunk at once -- a few KB of gzip can expand past the cap in one call.
_FETCH_HEADERS = {"Accept-Encoding": "gzip, deflate"}
_ZLIB_WBITS = {
    "gzip": zlib.MAX_WBITS | 16,
    "x-gzip": zlib.MAX_WBITS | 16,
    "deflate": zlib.MAX_WBITS,
}


def _read_capped(resp: httpx.Response, max_bytes: int, deadline_at: float) -> bytes:
    """Read a streamed body, never holding more than ``max_bytes`` decoded bytes."""
    encoding = resp.headers.get("content-encoding", "").strip().lower()
    if encoding in ("", "identity"):
        inflate = None
    elif encoding in _ZLIB_WBITS:
        inflate = zlib.decompressobj(_ZLIB_WBITS[encoding])
    else:
        raise PublicFetchError(f"unsupported content-encoding: {encoding}")

    too_big = PublicFetchError(f"response exceeds {max_bytes} bytes")
    if resp.is_stream_consumed:
        # Only an in-memory transport (tests' MockTransport) hands one back pre-read.
        if len(resp.content) > max_bytes:
            raise too_big
        return resp.content

    body = bytearray()
    for chunk in resp.iter_raw():
        if inflate is None:
            body += chunk
        else:
            room = max_bytes - len(body) + 1
            try:
                out = inflate.decompress(chunk, room)
            except zlib.error:
                if encoding != "deflate" or body or inflate.unused_data:
                    raise PublicFetchError("undecodable response body") from None
                # Some servers send raw deflate without the zlib header.
                inflate = zlib.decompressobj(-zlib.MAX_WBITS)
                try:
                    out = inflate.decompress(chunk, room)
                except zlib.error:
                    raise PublicFetchError("undecodable response body") from None
            body += out
            if inflate.unconsumed_tail:
                raise too_big
        if len(body) > max_bytes:
            raise too_big
        if time.monotonic() >= deadline_at:
            raise PublicFetchError("deadline exceeded")
    return bytes(body)


def fetch_public(
    url: str,
    *,
    client: httpx.Client | None = None,
    timeout: float = 12.0,
    max_bytes: int = MAX_FETCH_BYTES,
    deadline: float = FETCH_DEADLINE,
) -> bytes:
    """Fetch a user-supplied URL through the SSRF guard and return its body.

    The same per-hop host validation :func:`probe` uses, but the body comes back
    instead of a verdict — this is how the poller reads a tenant's custom RSS feed,
    whose URL is attacker-influenced. Raises :class:`PublicFetchError` when the URL
    is refused, unreachable, answers 4xx/5xx, exceeds ``max_bytes``, or doesn't
    finish within ``deadline`` seconds. The body is streamed and abandoned the
    moment it passes the cap, so an endless chunked response costs at most
    ``max_bytes`` of memory (#338).
    """
    url = (url or "").strip()
    owns_client = client is None
    client = client or ssrf_safe_client(timeout=timeout)
    try:
        with _deadline(client, deadline) as deadline_at:
            resp, _, error = _walk(url, client, deadline_at, _FETCH_HEADERS)
            if resp is None:
                raise PublicFetchError(error or "fetch failed")
            try:
                if resp.status_code >= 400:
                    raise PublicFetchError(f"HTTP {resp.status_code}")
                declared = resp.headers.get("content-length", "")
                if declared.isdigit() and int(declared) > max_bytes:
                    raise PublicFetchError(f"response exceeds {max_bytes} bytes")
                return _read_capped(resp, max_bytes, deadline_at)
            except httpx.HTTPError as exc:
                raise PublicFetchError(type(exc).__name__) from exc
            finally:
                resp.close()
    finally:
        if owns_client:
            client.close()


def probe(
    url: str,
    *,
    client: httpx.Client | None = None,
    timeout: float = 12.0,
    deadline: float = FETCH_DEADLINE,
) -> LinkStatus:
    """Fetch ``url`` as a browser and judge whether it still resolves to a page.

    A link is considered *not ok* when it is malformed, the connection fails, the
    server answers 4xx/5xx, or a deep URL redirects to the site's homepage -- the
    classic "this listing was pulled" signal. Redirects are followed by hand so
    each hop is SSRF-checked before we connect; the verdict is about the final
    response. Only the status line and headers matter, so no body is ever read,
    and the whole walk is bounded by ``deadline`` seconds (#338).
    """
    url = (url or "").strip()
    parts = urlsplit(url)
    if parts.scheme not in ("http", "https"):
        return LinkStatus(url=url, ok=False, error="not an http(s) URL")

    owns_client = client is None
    client = client or ssrf_safe_client(timeout=timeout)
    try:
        with _deadline(client, deadline) as deadline_at:
            resp, final_url, error = _walk(url, client, deadline_at)
        if resp is None:
            return LinkStatus(url=url, ok=False, error=error)
        resp.close()
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
