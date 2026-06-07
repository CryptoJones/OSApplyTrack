-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Step 5 data-delete: wire every per-tenant table's tenant_id to users(id) with
-- ON DELETE CASCADE. With these in place, DELETE /api/account is a single
-- `DELETE FROM users WHERE id = @t` that drops the tenant's applications, search
-- profile, blacklist, seen ledger, and queued poll requests in one statement.
-- sessions and magic_tokens already cascade on user_id (0005/0006).
--
-- Runs once via DbUp; on a fresh DB every table is empty so the constraints add
-- cleanly. Because tenant_id is now a real FK, a tenant must exist (sign in) before
-- any row is written under it — so `applytrack import-md --tenant N` must target an
-- already-signed-in tenant id. See README "First-run import".
ALTER TABLE applications
    ADD CONSTRAINT applications_tenant_fk
    FOREIGN KEY (tenant_id) REFERENCES users (id) ON DELETE CASCADE;

ALTER TABLE search_profiles
    ADD CONSTRAINT search_profiles_tenant_fk
    FOREIGN KEY (tenant_id) REFERENCES users (id) ON DELETE CASCADE;

ALTER TABLE blacklist
    ADD CONSTRAINT blacklist_tenant_fk
    FOREIGN KEY (tenant_id) REFERENCES users (id) ON DELETE CASCADE;

ALTER TABLE seen
    ADD CONSTRAINT seen_tenant_fk
    FOREIGN KEY (tenant_id) REFERENCES users (id) ON DELETE CASCADE;

ALTER TABLE poll_requests
    ADD CONSTRAINT poll_requests_tenant_fk
    FOREIGN KEY (tenant_id) REFERENCES users (id) ON DELETE CASCADE;

-- The other per-tenant tables lead their PK/index with tenant_id, so cascade
-- deletes find their rows by index. poll_requests' PK is on id, so give the
-- cascade (and the worker's per-tenant claim) an index to ride.
CREATE INDEX poll_requests_tenant_idx ON poll_requests (tenant_id);
