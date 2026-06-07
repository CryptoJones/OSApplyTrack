-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The on-demand poll queue. The SPA's "Poll now" button hits POST /api/poll, but
-- the two runtimes are decoupled, so .NET can't run the Python poller inline:
-- instead it enqueues one row here and a fast cron (`applytrack poll --drain`,
-- every minute) claims it and polls just that tenant, rather than waiting for the
-- hourly pass. Rows are transient — the worker DELETEs each as it drains. Written
-- only by the .NET API, drained only by the Python worker. The FK cascade from
-- users lands in Step 5 (data delete), matching the other per-tenant tables.
CREATE TABLE poll_requests (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    bigint NOT NULL,
    requested_at timestamptz NOT NULL DEFAULT now()
);
