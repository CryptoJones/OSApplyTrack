-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- First migration: enable Postgres extensions the later schema relies on.
-- citext backs case-insensitive unique emails (users.email) in Step 2.
CREATE EXTENSION IF NOT EXISTS citext;
