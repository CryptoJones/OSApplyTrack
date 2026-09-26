-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Per-tenant daily count of model requests made on the operator's instance LLM key
-- (#346), so open signup can't be used to spend it without bound (Llm__DailyCapPerTenant).
-- One row per tenant per UTC day; rows go with the account.
CREATE TABLE IF NOT EXISTS llm_usage (
    tenant_id bigint  NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    day       date    NOT NULL,
    calls     integer NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, day)
);
