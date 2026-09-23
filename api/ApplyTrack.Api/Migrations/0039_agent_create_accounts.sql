-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Candidate-account creation (#277): a per-tenant opt-in, default off. On, when a run stops at
-- an ATS that only takes applications from a signed-in candidate (Workday, haystack.cv) and no
-- account is saved for it, the agent registers one with the tenant's application email and a
-- generated password, verifies it from the tenant's mailbox, proves it by signing in, and only
-- then seals it under Board accounts. Off, nothing is ever registered anywhere.

ALTER TABLE agent_settings
    ADD COLUMN IF NOT EXISTS create_accounts boolean NOT NULL DEFAULT false;
