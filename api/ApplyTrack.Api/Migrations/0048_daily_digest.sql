-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- The daily applied-jobs digest (#336): one mail a day, through the instance's own SMTP
-- sender to the account's own address, listing the applications marked applied the
-- previous (UTC) day. Off by default, and so is "Insult me". digest_hour is the UTC hour
-- it goes out at; digest_last_day is the day last reported — claimed before the send,
-- so no day is ever mailed twice; digest_last_quip keeps the insult from repeating.
ALTER TABLE notification_settings
    ADD COLUMN IF NOT EXISTS digest_enabled   boolean  NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS digest_insults   boolean  NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS digest_hour      smallint NOT NULL DEFAULT 12
        CHECK (digest_hour BETWEEN 0 AND 23),
    ADD COLUMN IF NOT EXISTS digest_last_day  date,
    ADD COLUMN IF NOT EXISTS digest_last_quip smallint;
