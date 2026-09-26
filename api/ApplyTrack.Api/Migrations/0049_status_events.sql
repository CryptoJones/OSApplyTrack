-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Status history (#353): one row per status an application entered, so response
-- rate and time-to-response can be computed instead of overwritten. from_status is
-- NULL on the row an application was created with. Written by triggers, not by the
-- repos, so every writer is covered alike — the API's edits and imports, the agent's
-- flips, the Python poller's lead inserts — and nobody can forget to. Statement-level
-- with transition tables, like the list revision (0045): a 10k-row import is one
-- INSERT ... SELECT here, not ten thousand. The per-application timeline and the
-- analytics read it; later timeline sources (reminders, a notes log) sit beside it
-- keyed the same way (tenant_id, application_id, at).
CREATE TABLE IF NOT EXISTS status_events (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id      bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    application_id bigint NOT NULL REFERENCES applications (id) ON DELETE CASCADE,
    from_status    text,
    to_status      text   NOT NULL,
    at             timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS status_events_app_idx ON status_events (tenant_id, application_id, at);
CREATE INDEX IF NOT EXISTS status_events_tenant_at_idx ON status_events (tenant_id, at);

CREATE OR REPLACE FUNCTION record_status_inserted()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO status_events (tenant_id, application_id, from_status, to_status)
    SELECT tenant_id, id, NULL, status FROM changed;
    RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION record_status_updated()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO status_events (tenant_id, application_id, from_status, to_status)
    SELECT n.tenant_id, n.id, o.status, n.status
    FROM new_rows n JOIN old_rows o ON o.id = n.id
    WHERE o.status IS DISTINCT FROM n.status;
    RETURN NULL;
END;
$$;

-- A transition table rules out an UPDATE OF status column list, hence the filter above.
DROP TRIGGER IF EXISTS applications_status_insert ON applications;
DROP TRIGGER IF EXISTS applications_status_update ON applications;

CREATE TRIGGER applications_status_insert
AFTER INSERT ON applications
REFERENCING NEW TABLE AS changed
FOR EACH STATEMENT EXECUTE FUNCTION record_status_inserted();

CREATE TRIGGER applications_status_update
AFTER UPDATE ON applications
REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
FOR EACH STATEMENT EXECUTE FUNCTION record_status_updated();

-- Seed one event per existing application so history starts complete: the status it
-- is in now, dated by its Applied date when it has a valid one in ISO form and is past
-- applying, else when the row was created. Nothing earlier is knowable. Applied is
-- free text, so a bad date (2026-02-30) falls back rather than failing the migration.
CREATE OR REPLACE FUNCTION pg_temp.iso_day(v text)
RETURNS timestamptz
LANGUAGE plpgsql
AS $$
BEGIN
    IF v !~ '^\d{4}-\d{2}-\d{2}$' THEN
        RETURN NULL;
    END IF;
    RETURN (v || 'T00:00:00Z')::timestamptz;
EXCEPTION WHEN others THEN
    RETURN NULL;
END;
$$;

INSERT INTO status_events (tenant_id, application_id, from_status, to_status, at)
SELECT a.tenant_id, a.id, NULL, a.status,
       coalesce(CASE WHEN a.status IN ('applied', 'screen', 'onsite', 'offer', 'rejected')
                     THEN pg_temp.iso_day(a.applied) END,
                a.created_at)
FROM applications a
WHERE NOT EXISTS (
    SELECT 1 FROM status_events e
    WHERE e.tenant_id = a.tenant_id AND e.application_id = a.id);
