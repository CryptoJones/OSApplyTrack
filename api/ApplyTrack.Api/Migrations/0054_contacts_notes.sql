-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Contacts and a dated notes log per application (#360).
--
-- contacts: the people behind a process — a recruiter, a hiring manager, a panel —
-- kept once per tenant and linked to as many applications as they turn up in. email,
-- phone, linkedin and notes are personal data and sealed at rest by the API
-- (SecretProtector, as #117); name, role and company stay readable so the list sorts.
-- version is the optimistic lock, as on applications. The application's own contact /
-- contact_email columns stay as they are, for back-compat and the export.
CREATE TABLE IF NOT EXISTS contacts (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id  bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    name       text   NOT NULL,
    email      text   NOT NULL DEFAULT '',
    phone      text   NOT NULL DEFAULT '',
    role       text   NOT NULL DEFAULT '',
    company    text   NOT NULL DEFAULT '',
    linkedin   text   NOT NULL DEFAULT '',
    notes      text   NOT NULL DEFAULT '',
    version    bigint NOT NULL DEFAULT 1,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS contacts_tenant_idx ON contacts (tenant_id, name);

-- Who someone is in one application's process (role: recruiter, hiring manager, panel…).
-- Gone with either side.
CREATE TABLE IF NOT EXISTS application_contacts (
    tenant_id      bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    application_id bigint NOT NULL REFERENCES applications (id) ON DELETE CASCADE,
    contact_id     bigint NOT NULL REFERENCES contacts (id) ON DELETE CASCADE,
    role           text   NOT NULL DEFAULT '',
    created_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (application_id, contact_id)
);

CREATE INDEX IF NOT EXISTS application_contacts_tenant_idx ON application_contacts (tenant_id, application_id);
CREATE INDEX IF NOT EXISTS application_contacts_contact_idx ON application_contacts (tenant_id, contact_id);

-- The interaction log: dated notes on an application, never edited (one can be removed).
-- body is sealed at rest. Keyed like app_events so the timeline merges it.
CREATE TABLE IF NOT EXISTS app_notes (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id      bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    application_id bigint NOT NULL REFERENCES applications (id) ON DELETE CASCADE,
    at             timestamptz NOT NULL DEFAULT now(),
    body           text   NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS app_notes_app_idx ON app_notes (tenant_id, application_id, at);
