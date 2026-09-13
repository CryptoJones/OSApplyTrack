-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The answer bank: every screening question the agent has met on an application
-- form, keyed by the question as the form asked it, with the answer it gave. New
-- questions land here as packets are built; a person edits an answer once and the
-- agent uses that answer on every later form that asks the same question (#174).
-- Answers are the candidate's own words (salary, eligibility): sealed at rest like
-- packet answers. Per tenant.
CREATE TABLE IF NOT EXISTS answer_bank (
    tenant_id         bigint      NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    key               text        NOT NULL,
    label             text        NOT NULL DEFAULT '',
    help              text        NOT NULL DEFAULT '',
    type              text        NOT NULL DEFAULT 'text',
    options           jsonb       NOT NULL DEFAULT '[]'::jsonb,
    answer_ciphertext text        NOT NULL DEFAULT '',
    -- 'agent': what the drafter last said, refreshed on every build.
    -- 'human': the person's own answer; the drafter never overwrites it.
    source            text        NOT NULL DEFAULT 'agent',
    first_application text        NOT NULL DEFAULT '',
    times_seen        integer     NOT NULL DEFAULT 1,
    first_seen_at     timestamptz NOT NULL DEFAULT now(),
    last_seen_at      timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, key)
);
