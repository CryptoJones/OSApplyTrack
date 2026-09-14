-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The candidate's own accounts on applicant-tracking systems that only take an
-- application from a signed-in candidate (SAP SuccessFactors, #216). One row per
-- tenant per host; the password is sealed at rest like the mailbox password and the
-- Telegram token, and never returned to the client. The browser signs in with it
-- when Apply leads to that host and the page asks for a password.
CREATE TABLE IF NOT EXISTS board_accounts (
    tenant_id           bigint  NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    host                text    NOT NULL,
    username            text    NOT NULL DEFAULT '',
    password_ciphertext text    NOT NULL DEFAULT '',
    updated_at          timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, host)
);
