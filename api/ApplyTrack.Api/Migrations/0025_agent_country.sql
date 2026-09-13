-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Country of residence, a standing answer. Greenhouse's rendered application form
-- requires a Country picker that its Job Board API never lists, so every packet
-- built from the API was missing it and every Submit click stopped at "Select a
-- country". Blank means "infer from the résumé's location line".
ALTER TABLE agent_settings
    ADD COLUMN IF NOT EXISTS country text NOT NULL DEFAULT '';
