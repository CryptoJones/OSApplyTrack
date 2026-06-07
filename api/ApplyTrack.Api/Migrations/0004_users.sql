-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The users table: one row per account. For v1, tenant_id == users.id (the name
-- future-proofs orgs), so this id is the value every other table scopes by.
-- email is citext (enabled by 0000_extensions.sql) so logins are case-insensitive.
-- FK cascades from the per-tenant data tables land in Step 5 (data delete); the
-- existing bootstrap tenant_id=1 rows have no user row yet, so we don't add them now.
CREATE TABLE users (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      citext NOT NULL UNIQUE,
    status     text   NOT NULL DEFAULT 'active',
    created_at timestamptz NOT NULL DEFAULT now()
);
