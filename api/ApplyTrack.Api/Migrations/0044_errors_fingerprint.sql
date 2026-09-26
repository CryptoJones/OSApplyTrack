-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The Errors view's ETag (#349) reads the tenant's newest evidence, its failures inside
-- the retry window, and its newest packet on every 5-second poll; these keep each an
-- index probe (or a short range) instead of a scan of the tenant's history.
CREATE INDEX IF NOT EXISTS agent_evidence_tenant_id_idx ON agent_evidence (tenant_id, id);
CREATE INDEX IF NOT EXISTS agent_evidence_tenant_failed_idx ON agent_evidence (tenant_id, created_at)
    WHERE kind = 'failed';
CREATE INDEX IF NOT EXISTS agent_events_tenant_packet_idx ON agent_events (tenant_id, created_at DESC)
    WHERE kind = 'packet';
