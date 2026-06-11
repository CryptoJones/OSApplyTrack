# OSApplyTrack — sprint tracker

Lightweight status board for the build. **[`plan.md`](./plan.md) is the source of
truth** for scope and design; this file just tracks where each sprint stands.

Status legend: ✅ done · 🚧 in progress · ⬜ not started

| Sprint | Scope | Status |
| ------ | ----- | ------ |
| **Step 0** | Test scaffolding + repo split + license setup | ✅ done |
| **Step 1** | .NET API skeleton + Postgres storage; serve the SPA verbatim (single-user, bootstrap `tenant_id=1`) | ✅ done |
| **Step 2** | Auth + tenancy spine (magic-link, server-side sessions, tenant choke-point) | ✅ done |
| **Step 3** | Per-user search profiles (Python poller reads them) | ✅ done |
| **Step 4** | Per-tenant cron + `seen` table (Python worker, fetch-once-per-run) | ✅ done |
| **Step 5** | Self-host packaging + data export/delete (replaces billing) | ✅ done |

## Step 0 — done

Shipped:
- **Python poller ported** from the original applytrack, trimmed to the poller +
  CLI (`src/applytrack/`), 27 pytest tests green (criteria/poll/fetchers), offline
  `run_poll(listings=)` seam preserved.
- **Empty .NET 10 solution** (`api/ApplyTrack.slnx`) with `ApplyTrack.Api`
  (Dapper + Npgsql + DbUp) and a DbUp migration runner (`Data/Migrator.cs`,
  first migration `0000_extensions.sql` enabling `citext`). `/health` endpoint.
- **.NET test project** (`ApplyTrack.Api.Tests`) — xUnit + Testcontainers-Postgres,
  real DbUp upgrade in the fixture; 2 tests green.
- **Apache-2.0** license set up: root `LICENSE`, `pyproject.toml`, `.csproj`
  metadata, SPDX headers on every source file.
- **Runs locally *and* as a container:** env-driven connection string;
  `docker-compose.yml` (Postgres + API) + `Dockerfile`; both run modes verified.

## Step 1 — done

The first user-visible milestone: the existing vanilla-JS SPA loads and works
**identically**, now served by .NET off Postgres (bootstrap `tenant_id=1`, no auth).

Shipped:
- **Schema** (DbUp): `applications` (15 free-text fields + `version` for optimistic
  locking, `UNIQUE(tenant_id,name)`, `(tenant_id,status)` index), `search_profiles`
  (criteria columns the Python poller reads in Steps 3-4), `blacklist`.
- **Domain + codec ported to C#:** `AppFields`, `Slug` (the `safe_name` heir),
  `MarkdownCodec` (frontmatter render/parse), `Criteria` (loose-JSON normalizer).
- **Tenant-scoped Dapper repos:** `ApplicationRepo` (CRUD, list ordering, stats,
  `version`-column optimistic lock → 409), `CriteriaRepo` (defaults-if-absent +
  upsert), `BlacklistRepo` (normalized keys + pass-open-leads).
- **Minimal API** written against the SPA contract byte-for-byte: snake_case JSON,
  `?expected_version=` 409 flow, FastAPI-style `{"detail"}` errors. `poll` /
  `check-link` / `draft` answer 501 (out of v1). SPA copied verbatim to `wwwroot/`.
- **Migration CLI (Python):** `applytrack import-md --dir … --tenant 1` reuses
  `parse_app` to upsert legacy Markdown into Postgres (psycopg3).
- **Verified:** 29 .NET tests + 30 pytest green; end-to-end curl sweep of every
  endpoint; 36 real legacy apps imported and served by the API. (No in-browser
  visual test — headless environment.)

## Steps 2–5 — done

All four shipped; **v1 is feature-complete** (every `plan.md` build step is done).

- **Step 2 — Auth + tenancy spine** (`245bc54`): magic-link request/verify,
  single-use SHA-256 tokens (15-min TTL), opaque server-side sessions (30-day,
  instant revocation on logout), and the tenant choke-point middleware that hands
  every endpoint a pre-scoped repo. No account enumeration (`/api/auth/request`
  is always 200).
- **Step 3 — Per-user search profiles** (`4ea1e62`): the Python poller reads each
  tenant's `search_profiles` row and scores listings against it; `/api/criteria`
  round-trips the same snake_case shape the SPA and poller share.
