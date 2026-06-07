# OSApplyTrack — sprint tracker

Lightweight status board for the build. **[`plan.md`](./plan.md) is the source of
truth** for scope and design; this file just tracks where each sprint stands.

Status legend: ✅ done · 🚧 in progress · ⬜ not started

| Sprint | Scope | Status |
| ------ | ----- | ------ |
| **Step 0** | Test scaffolding + repo split + license setup | ✅ done |
| **Step 1** | .NET API skeleton + Postgres storage; serve the SPA verbatim (single-user, bootstrap `tenant_id=1`) | ⬜ not started |
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

## Up next — Step 1

The first user-visible milestone: the existing vanilla-JS SPA loads and works
**identically**, now served by .NET off Postgres. See `plan.md` § "Step 1" for the
`applications`/`search_profiles`/`blacklist` schema, the tenant-scoped
`ApplicationRepo`, the slug choke-point, the optimistic-lock 409 flow, and the 8
SPA-contract endpoints.
