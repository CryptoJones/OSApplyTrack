-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Per-tenant outbound notification target — the "moo" that says a packet is ready
-- to submit. Telegram only in v1: the bot token is the tenant's own, encrypted at
-- rest with SecretProtector (same as llm_settings.api_key_ciphertext) and never
-- returned to the client. One row per tenant; absence means "off".
-- (0019-0021 are reserved for step 4's tables; DbUp orders by name, and a gap is fine.)
CREATE TABLE notification_settings (
    tenant_id                     bigint  PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    telegram_enabled              boolean NOT NULL DEFAULT false,
    telegram_bot_token_ciphertext text    NOT NULL DEFAULT '',
    telegram_chat_id              text    NOT NULL DEFAULT '',
    updated_at                    timestamptz NOT NULL DEFAULT now()
);
