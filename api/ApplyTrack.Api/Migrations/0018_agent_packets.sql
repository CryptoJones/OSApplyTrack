-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Agentic auto-apply, step 3: the prepared application packet — everything the
-- human reviews before Submit. One packet per application, the same composite-FK
-- and cascade shape as cover_letters (the letter itself stays in cover_letters).
-- `questions` is the typed form the ATS asked (label, required, type, options);
-- `answers` maps question id -> the drafted text; `needs_review` lists the ids a
-- human must resolve before Submit unblocks, each with its reason. `verdict` and
-- `posting_excerpt` are what the agent actually judged, kept with the packet so a
-- veto or an answer can be checked against the text that produced it. The packet
-- has its own `version` for the edit-before-submit flow — applications.version
-- stays the only lock on applications. `notified_at` is the at-most-once claim
-- for the ready-to-submit notification.
CREATE TABLE agent_packets (
    tenant_id        bigint NOT NULL,
    application_name text   NOT NULL,
    provider         text   NOT NULL DEFAULT 'unknown',
    posting_excerpt  text   NOT NULL DEFAULT '',
    verdict          jsonb  NOT NULL DEFAULT '{}'::jsonb,
    questions        jsonb  NOT NULL DEFAULT '[]'::jsonb,
    answers          jsonb  NOT NULL DEFAULT '{}'::jsonb,
    needs_review     jsonb  NOT NULL DEFAULT '[]'::jsonb,
    model            text   NOT NULL DEFAULT '',
    version          bigint NOT NULL DEFAULT 1,
    notified_at      timestamptz,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, application_name),
    FOREIGN KEY (tenant_id, application_name)
        REFERENCES applications (tenant_id, name) ON DELETE CASCADE
);
