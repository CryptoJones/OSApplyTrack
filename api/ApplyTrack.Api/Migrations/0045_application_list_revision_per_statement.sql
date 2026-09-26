-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The list revision (0015) bumped the tenant's users row once per changed
-- application: a 10k-row import meant 10k updates of one row in one transaction —
-- 10k dead tuples and that row locked throughout, stalling the agent's and the
-- poller's writes for the tenant (#351). Statement-level triggers with transition
-- tables bump each touched tenant once per statement instead. The revision only has
-- to move when the list changes, never by how much, so the ETag contract holds.
-- Postgres allows a transition table only on a single-event trigger, hence three.
CREATE OR REPLACE FUNCTION bump_application_list_revision()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE users
    SET applications_revision = applications_revision + 1
    WHERE id IN (SELECT DISTINCT tenant_id FROM changed);
    RETURN NULL;
END;
$$;

DROP TRIGGER IF EXISTS applications_list_revision ON applications;
DROP TRIGGER IF EXISTS applications_list_revision_insert ON applications;
DROP TRIGGER IF EXISTS applications_list_revision_update ON applications;
DROP TRIGGER IF EXISTS applications_list_revision_delete ON applications;

CREATE TRIGGER applications_list_revision_insert
AFTER INSERT ON applications
REFERENCING NEW TABLE AS changed
FOR EACH STATEMENT EXECUTE FUNCTION bump_application_list_revision();

CREATE TRIGGER applications_list_revision_update
AFTER UPDATE ON applications
REFERENCING NEW TABLE AS changed
FOR EACH STATEMENT EXECUTE FUNCTION bump_application_list_revision();

CREATE TRIGGER applications_list_revision_delete
AFTER DELETE ON applications
REFERENCING OLD TABLE AS changed
FOR EACH STATEMENT EXECUTE FUNCTION bump_application_list_revision();
