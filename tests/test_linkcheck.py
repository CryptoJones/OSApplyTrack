# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Offline tests for the link probe's SSRF guard.

Everything here uses IP literals (so the host check never does real DNS) and an
httpx.MockTransport (so no real network), keeping the suite hermetic.
"""

from __future__ import annotations

import socket
import threading
import time
from collections.abc import Callable, Iterator

import httpcore
import httpx
import pytest

from applytrack import linkcheck
from applytrack.linkcheck import (
    PublicFetchError,
    _host_is_public,
    _PinnedNetworkBackend,
    _PinnedResolver,
    fetch_public,
    probe,
    ssrf_safe_client,
)

Handler = Callable[[httpx.Request], httpx.Response]


def _client(handler: Handler) -> httpx.Client:
    return httpx.Client(transport=httpx.MockTransport(handler))


def test_public_ip_literal_is_allowed() -> None:
    assert _host_is_public("1.1.1.1") is True


def test_internal_ip_literals_are_blocked() -> None:
    for host in (
        "127.0.0.1",        # loopback
        "10.0.0.1",         # RFC-1918 private
        "192.168.1.1",      # RFC-1918 private
        "169.254.169.254",  # link-local / cloud metadata
        "0.0.0.0",          # unspecified
        "::1",              # IPv6 loopback
        "[::1]",            # bracketed IPv6 loopback
        "::ffff:127.0.0.1",  # IPv4-mapped loopback
    ):
        assert _host_is_public(host) is False, host


def test_probe_refuses_loopback_without_touching_the_network() -> None:
    status = probe("http://127.0.0.1/some/job")
    assert status.ok is False
    assert "non-public" in status.error


def test_probe_refuses_non_http_scheme() -> None:
    status = probe("file:///etc/passwd")
    assert status.ok is False
    assert status.error == "not an http(s) URL"


def test_probe_allows_a_live_public_posting() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, text="a real job posting")

    status = probe("http://1.1.1.1/jobs/senior-engineer-12345", client=_client(handler))
    assert status.ok is True
    assert status.status_code == 200


def test_probe_blocks_a_redirect_to_an_internal_address() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        # A public posting that 302s to the loopback interface — the SSRF the
        # per-hop guard exists to stop. The redirect target must never be fetched.
        assert request.url.host == "1.1.1.1", "should never connect to the internal hop"
        return httpx.Response(302, headers={"location": "http://127.0.0.1/admin"})

    status = probe("http://1.1.1.1/jobs/lure-12345", client=_client(handler))
    assert status.ok is False
    assert "non-public" in status.error


def test_probe_follows_a_public_redirect_chain() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/old":
            return httpx.Response(301, headers={"location": "http://1.0.0.1/jobs/new-67890"})
        return httpx.Response(200, text="moved posting")

    status = probe("http://1.1.1.1/old", client=_client(handler))
    assert status.ok is True
    assert status.final_url == "http://1.0.0.1/jobs/new-67890"


def test_dns_answer_is_validated_once_then_tcp_uses_the_pinned_ip(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    dns_calls: list[str] = []

    def fake_getaddrinfo(
        host: str,
        port: int | None,
        *,
        type: socket.SocketKind,
    ) -> list[tuple[socket.AddressFamily, socket.SocketKind, int, str, tuple[str, int]]]:
        del port, type
        dns_calls.append(host)
        address = "93.184.216.34" if len(dns_calls) == 1 else "127.0.0.1"
        return [(socket.AF_INET, socket.SOCK_STREAM, 6, "", (address, 0))]

    connected: list[str] = []

    def fake_connect(
        self: httpcore.SyncBackend,
        host: str,
        port: int,
        timeout: float | None = None,
        local_address: str | None = None,
        socket_options: list[tuple[int, int, int | bytes]] | None = None,
    ) -> object:
        del self, port, timeout, local_address, socket_options
        connected.append(host)
        return object()

    monkeypatch.setattr(socket, "getaddrinfo", fake_getaddrinfo)
    monkeypatch.setattr(httpcore.SyncBackend, "connect_tcp", fake_connect)

    resolver = _PinnedResolver()
    assert resolver.pin("jobs.example") is True
    assert resolver.pin("jobs.example") is True
    backend = _PinnedNetworkBackend(resolver)
    backend.connect_tcp("jobs.example", 443)

    assert dns_calls == ["jobs.example"]
    assert connected == ["93.184.216.34"]


def test_resolver_rejects_a_hostname_with_any_private_answer(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def fake_getaddrinfo(
        host: str,
        port: int | None,
        *,
        type: socket.SocketKind,
    ) -> list[tuple[socket.AddressFamily, socket.SocketKind, int, str, tuple[str, int]]]:
        del host, port, type
        return [
            (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("93.184.216.34", 0)),
            (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("10.0.0.8", 0)),
        ]

    monkeypatch.setattr(socket, "getaddrinfo", fake_getaddrinfo)
    assert _PinnedResolver().pin("mixed.example") is False


def test_injected_ordinary_client_cannot_bypass_pinning_for_a_hostname() -> None:
    touched = False

    def handler(request: httpx.Request) -> httpx.Response:
        nonlocal touched
        touched = True
        return httpx.Response(200, request=request)

    with _client(handler) as client:
        status = probe("https://jobs.example/posting/123", client=client)

    assert status.ok is False
    assert "unpinned" in status.error
    assert touched is False


# -- fetch_public: the same guard, but the body comes back ------------------


def test_fetch_public_returns_the_body_of_a_public_url() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, content=b"<rss/>", request=request)

    with _client(handler) as client:
        assert fetch_public("http://1.1.1.1/feed.rss", client=client) == b"<rss/>"


def test_fetch_public_refuses_a_private_address_without_touching_the_network() -> None:
    touched = False

    def handler(request: httpx.Request) -> httpx.Response:
        nonlocal touched
        touched = True
        return httpx.Response(200, request=request)

    with _client(handler) as client, pytest.raises(PublicFetchError, match="non-public"):
        fetch_public("http://169.254.169.254/latest/meta-data", client=client)

    assert touched is False


def test_fetch_public_refuses_a_redirect_to_an_internal_address() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.host == "1.1.1.1":
            return httpx.Response(302, headers={"location": "http://127.0.0.1/feed"})
        return httpx.Response(200, content=b"secret", request=request)

    with _client(handler) as client, pytest.raises(PublicFetchError, match="non-public"):
        fetch_public("http://1.1.1.1/feed", client=client)


def test_fetch_public_rejects_an_error_response() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(404, request=request)

    with _client(handler) as client, pytest.raises(PublicFetchError, match="404"):
        fetch_public("http://1.1.1.1/gone.rss", client=client)


def test_fetch_public_enforces_the_size_cap() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, content=b"x" * 50, request=request)

    # Declared Content-Length is refused before the body is looked at; an undeclared
    # oversize body is refused after.
    with _client(handler) as client, pytest.raises(PublicFetchError, match="exceeds"):
        fetch_public("http://1.1.1.1/huge.rss", client=client, max_bytes=10)


# -- bounded fetches: an endless or trickling server can't hold the poller (#338) --


def _endless() -> Iterator[bytes]:
    while True:
        yield b"x" * 1024


def test_fetch_public_abandons_an_endless_chunked_body_at_the_cap() -> None:
    # No Content-Length, a body that never ends: the old code buffered it forever.
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, content=_endless(), request=request)

    with _client(handler) as client, pytest.raises(PublicFetchError, match="exceeds"):
        fetch_public("http://1.1.1.1/feed.rss", client=client, max_bytes=64 * 1024)


def test_probe_never_reads_the_body() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, content=_endless(), request=request)

    with _client(handler) as client:
        status = probe("http://1.1.1.1/jobs/senior-engineer-12345", client=client)
    assert status.ok is True


def test_fetch_public_enforces_the_deadline_between_chunks_on_any_client() -> None:
    def slow() -> Iterator[bytes]:
        while True:
            time.sleep(0.05)
            yield b"x"

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, content=slow(), request=request)

    started = time.monotonic()
    with _client(handler) as client, pytest.raises(PublicFetchError, match="deadline"):
        fetch_public("http://1.1.1.1/feed.rss", client=client, deadline=0.5)
    assert time.monotonic() - started < 3


class _TrickleServer:
    """A real local socket server that dribbles one byte at a time, forever."""

    def __init__(self, payload: bytes, then: bytes) -> None:
        self._payload = payload
        self._then = then
        self._stop = threading.Event()
        self._sock = socket.socket()
        self._sock.bind(("127.0.0.1", 0))
        self._sock.listen()
        self._sock.settimeout(0.1)
        self.port = self._sock.getsockname()[1]
        self._thread = threading.Thread(target=self._serve, daemon=True)
        self._thread.start()

    def _serve(self) -> None:
        while not self._stop.is_set():
            try:
                conn, _ = self._sock.accept()
            except OSError:
                continue
            with conn:
                try:
                    conn.recv(65536)
                    conn.sendall(self._payload)
                    while not self._stop.is_set():
                        conn.sendall(self._then)
                        time.sleep(0.05)
                except OSError:
                    pass

    def close(self) -> None:
        self._stop.set()
        self._thread.join(timeout=2)
        self._sock.close()


@pytest.fixture
def allow_loopback(monkeypatch: pytest.MonkeyPatch) -> None:
    # The SSRF gate rightly refuses 127.0.0.1; lift it so the real pinned client can
    # talk to the local trickle server and exercise its socket-level deadline.
    monkeypatch.setattr(linkcheck, "_ip_is_public", lambda ip: True)


@pytest.mark.usefixtures("allow_loopback")
def test_pinned_client_deadline_stops_a_body_that_trickles_forever() -> None:
    # Every byte lands well inside the 5s per-read timeout, so only the wall clock
    # can end this -- and a 1-byte-per-chunk stream never reaches the size cap.
    server = _TrickleServer(
        b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n", b"1\r\nx\r\n"
    )
    try:
        started = time.monotonic()
        with ssrf_safe_client(timeout=5.0) as client, pytest.raises(PublicFetchError):
            fetch_public(f"http://127.0.0.1:{server.port}/feed", client=client, deadline=1.0)
        assert time.monotonic() - started < 3
    finally:
        server.close()


@pytest.mark.usefixtures("allow_loopback")
def test_pinned_client_deadline_stops_headers_that_trickle_forever() -> None:
    # A header block that never finishes: no chunk boundary is ever reached, so only
    # the socket-level clamp can cut it off.
    server = _TrickleServer(b"HTTP/1.1 200 OK\r\n", b"X")
    try:
        started = time.monotonic()
        with ssrf_safe_client(timeout=5.0) as client:
            status = probe(
                f"http://127.0.0.1:{server.port}/jobs/lure-12345", client=client, deadline=1.0
            )
        assert status.ok is False
        assert "Timeout" in status.error
        assert time.monotonic() - started < 3
    finally:
        server.close()


@pytest.mark.usefixtures("allow_loopback")
def test_pinned_client_is_reusable_after_a_deadline() -> None:
    # The deadline is armed per fetch, so the poller's shared client isn't poisoned.
    server = _TrickleServer(b"HTTP/1.1 200 OK\r\n", b"X")
    try:
        with ssrf_safe_client(timeout=5.0) as client:
            probe(f"http://127.0.0.1:{server.port}/a/1", client=client, deadline=0.3)
            assert client._deadline.at is None  # type: ignore[attr-defined]
    finally:
        server.close()
