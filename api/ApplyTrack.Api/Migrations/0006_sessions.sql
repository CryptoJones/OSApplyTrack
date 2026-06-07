-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Server-side sessions (not JWT) so logout / revocation is instant: dropping the
-- row kills the session. id is a high-entropy opaque token held in the cookie and
-- used as the PK. Rows cascade with the user.
CREATE TABLE sessions (
    id         text   PRIMARY KEY,
    user_id    bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX sessions_user_idx ON sessions (user_id);
