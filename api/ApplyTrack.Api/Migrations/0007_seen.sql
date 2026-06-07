-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The poller's persistent dedup ledger, the Postgres heir to applications/.seen.json.
-- A company must never be pinged twice: each staged lead records its normalized
-- listing URL and its normalized `company + role` slug as two independent keys.
-- `kind` is 'url' or 'slug'; if either key has been seen, the listing is skipped.
-- Keys persist even after the matching application is deleted, so a removed lead is
-- never re-discovered. Written and read only by the Python poller, scoped per tenant.
CREATE TABLE seen (
    tenant_id  bigint NOT NULL,
    kind       text   NOT NULL,
    key        text   NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, kind, key)
);
