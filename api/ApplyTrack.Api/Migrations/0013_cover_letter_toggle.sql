-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Per-tenant switch for the cover-letter engine. People who don't want to run a
-- model turn it off: the SPA hides every drafting affordance and the draft
-- endpoint refuses. Default ON so existing tenants keep their current behavior;
-- absence of the llm_settings row also means ON.
ALTER TABLE llm_settings
    ADD COLUMN IF NOT EXISTS cover_letters_enabled boolean NOT NULL DEFAULT true;
