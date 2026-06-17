# OSApplyTrack — project instructions

Open-source, multi-tenant, self-hostable job-application tracker. A backend
rewrite of `applytrack` (the original single-user app lives at
`/home/hermes/Source/repos/applytrack`).

**The full implementation plan is in [`./plan.md`](./plan.md). Start at Step 0.**
`plan.md` is the source of truth; this file is just the always-on conventions.

## License & attribution
- **Apache-2.0 licensed** (same license as the original applytrack — no
  relicense). Every source file carries `SPDX-License-Identifier: Apache-2.0` and
  `Copyright 2026 Aaron K. Clark`. Full text in `LICENSE`.
- Attribution `Aaron K. Clark` overrides the global default for this repo.

## Architecture pins (non-negotiable)
- **Core API: .NET 10** — ASP.NET Core Minimal APIs on Kestrel. (".NET Core" is
  just ".NET" since v5; .NET 10 is the current LTS.)
- **Discovery poller: Python** — reuse the original `poll.py` (8 fetchers + HTML
  scrape + `classify`) as a cron worker. **Do NOT rewrite the fetchers in C#.**
- **Data: Dapper + Npgsql** over Postgres. Migrations via **DbUp** (idempotent
  `.sql` scripts). Optimistic locking via a manual `version` column. **Not** EF
  Core, **not** SQLAlchemy.
- **Keep the existing vanilla-JS SPA VERBATIM** (`web/static/*` → the .NET app's
  `wwwroot/`). The API is written *against* the SPA's existing endpoint contract:
  same URLs + JSON shapes, including `?expected_version=` and the 409 conflict
  flow. The SPA is not to be rewritten.
- **Materials / LLM cover-letter engine was out of v1; it landed post-v1, in
  v1.1.0.**
- **⚠ ANY-LLM, AND OPTIONAL:** the cover-letter engine **must run against any
  OpenAI-compatible endpoint** (local Ollama/vLLM/LM Studio or any hosted
  provider) — never hard-code one vendor, never require a specific model or a
  paid key. Drafting is **opt-out per tenant** (`cover_letters_enabled`):
  users who don't want to run a model get no drafting UI and no LLM calls.

## The cross-runtime contract
Two runtimes, one Postgres. **The schema is the contract** — the .NET API and the
Python poller never call each other; they share tables. .NET owns auth/sessions +
CRUD; Python writes leads + reads profiles/seen/users. **Every query in both
runtimes unconditionally filters `WHERE tenant_id`.**

## Repo layout (target, per plan.md)
- `api/` — the .NET solution (`ApplyTrack.Api`), with the SPA in `wwwroot/`.
- `src/applytrack/` — Python, trimmed to the poller + CLI.

## Git / remote
Codeberg auth on this machine is pass-backed (GPG-encrypted); use a tokenless
username-only remote URL (`https://cryptojones@codeberg.org/CryptoJones/<repo>.git`).
Never embed a token in `.git/config`. Note: creating a *new* Codeberg remote may
be blocked by a pending 100-repo-cap raise.
