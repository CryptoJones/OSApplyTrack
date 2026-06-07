-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Per-tenant discovery criteria, the Postgres heir to applications/.criteria.json.
-- Columns mirror the Criteria domain object (the /api/criteria JSON contract the
-- SPA round-trips) so the Python poller can read them directly in Steps 3-4.
-- One row per tenant; absence means "fall back to defaults" (handled in the API).
CREATE TABLE search_profiles (
    tenant_id         bigint  PRIMARY KEY,
    keywords          jsonb   NOT NULL DEFAULT '[]'::jsonb,
    default_lane      text    NOT NULL DEFAULT 'ai',
    min_fit_score     integer NOT NULL DEFAULT 55,
    remote_only       boolean NOT NULL DEFAULT false,
    exclude_locations jsonb   NOT NULL DEFAULT '[]'::jsonb,
    sources           jsonb   NOT NULL DEFAULT '{}'::jsonb,
    ats_boards        jsonb   NOT NULL DEFAULT '[]'::jsonb,
    active            boolean NOT NULL DEFAULT true,
    updated_at        timestamptz NOT NULL DEFAULT now()
);
