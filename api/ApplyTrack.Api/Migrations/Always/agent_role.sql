-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Runs on EVERY boot of the migrating (owner) container, after the versioned
-- scripts: the least-privilege grants for the agent worker's own Postgres role.
-- The role is optional and created by the operator (docker/db-init/10-agent-role.sh
-- when AGENT_DB_PASSWORD is set); when it does not exist this is a no-op. It gets
-- SELECT on the tenant tables, INSERT/UPDATE only where the agent writes, no
-- DELETE anywhere, and NO access to sessions or magic_tokens — so a renderer
-- escape in the browser container is "write rows the agent already writes", not
-- "read every session token in the system". Re-applied every boot so a table
-- added by a later migration is covered without a hand-run GRANT.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'applytrack_agent') THEN
        GRANT USAGE ON SCHEMA public TO applytrack_agent;
        GRANT SELECT ON ALL TABLES IN SCHEMA public TO applytrack_agent;
        GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO applytrack_agent;
        REVOKE ALL ON sessions, magic_tokens FROM applytrack_agent;
        GRANT INSERT, UPDATE ON agent_events, agent_packets, agent_evidence,
            cover_letters, submit_requests TO applytrack_agent;
        -- Status flips (ready / applied); the list-revision trigger bumps users.
        GRANT UPDATE ON applications TO applytrack_agent;
        GRANT UPDATE (applications_revision) ON users TO applytrack_agent;
    END IF;
END $$;
