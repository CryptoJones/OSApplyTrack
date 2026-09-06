-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Agentic auto-apply, step 5: the unknown long tail is opt-in. Greenhouse, Lever
-- and Ashby forms are understood (published schema, or discovered read-only in the
-- browser); anything else is a generic form the agent can only fill by accessible
-- name, refusing to click if a required field is unmapped. Off by default because
-- a form the agent has never seen is exactly where a wrong guess is likeliest.
ALTER TABLE agent_settings
    ADD COLUMN IF NOT EXISTS long_tail boolean NOT NULL DEFAULT false;
