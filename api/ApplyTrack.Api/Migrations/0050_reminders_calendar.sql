-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Follow-up and interview reminders, and a calendar feed (#354).
--
-- app_events: dated events on an application beyond its status — one row per interview
-- round for now (kind = 'interview'), keyed like status_events so the timeline merges
-- the two. `at` is an instant; `note` is free text (who, where, the video link).
CREATE TABLE IF NOT EXISTS app_events (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id      bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    application_id bigint NOT NULL REFERENCES applications (id) ON DELETE CASCADE,
    kind           text   NOT NULL DEFAULT 'interview' CHECK (kind IN ('interview')),
    at             timestamptz NOT NULL,
    note           text   NOT NULL DEFAULT '',
    created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS app_events_app_idx ON app_events (tenant_id, application_id, at);
CREATE INDEX IF NOT EXISTS app_events_tenant_at_idx ON app_events (tenant_id, at);

-- The daily reminder: follow-ups due today and interviews in the next day, through the
-- same channels and per-event toggles as the rest (#355) — on by default, like the other
-- events, since a channel still has to be switched on. reminder_hour is the UTC hour it
-- goes out at; reminder_last_day is the day last claimed, so no day is sent twice.
ALTER TABLE notification_settings
    ADD COLUMN IF NOT EXISTS notify_followup_due boolean  NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS reminder_hour       smallint NOT NULL DEFAULT 12
        CHECK (reminder_hour BETWEEN 0 AND 23),
    ADD COLUMN IF NOT EXISTS reminder_last_day   date;

-- Secret tokens that stand in for a session where a cookie can't go. Stored by sha256
-- only; the plaintext is shown once. Scoped: a 'calendar' token opens the tenant's ICS
-- feed and nothing else. The personal API tokens (#356) are meant to live here too,
-- with more scopes; one calendar token per tenant, so a new one revokes the old.
CREATE TABLE IF NOT EXISTS api_tokens (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    scope        text   NOT NULL CHECK (scope IN ('calendar')),
    token_hash   bytea  NOT NULL UNIQUE,
    created_at   timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz
);

CREATE UNIQUE INDEX IF NOT EXISTS api_tokens_calendar_idx ON api_tokens (tenant_id) WHERE scope = 'calendar';
