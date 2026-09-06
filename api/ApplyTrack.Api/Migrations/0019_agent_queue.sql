-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Agentic auto-apply, step 4: the human's "Submit" click, as a queue the worker
-- drains. One row per application (UNIQUE) so a double-click is a no-op while a
-- request is in flight; a finished row is reused by the next request. Claimed by
-- UPDATE (claimed_at) under FOR UPDATE SKIP LOCKED rather than DELETE, because the
-- agent's Postgres role deliberately has no DELETE anywhere.
CREATE TABLE submit_requests (
    id               bigserial PRIMARY KEY,
    tenant_id        bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    application_name text   NOT NULL,
    dry_run          boolean NOT NULL DEFAULT true,
    requested_at     timestamptz NOT NULL DEFAULT now(),
    claimed_at       timestamptz,
    done_at          timestamptz,
    UNIQUE (tenant_id, application_name)
);
CREATE INDEX submit_requests_pending_idx ON submit_requests (requested_at) WHERE done_at IS NULL;
