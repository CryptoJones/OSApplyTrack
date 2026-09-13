-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The candidate's own mailbox, read over IMAP, so a run parked on Greenhouse's
-- emailed security code can fetch the code itself instead of waiting for a human
-- to relay it. Per tenant; the password (an app password) is sealed at rest like
-- the Telegram bot token and never returned to the client.
CREATE TABLE IF NOT EXISTS mailbox_settings (
    tenant_id           bigint  PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    enabled             boolean NOT NULL DEFAULT false,
    host                text    NOT NULL DEFAULT '',
    port                integer NOT NULL DEFAULT 993,
    username            text    NOT NULL DEFAULT '',
    password_ciphertext text    NOT NULL DEFAULT '',
    updated_at          timestamptz NOT NULL DEFAULT now()
);
