-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- LinkedIn Easy Apply pacing (#278) counts the tenant's recent real submissions before
-- every submit; this keeps that count a short range instead of a scan of all evidence (#352).
CREATE INDEX IF NOT EXISTS agent_evidence_tenant_submitted_idx ON agent_evidence (tenant_id, created_at)
    WHERE kind = 'submitted';
