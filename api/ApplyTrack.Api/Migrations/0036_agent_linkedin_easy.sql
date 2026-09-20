-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- LinkedIn Easy Apply (#278): a per-tenant opt-in, default off. On, the poller stages Easy
-- Apply postings (it skipped them before) and the browser may drive the Easy Apply dialog
-- signed in as the tenant's own LinkedIn account. Off, nothing about LinkedIn changes.
-- Read by both runtimes: the .NET agent gates the driver on it, the Python poller the staging.

ALTER TABLE agent_settings
    ADD COLUMN IF NOT EXISTS linkedin_easy boolean NOT NULL DEFAULT false;
