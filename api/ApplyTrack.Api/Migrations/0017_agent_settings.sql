-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Agentic auto-apply, step 2: what the agent MAY do for a tenant, and a record of
-- what it DID. Deliberately a new table rather than columns on llm_settings —
-- llm_settings answers "where does a model live", this answers "what may the agent
-- do with it". `enabled` defaults false: unlike cover letters, the agent spends the
-- tenant's LLM budget unattended, so it must never switch itself on at a version
-- bump. `min_fit_score` is separate from search_profiles.min_fit_score on purpose:
-- "worth showing me" (55) and "worth spending tokens on" (70) are different bars.
-- The standing-answer fields are facts the agent hands to an application form
-- deterministically; they never go through the model.
CREATE TABLE agent_settings (
    tenant_id          bigint  PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    enabled            boolean NOT NULL DEFAULT false,
    -- Never click Submit on the tenant's behalf; prepare and stop. Read by step 4.
    dry_run            boolean NOT NULL DEFAULT true,
    min_fit_score      integer NOT NULL DEFAULT 70,
    max_per_run        integer NOT NULL DEFAULT 5,
    max_per_day        integer NOT NULL DEFAULT 20,
    work_authorization text    NOT NULL DEFAULT '',
    needs_sponsorship  boolean NOT NULL DEFAULT false,
    clearance_ok       boolean NOT NULL DEFAULT false,
    salary_expectation text    NOT NULL DEFAULT '',
    phone              text    NOT NULL DEFAULT '',
    updated_at         timestamptz NOT NULL DEFAULT now()
);

-- The audit trail. Keyed to users, NOT applications: deleting an application must
-- never erase the record of what the agent did about it, so application_name is a
-- plain text column with no FK. A `skip` verdict is terminal and lands here with
-- its reason, so a veto is visible rather than silent.
CREATE TABLE agent_events (
    id               bigserial PRIMARY KEY,
    tenant_id        bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    application_name text   NOT NULL DEFAULT '',
    kind             text   NOT NULL,
    detail           jsonb  NOT NULL DEFAULT '{}'::jsonb,
    created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX agent_events_tenant_created_idx ON agent_events (tenant_id, created_at DESC);
CREATE INDEX agent_events_tenant_app_idx ON agent_events (tenant_id, application_name, kind);
