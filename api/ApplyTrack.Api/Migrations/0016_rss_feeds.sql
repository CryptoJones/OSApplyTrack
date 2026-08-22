-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Custom RSS/Atom job feeds a tenant follows, alongside the built-in sources and
-- the ATS boards. A jsonb array of absolute http(s) feed URLs, normalized by both
-- runtimes through Criteria (junk and non-http(s) entries dropped). The Python
-- poller reads this column directly, so it is part of the cross-runtime contract.
ALTER TABLE search_profiles
    ADD COLUMN rss_feeds jsonb NOT NULL DEFAULT '[]'::jsonb;
