-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Single-use magic-link tokens. Only the sha256 of the emailed token is stored,
-- never the raw token: a DB read alone cannot mint a login. Verify looks up by
-- hash, checks expires_at/used_at, then stamps used_at. Rows cascade with the user.
CREATE TABLE magic_tokens (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id      bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    token_sha256 bytea  NOT NULL UNIQUE,
    expires_at   timestamptz NOT NULL,
    used_at      timestamptz,
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX magic_tokens_user_idx ON magic_tokens (user_id);
