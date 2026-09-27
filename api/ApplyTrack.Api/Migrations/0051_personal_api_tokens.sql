-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Personal API tokens (#356): api_tokens (0050) takes two more scopes, for scripts, a
-- browser clipper, anything that can't carry the session cookie. 'read' opens the GET
-- routes, 'write' every method; neither reaches the account's credentials (tokens,
-- sessions, the calendar link) or deletes the account. Many per tenant, each named so
-- the owner can tell them apart; revoking one deletes its row. Still stored by sha256
-- only, the plaintext shown once when made.
ALTER TABLE api_tokens ADD COLUMN IF NOT EXISTS name text NOT NULL DEFAULT '';

ALTER TABLE api_tokens DROP CONSTRAINT IF EXISTS api_tokens_scope_check;
ALTER TABLE api_tokens ADD CONSTRAINT api_tokens_scope_check CHECK (scope IN ('calendar', 'read', 'write'));

CREATE INDEX IF NOT EXISTS api_tokens_tenant_idx ON api_tokens (tenant_id, created_at);
