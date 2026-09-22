-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- "Sign in now" for a signed-in source's board account (LinkedIn, MyGreenhouse, Handshake).
-- The agent renews a kept session on its own clock — once every six hours after a try that
-- stopped on the LinkedIn app's tap or a mailed PIN — and the tap has to come within four
-- minutes of the moo, at an hour nobody chose: 2026-09-21 to 09-22 every attempt ran out
-- unanswered and twenty-five Easy Apply packets failed for want of a session. The person
-- asks for the sign-in when they have their phone in hand; the worker takes the request on
-- its next tick and clears it. Only the .NET agent reads this column.

ALTER TABLE board_accounts
    ADD COLUMN IF NOT EXISTS renew_requested_at timestamptz;
