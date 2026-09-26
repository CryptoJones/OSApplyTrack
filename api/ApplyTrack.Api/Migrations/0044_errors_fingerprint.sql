-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The Errors view's ETag (#349) reads the tenant's newest evidence and newest packet on
-- every 5-second poll; these keep both an index probe instead of a scan of the tenant.
CREATE INDEX IF NOT EXISTS agent_evidence_tenant_id_idx ON agent_evidence (tenant_id, id);
CREATE INDEX IF NOT EXISTS agent_events_tenant_packet_idx ON agent_events (tenant_id, created_at DESC)
    WHERE kind = 'packet';