- **Step 4 — Per-tenant cron + seen ledger** (`4119d91`, `ca15974`): the worker
  fetches sources once per pass and fans out across active tenants, deduping
  against the `seen` table; the on-demand `/api/poll` enqueues to `poll_requests`,
  drained on the fast lane.
- **Step 5 — Self-host packaging + export/delete** (`4cc48a1`): three-service
  `docker compose up` (db + api + poller); `GET /api/account/export` (a zip of
  Markdown + `settings.json`) and `DELETE /api/account` (`ON DELETE CASCADE`).

## Post-v1 hardening & polish

- **Security** (`805664e`): fixes for a cross-edition OWASP Top 10 audit — an
  SSRF-hardened link probe, DOMPurify on rendered notes, a strict CSP + security-
  header middleware, per-IP rate limits, generic 500s, and a dependency-audit CI.
- **Docs** (`9d9ccf4`, `3f91e2c`, `7f7f596`): an extensive README (tagline, badge
  row, full API reference, data model, security section) + a dashboard screenshot.

## Post-v1 polish

- ✅ **Pipeline grand-total pill** — the dashboard pipeline strip now renders an
  accent `N total` pill flush-right of the per-status counts (`renderPipeline()`
  in `app.js`, `.pipe-total` in `app.css`). Distinct from the footer `N apps`
  counter, which still tracks the *filtered* view (`shown/total`).
- ✅ **Build is warning-clean** — swapped the obsolete `ForwardedHeadersOptions.
  KnownNetworks` for `KnownIPNetworks` (.NET 10 `ASPDEPR005`).
- ✅ **GitHub CI** (`.github/workflows/ci.yml`) — the Forgejo runner has no Docker,
  so its workflow only audits deps; GitHub's ubuntu runners do, so CI there runs
  *both* suites (Testcontainers .NET + pytest/ruff/mypy) plus the NuGet/pip audit.
  Live CI badge in the README. Its first run caught a stale runner `pip`
  (PYSEC-2026-196); both workflows now upgrade pip before auditing.
- ✅ **v1.0.0 release** (`.github/workflows/release.yml`) — a `v*` tag builds the
  api + poller images and pushes them to GHCR
  (`ghcr.io/cryptojones/osapplytrack-{api,poller}`, semver + `latest`, public) and
  publishes a GitHub Release. Versions bumped to 1.0.0; release + GHCR badges added.
- ✅ **Shared opportunity list** (issue #1) — `GET /api/account/export/shared`
  exports the peer-facing sibling of the migration snapshot
  (`format: applytrack-shared`): slug + company/role/link/location/source only,
  personal state stripped server-side. Import branches on the `format` field:
  every entry lands as a fresh `lead`, slugs already tracked are skipped (new
  `ApplicationRepo.InsertIfAbsentAsync`, `ON CONFLICT DO NOTHING`). 5 new tests.
- ✅ **Settings hub** — one ⚙ Settings masthead button replaces the six scattered
  panel/export buttons; tabs for Criteria, Résumé, AI, Blacklist, Account. New
  surfaces that previously had **no UI**: blacklist view/remove
  (`DELETE /api/blacklist/{company}`), account delete (double-confirm), sign-out.
  Export / Share / Import live under Account.
- ✅ **Port guard rails** — the stack checks before it binds instead of silently
  winning the race for a contested port (8080: vLLM/llama.cpp/dev tools).
  Compose gains a host-network `portcheck` preflight that fails the `api`
  service with a clear message when `API_PORT` is taken; `deploy/quadlet/` now
  ships example rootless-podman units with the same `ExecStartPre` guard.
- ✅ **Cover-letter kill switch** (DbUp 0013) — per-tenant
  `cover_letters_enabled` on `llm_settings`, surfaced as a checkbox in
  Settings · AI. OFF hides every drafting affordance in the SPA and
  `POST /api/apps/{name}/draft` refuses with a clear 400, so tenants who don't
  want to run a model never see or call one. Omitted-means-keep PUT semantics,
  same as `api_key`.

## Backlog / ideas

- ⬜ **`tailscale serve` front-end** — rainy-day: serve over Tailscale instead of a
  self-signed TLS + reverse proxy (see the `plan.md` appendix).
