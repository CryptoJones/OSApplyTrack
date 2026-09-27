-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- When the poller last finished a pass for this tenant (#358), for the operator's
-- `admin usage`. Stamped by src/applytrack/worker.py after each tenant's poll; NULL
-- until the first one. The poller's role may update this column and no other on users.
ALTER TABLE users ADD COLUMN IF NOT EXISTS last_polled_at timestamptz;
