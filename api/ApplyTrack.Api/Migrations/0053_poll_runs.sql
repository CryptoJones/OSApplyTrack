-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- One row per finished poller pass for one tenant (#359): when it ran, how many leads it
-- staged, and which of the tenant's sources failed. users.last_polled_at (0052) stays the
-- cheap cross-tenant stamp the operator's `admin usage` reads; this is the per-pass detail
-- behind the SPA's "last polled 12 min ago · 2 sources failing". ats_only marks the
-- freshness fast lane, which polls only ATS boards — its errors say nothing about the
-- aggregators. errors is a JSON array of {"source": "...", "error": "..."}. Written by
-- src/applytrack/worker.py (INSERT only); read by GET /api/poll/status; pruned by the
-- retention sweep (Retention__PollRunDays).
CREATE TABLE IF NOT EXISTS poll_runs (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id   bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    started_at  timestamptz NOT NULL,
    finished_at timestamptz NOT NULL DEFAULT now(),
    ats_only    boolean NOT NULL DEFAULT false,
    leads_added integer NOT NULL DEFAULT 0,
    errors      jsonb NOT NULL DEFAULT '[]'::jsonb
);

CREATE INDEX IF NOT EXISTS poll_runs_tenant_finished_idx ON poll_runs (tenant_id, finished_at DESC);
