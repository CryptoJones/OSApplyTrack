-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Tenant-provided closing signature appended to generated cover letters.
ALTER TABLE llm_settings
    ADD COLUMN IF NOT EXISTS cover_letter_signature text NOT NULL DEFAULT '';
