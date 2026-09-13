-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The agent worker's heartbeat. On every shipped deployment shape the browser is
-- wired to the agent container only, so the api process cannot tell from its own
-- configuration whether a Submit click has anything to land on. The worker stamps
-- this row on each tick; the api reads it and lets Submit and packet/prepare queue a
-- browser run when a browser-capable worker has been seen recently (#159). One row
-- per worker container (its hostname), never deleted, ignored once stale.
CREATE TABLE IF NOT EXISTS agent_workers (
    id         text        PRIMARY KEY,
    browser    boolean     NOT NULL DEFAULT false,
    started_at timestamptz NOT NULL DEFAULT now(),
    seen_at    timestamptz NOT NULL DEFAULT now()
);
