-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Discovery -> verdict -> dry-run -> submit timeline per application, so the win
-- from cutting posting->applied latency is measurable and any regression is visible.
-- Every timestamp already exists (applications.created_at, agent_events.created_at,
-- agent_evidence.created_at); this view just joins the first of each and reports the
-- second-deltas. Read-only and tenant-scoped by column: a caller still filters
-- WHERE tenant_id like every other query. The agent role's SELECT is re-granted on
-- every boot by Migrations/Always/agent_role.sql (GRANT SELECT ON ALL TABLES covers
-- views), so no extra grant is needed here.
CREATE OR REPLACE VIEW agent_latency AS
SELECT
    a.tenant_id,
    a.name        AS application_name,
    a.company,
    a.role,
    a.status,
    a.created_at  AS discovered_at,
    v.first_verdict_at,
    d.first_dry_run_at,
    s.first_submitted_at,
    -- ::double precision because EXTRACT returns numeric on PG14+, and the callers read
    -- these as a floating-point number of seconds.
    EXTRACT(EPOCH FROM (v.first_verdict_at   - a.created_at))::double precision AS discovery_to_verdict_seconds,
    EXTRACT(EPOCH FROM (d.first_dry_run_at   - a.created_at))::double precision AS discovery_to_dry_run_seconds,
    EXTRACT(EPOCH FROM (s.first_submitted_at - a.created_at))::double precision AS discovery_to_submitted_seconds
FROM applications a
LEFT JOIN (
    SELECT tenant_id, application_name, min(created_at) AS first_verdict_at
    FROM agent_events WHERE kind = 'verdict'
    GROUP BY tenant_id, application_name
) v ON v.tenant_id = a.tenant_id AND v.application_name = a.name
LEFT JOIN (
    SELECT tenant_id, application_name, min(created_at) AS first_dry_run_at
    FROM agent_evidence WHERE kind = 'dry_run'
    GROUP BY tenant_id, application_name
) d ON d.tenant_id = a.tenant_id AND d.application_name = a.name
LEFT JOIN (
    SELECT tenant_id, application_name, min(created_at) AS first_submitted_at
    FROM agent_evidence WHERE kind = 'submitted'
    GROUP BY tenant_id, application_name
) s ON s.tenant_id = a.tenant_id AND s.application_name = a.name;
