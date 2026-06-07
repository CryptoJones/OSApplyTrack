#!/bin/sh
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
#
# Two cadences, one container, no cron daemon:
#   - fast lane: drain the on-demand poll queue (the SPA's "Poll now" button)
#     every DRAIN_INTERVAL seconds, so the button doesn't wait for the hourly pass.
#   - slow lane: a full multi-tenant poll every POLL_INTERVAL seconds.
# `|| true` keeps a transient board/DB failure (e.g. the schema not migrated yet on
# first boot) from killing either loop — the next tick retries. Prefer host cron /
# a systemd timer? Run `applytrack poll` and `applytrack poll --drain` from there
# instead and drop this service.
set -eu

POLL_INTERVAL="${POLL_INTERVAL:-3600}"
DRAIN_INTERVAL="${DRAIN_INTERVAL:-60}"

echo "applytrack poller: drain every ${DRAIN_INTERVAL}s, full poll every ${POLL_INTERVAL}s"

while true; do applytrack poll --drain || true; sleep "${DRAIN_INTERVAL}"; done &
while true; do applytrack poll || true; sleep "${POLL_INTERVAL}"; done
