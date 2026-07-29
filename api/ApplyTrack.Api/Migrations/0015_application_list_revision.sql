-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- A cheap, tenant-scoped validator for GET /api/apps. Every writer goes through
-- the applications table, including the Python poller and account imports, so a
-- database trigger keeps the revision correct without coupling either runtime to
-- cache invalidation logic.
ALTER TABLE users
    ADD COLUMN applications_revision bigint NOT NULL DEFAULT 0;

CREATE FUNCTION bump_application_list_revision()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    affected_tenant bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        affected_tenant := OLD.tenant_id;
    ELSE
        affected_tenant := NEW.tenant_id;
    END IF;

    UPDATE users
    SET applications_revision = applications_revision + 1
    WHERE id = affected_tenant;

    RETURN NULL;
END;
$$;

CREATE TRIGGER applications_list_revision
AFTER INSERT OR UPDATE OR DELETE ON applications
FOR EACH ROW EXECUTE FUNCTION bump_application_list_revision();
