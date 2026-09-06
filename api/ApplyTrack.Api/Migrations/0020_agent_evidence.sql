-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- What the browser saw: a screenshot and any confirmation text per dry run or
-- submission, kept out of the hot packet row (a screenshot is a few hundred KB the
-- packet never needs to carry). Cascades with the application like the packet.
CREATE TABLE agent_evidence (
    id               bigserial PRIMARY KEY,
    tenant_id        bigint NOT NULL,
    application_name text   NOT NULL,
    -- dry_run | submitted | failed
    kind             text   NOT NULL,
    url              text   NOT NULL DEFAULT '',
    confirmation     text   NOT NULL DEFAULT '',
    detail           jsonb  NOT NULL DEFAULT '{}'::jsonb,
    screenshot       bytea,
    created_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, application_name)
        REFERENCES applications (tenant_id, name) ON DELETE CASCADE
);
CREATE INDEX agent_evidence_app_idx ON agent_evidence (tenant_id, application_name, created_at DESC);
