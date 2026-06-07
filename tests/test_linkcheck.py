# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Offline tests for the link probe's SSRF guard.

Everything here uses IP literals (so the host check never does real DNS) and an
httpx.MockTransport (so no real network), keeping the suite hermetic.
"""

from __future__ import annotations

from collections.abc import Callable

import httpx

from applytrack.linkcheck import _host_is_public, probe

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
