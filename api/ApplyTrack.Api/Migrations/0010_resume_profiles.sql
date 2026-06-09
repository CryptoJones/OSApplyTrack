-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Materials engine: per-tenant structured résumé — the factual brief the
-- cover-letter drafter feeds the LLM as "the only facts you may assert about
-- the candidate" (the multi-tenant heir to materials.py's hardcoded _BACKGROUND).
-- One row per tenant; absence means "empty résumé" (handled in the API).
-- jsonb for the repeating sections (experience/skills/certifications/links) so a
-- structured editor round-trips them without a child table.
CREATE TABLE resume_profiles (
    tenant_id      bigint  PRIMARY KEY
                   REFERENCES users (id) ON DELETE CASCADE,
    full_name      text    NOT NULL DEFAULT '',
    headline       text    NOT NULL DEFAULT '',
    location       text    NOT NULL DEFAULT '',
    summary        text    NOT NULL DEFAULT '',
    experience     jsonb   NOT NULL DEFAULT '[]'::jsonb,
    skills         jsonb   NOT NULL DEFAULT '[]'::jsonb,
    certifications jsonb   NOT NULL DEFAULT '[]'::jsonb,
    links          jsonb   NOT NULL DEFAULT '[]'::jsonb,
    updated_at     timestamptz NOT NULL DEFAULT now()
);
