-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Jev semantic fit (#300): a per-tenant opt-in, default off. On -- and only when the
-- operator has set TYPESAFE_API_KEY on the poller -- the poller asks TypeSafe's Jev
-- whether each new listing's own work is the kind the tenant's keywords describe, and
-- that 0-100 fit replaces the keyword score against min_fit_score. Off, or on any error,
-- the keyword classify() decides exactly as before. Read by the Python poller; the .NET
-- API only stores and returns it.

ALTER TABLE agent_settings
    ADD COLUMN IF NOT EXISTS jev_classify boolean NOT NULL DEFAULT false;
