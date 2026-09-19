-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Remove duplicate aggregator lead entries where the application already exists in ready or pipeline (#269).

DELETE FROM applications
WHERE status = 'lead'
  AND company ILIKE '%AgileGrid%'
  AND role ILIKE '%Backend Engineer%'
  AND EXISTS (
      SELECT 1 FROM applications other
      WHERE other.tenant_id = applications.tenant_id
        AND other.id != applications.id
        AND other.status != 'lead'
        AND other.company ILIKE '%AgileGrid%'
        AND other.role ILIKE '%Backend Engineer%'
  );
