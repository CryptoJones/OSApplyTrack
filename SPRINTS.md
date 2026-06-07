# OSApplyTrack — sprint tracker

Lightweight status board for the build. **[`plan.md`](./plan.md) is the source of
truth** for scope and design; this file just tracks where each sprint stands.

Status legend: ✅ done · 🚧 in progress · ⬜ not started

| Sprint | Scope | Status |
| ------ | ----- | ------ |
| **Step 0** | Test scaffolding + repo split + license setup | ✅ done |
| **Step 1** | .NET API skeleton + Postgres storage; serve the SPA verbatim (single-user, bootstrap `tenant_id=1`) | ✅ done |
| **Step 2** | Auth + tenancy spine (magic-link, server-side sessions, tenant choke-point) | ⬜ not started |
| **Step 3** | Per-user search profiles (Python poller reads them) | ⬜ not started |
| **Step 4** | Per-tenant cron + `seen` table (Python worker, fetch-once-per-run) | ⬜ not started |
| **Step 5** | Self-host packaging + data export/delete (replaces billing) | ⬜ not started |

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

## Up next — Step 2

Auth + tenancy spine: magic-link login, server-side sessions, and the single
tenant choke-point that hands endpoints a pre-scoped `ApplicationRepo`. See
`plan.md` § "Step 2".

## Backlog / ideas

- ⬜ **Total-apps counter** — surface a running count of applications in the SPA
  (e.g. next to the stats). `/api/stats` already aggregates by status/lane; the
  total is the sum, so this is mostly a frontend add.
