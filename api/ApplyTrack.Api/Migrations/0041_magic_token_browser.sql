-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Bind each magic-link token to the browser that asked for it (#341). POST /api/auth/request
-- sets a pre-auth cookie and stores its sha256 here; verify only spends the token when the
-- same cookie comes back, so a link the attacker requested for their own address cannot
-- sign a victim's browser into the attacker's account (login CSRF). Rows from before this
-- migration have no binding and can never be spent — they expire within 15 minutes anyway.

ALTER TABLE magic_tokens
    ADD COLUMN IF NOT EXISTS browser_sha256 bytea;
