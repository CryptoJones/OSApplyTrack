-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Runs on EVERY boot of the migrating (owner) container, beside agent_role.sql: the
-- grants for the discovery poller's own Postgres role (#345). The migrating container
-- creates the role when POLLER_DB_PASSWORD is set; without it this is a no-op and the
-- poller keeps connecting as the owner. Exactly what src/applytrack/db.py and
-- worker.py run: read the tenant's profile, blacklist and dedup ledger, stage leads,
-- keep the board sessions it signs in with, claim the poll queue. Nothing else — no
-- sessions, magic tokens, résumés, letters, board passwords or DDL — so a bug in the
-- feed-parsing surface is "insert leads", not "the whole database". Revoked first so
-- the role holds this list and only this list, whatever an earlier boot granted.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'applytrack_poller') THEN
        REVOKE ALL ON ALL TABLES IN SCHEMA public FROM applytrack_poller;
        GRANT USAGE ON SCHEMA public TO applytrack_poller;
        GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO applytrack_poller;
        -- Active tenants; the list-revision trigger bumps the revision on each lead.
        GRANT SELECT (id, status, applications_revision) ON users TO applytrack_poller;
        GRANT UPDATE (applications_revision) ON users TO applytrack_poller;
        GRANT SELECT ON search_profiles, blacklist, agent_settings TO applytrack_poller;
        GRANT SELECT, INSERT ON applications, seen TO applytrack_poller;
        -- The sealed session, never the sealed password.
        GRANT SELECT (tenant_id, host, username, session_ciphertext, session_expires_at)
            ON board_accounts TO applytrack_poller;
        GRANT UPDATE (session_ciphertext, session_expires_at, updated_at)
            ON board_accounts TO applytrack_poller;
        -- Claimed with DELETE ... RETURNING tenant_id; re-queued with INSERT.
        GRANT SELECT, INSERT, DELETE ON poll_requests TO applytrack_poller;
    END IF;
END $$;
