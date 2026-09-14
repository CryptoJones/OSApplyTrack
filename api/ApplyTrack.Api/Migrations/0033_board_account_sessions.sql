-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The session a signed-in discovery source keeps on the tenant's board account
-- (MyGreenhouse, #218): sealed like the password, written and read by the Python
-- poller under the same master key, so the emailed security code is read once a
-- fortnight rather than on every poll.
ALTER TABLE board_accounts
    ADD COLUMN IF NOT EXISTS session_ciphertext text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS session_expires_at timestamptz;
