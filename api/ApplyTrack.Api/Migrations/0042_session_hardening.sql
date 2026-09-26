-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Session hardening (#346). The cookie's session id is now stored only as its sha256
-- (lowercase hex), like magic tokens, so a database read yields no live cookies. Rows
-- written before this migration hold the raw id; hash them in place, so nobody is signed
-- out by the upgrade. A raw id is 43 base64url chars and can never look like 64 hex
-- digits, so the WHERE makes this safe to run twice.
UPDATE sessions
SET id = encode(sha256(convert_to(id, 'UTF8')), 'hex')
WHERE id !~ '^[0-9a-f]{64}$';

-- Sliding expiry + "where am I signed in": when the session was last used, and the
-- browser that opened it (trimmed, display only).
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS last_seen_at timestamptz;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS user_agent text NOT NULL DEFAULT '';
UPDATE sessions SET last_seen_at = created_at WHERE last_seen_at IS NULL;
ALTER TABLE sessions ALTER COLUMN last_seen_at SET DEFAULT now();
ALTER TABLE sessions ALTER COLUMN last_seen_at SET NOT NULL;
