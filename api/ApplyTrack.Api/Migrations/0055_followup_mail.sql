-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Outgoing mail for agentic follow-ups (#394).
--
-- A follow-up goes out from the candidate's own mailbox, never the instance's relay: the
-- IMAP account already saved for security codes gains the SMTP host and port of the same
-- provider, and sends with the same username and sealed password. Blank smtp_host means
-- the tenant has no outgoing mail here — the draft is copied or opened in a mail app.
ALTER TABLE mailbox_settings ADD COLUMN IF NOT EXISTS smtp_host text NOT NULL DEFAULT '';
ALTER TABLE mailbox_settings ADD COLUMN IF NOT EXISTS smtp_port integer NOT NULL DEFAULT 587;
