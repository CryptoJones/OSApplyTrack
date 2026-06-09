-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Materials engine: per-tenant override of the OpenAI-compatible LLM endpoint the
-- cover-letter drafter calls. The INSTANCE default (base url / model / key) lives
-- in app config (env Llm__BaseUrl / Llm__Model / Llm__ApiKey); this table only
-- holds a tenant's overrides. A blank/NULL field here means "inherit the instance
-- default for that field", so a tenant can override just the model and keep the URL.
-- One row per tenant; absence means "use the instance default for everything".
--
-- api_key_ciphertext is the tenant's key encrypted at rest (AES-GCM via
-- SecretProtector, base64). It is never returned to the client — the API exposes
-- only a has_api_key boolean.
CREATE TABLE llm_settings (
    tenant_id          bigint  PRIMARY KEY
                       REFERENCES users (id) ON DELETE CASCADE,
    base_url           text    NOT NULL DEFAULT '',
    model              text    NOT NULL DEFAULT '',
    api_key_ciphertext text    NOT NULL DEFAULT '',
    updated_at         timestamptz NOT NULL DEFAULT now()
);
