# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
#
# The forward proxy the browser container must go through. With HTTPS_PROXY set,
# Chromium emits CONNECT host:443 and never resolves a name itself, so DNS
# rebinding is structurally impossible at the browser — the same property the
# Python link checker's pinned resolver buys. Squid also refuses to CONNECT to
# private, loopback, link-local and CGNAT destinations, so even a posting that
# redirects the browser at the LAN goes nowhere.
FROM ubuntu/squid:6.6-24.04_edge
COPY squid.conf /etc/squid/squid.conf
