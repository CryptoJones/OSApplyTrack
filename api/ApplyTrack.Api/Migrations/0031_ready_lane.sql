-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The Ready lane, worked in bulk (#183, #186, #188).
--
-- `prepare`: a submit request that means "rebuild the packet first, where the browser
-- is". The api container has no browser on any shipped deployment shape, so a packet
-- prepared in the request thread never sees FormDiscoverer and every non-Greenhouse
-- packet got the standard seven questions. Queued with prepare = true, the worker
-- judges (reusing a recorded verdict), builds the packet with discovery, and goes
-- straight on to the dry run in the same claim.
ALTER TABLE submit_requests
    ADD COLUMN IF NOT EXISTS prepare boolean NOT NULL DEFAULT false;

-- The salary expectation was one bare figure. A form that asks per month, per hour, or
-- in another currency was refused (correctly — no guessing across units), which meant
-- every such question needed the person, every time. With the period and the currency
-- stated, the drafter converts within the same currency and still refuses across
-- currencies. Blank keeps the old behaviour: whatever the figure itself says.
ALTER TABLE agent_settings
    ADD COLUMN IF NOT EXISTS salary_period   text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS salary_currency text NOT NULL DEFAULT '';
