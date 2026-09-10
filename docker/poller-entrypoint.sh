#!/bin/sh
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
#
# Three cadences, one container, no cron daemon:
#   - drain lane: drain the on-demand poll queue (the SPA's "Poll now" button)
#     every DRAIN_INTERVAL seconds, so the button doesn't wait for the hourly pass.
#   - ATS fast lane: poll only the tenants' ATS boards (Greenhouse/Lever/Ashby) every
#     ATS_POLL_INTERVAL seconds. These postings close fastest, so discovering them
#     within minutes — not up to an hour — is what lets the agent apply before they
#     close. It hits only the per-employer board APIs, so a short cadence does not
#     trip the shared aggregators' rate limits (which is why the full poll stays hourly).
#   - slow lane: a full multi-tenant poll (every source) every POLL_INTERVAL seconds.
# `|| true` keeps a transient board/DB failure (e.g. the schema not migrated yet on
# first boot) from killing any loop — the next tick retries. Prefer host cron /
# a systemd timer? Run `applytrack poll`, `applytrack poll --ats-only` and
# `applytrack poll --drain` from there instead and drop this service.
set -eu

POLL_INTERVAL="${POLL_INTERVAL:-3600}"
ATS_POLL_INTERVAL="${ATS_POLL_INTERVAL:-600}"
DRAIN_INTERVAL="${DRAIN_INTERVAL:-60}"

echo "applytrack poller: drain every ${DRAIN_INTERVAL}s, ATS fast lane every ${ATS_POLL_INTERVAL}s, full poll every ${POLL_INTERVAL}s"

while true; do applytrack poll --drain || true; sleep "${DRAIN_INTERVAL}"; done &
while true; do applytrack poll --ats-only || true; sleep "${ATS_POLL_INTERVAL}"; done &
while true; do applytrack poll || true; sleep "${POLL_INTERVAL}"; done
