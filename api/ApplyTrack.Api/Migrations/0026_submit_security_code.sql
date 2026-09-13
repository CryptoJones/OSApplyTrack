-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Greenhouse answers a submission its invisible reCAPTCHA scored as a bot's by
-- emailing the candidate a security code and asking for it on the form. The run
-- parks at that prompt; the human pastes the code here (POST /api/apps/{name}/
-- security-code) and the parked run types it in and clicks Submit again.
ALTER TABLE submit_requests
    ADD COLUMN IF NOT EXISTS security_code text NOT NULL DEFAULT '';
