-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Per-tenant company blacklist, the Postgres heir to applications/.blacklist.json.
-- `company` stores the normalized key (lowercased, non-alphanumeric runs -> '-'),
-- the same form the Python poller compares against when skipping companies.
CREATE TABLE blacklist (
    tenant_id  bigint NOT NULL,
    company    text   NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, company)
);
