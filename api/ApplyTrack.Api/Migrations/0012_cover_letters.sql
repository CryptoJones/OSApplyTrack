-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Materials engine: the generated cover letter for an application. v1 stores the
-- drafted body as plain text/Markdown (LaTeX/PDF is a later module). One letter
-- per application — re-drafting overwrites. Keyed to the application by its slug
-- (tenant_id, application_name), with a composite FK to applications so deleting
-- an app drops its letter; the application FK in turn rides users' ON DELETE
-- CASCADE, so account deletion cleans these up too.
CREATE TABLE cover_letters (
    tenant_id        bigint NOT NULL,
    application_name text   NOT NULL,
    body             text   NOT NULL DEFAULT '',
    model            text   NOT NULL DEFAULT '',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, application_name),
    FOREIGN KEY (tenant_id, application_name)
        REFERENCES applications (tenant_id, name) ON DELETE CASCADE
);
