-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The applications table: one row per job-application "note", the Postgres heir
-- to the original one-Markdown-file-per-app store. `name` is the slug (the
-- public key the SPA uses in /api/apps/{name}); it is unique within a tenant.
-- applied/followup/created/score stay text to match the SPA's free-text fields.
CREATE TABLE applications (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id     bigint NOT NULL,
    name          text   NOT NULL,
    company       text   NOT NULL DEFAULT '',
    role          text   NOT NULL DEFAULT '',
    lane          text   NOT NULL DEFAULT 'ai',
    status        text   NOT NULL DEFAULT 'lead',
    link          text   NOT NULL DEFAULT '',
    location      text   NOT NULL DEFAULT '',
    salary        text   NOT NULL DEFAULT '',
    source        text   NOT NULL DEFAULT '',
    contact       text   NOT NULL DEFAULT '',
    contact_email text   NOT NULL DEFAULT '',
    applied       text   NOT NULL DEFAULT '',
    followup      text   NOT NULL DEFAULT '',
    created       text   NOT NULL DEFAULT '',
    score         text   NOT NULL DEFAULT '',
    notes         text   NOT NULL DEFAULT '',
    version       bigint NOT NULL DEFAULT 1,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, name)
);

CREATE INDEX applications_tenant_status_idx ON applications (tenant_id, status);
