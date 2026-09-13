-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Auto-apply is off for every account until the operator allows it by hand. An
-- account not in this table cannot switch the agent on, prepare a packet, queue
-- a browser run, or hand a security code to one, and the worker never fans out
-- over it. There is no API for this table on purpose: a row is added at the
-- database —
--     INSERT INTO agent_allowlist (tenant_id, note) VALUES (1, 'the operator');
-- — and removed the same way.
CREATE TABLE IF NOT EXISTS agent_allowlist (
    tenant_id bigint      PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    added_at  timestamptz NOT NULL DEFAULT now(),
    note      text        NOT NULL DEFAULT ''
);
