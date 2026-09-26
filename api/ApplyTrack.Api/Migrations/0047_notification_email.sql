-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Email as a second notification channel (#355), sent through the instance's own SMTP
-- sender to the account's own address — so there is nothing to store but the switch.
-- The per-event toggles apply to every channel; each defaults on, so a tenant who had
-- Telegram mooing before keeps every moo it had.
ALTER TABLE notification_settings
    ADD COLUMN IF NOT EXISTS email_enabled        boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS notify_packet_ready  boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS notify_security_code boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS notify_submit_failed boolean NOT NULL DEFAULT true;
