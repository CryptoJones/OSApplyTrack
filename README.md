# OSApplyTrack

> ### Your job hunt, self-hosted and on autopilot.

[![CI](https://github.com/CryptoJones/OSApplyTrack/actions/workflows/ci.yml/badge.svg)](https://github.com/CryptoJones/OSApplyTrack/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](./LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-239120.svg)](https://learn.microsoft.com/dotnet/csharp/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-512BD4.svg?logo=dotnet&logoColor=white)](https://learn.microsoft.com/aspnet/core/)
[![Python 3.12+](https://img.shields.io/badge/Python-3.12+-3776AB.svg?logo=python&logoColor=white)](https://www.python.org/)
[![PostgreSQL 17](https://img.shields.io/badge/PostgreSQL-17-4169E1.svg?logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![Docker](https://img.shields.io/badge/Docker-Compose-2496ED.svg?logo=docker&logoColor=white)](https://docs.docker.com/compose/)
[![JavaScript](https://img.shields.io/badge/JavaScript-vanilla-F7DF1E.svg?logo=javascript&logoColor=black)](./api/ApplyTrack.Api/wwwroot)
[![Multi-tenant](https://img.shields.io/badge/multi--tenant-yes-success.svg)](#data-model)
[![Self-hosted](https://img.shields.io/badge/self--hosted-yes-success.svg)](#quickstart-docker)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](#contributing)

**Open-source, multi-tenant, self-hostable job-application tracker.** Track every
application through its pipeline (lead → applied → screen → onsite → offer), keep
per-user search criteria and a company blacklist, and let a background poller
discover fresh remote roles from public job boards and stage them as leads — so
your pipeline refills itself while you sleep.

Run it on your laptop with one `docker compose up`, or self-host it for your whole
job search. Your data is yours: one-click export to plain Markdown, one-call
account deletion, no lock-in, no telemetry, no SaaS.

<p align="center">
  <img src="docs/screenshot.png" alt="OSApplyTrack — the multi-lane pipeline dashboard with auto-discovered leads" width="900">
</p>

---

## Table of contents

- [Why OSApplyTrack](#why-osapplytrack)
- [Architecture](#architecture)
- [Quickstart (Docker)](#quickstart-docker)
- [How it works](#how-it-works)
- [Configuration](#configuration)
- [API reference](#api-reference)
- [Data model](#data-model)
- [The discovery poller](#the-discovery-poller)
- [Security & hardening](#security--hardening)
- [Your data](#your-data)
- [First-run import](#first-run-import-optional)
- [Local development](#local-development)
- [Tests](#tests)
- [Project layout](#project-layout)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)

---

## Why OSApplyTrack

- **It tracks the whole funnel.** Every application is a row with a status lane,
  company, role, link, location, salary, source, contacts, applied/follow-up dates,
  a relevance score, and free-form Markdown notes.
- **It finds work for you.** A Python poller fetches listings from public job
  boards, scores them against your saved criteria, drops anything from a
  blacklisted company, dedupes against what you've already seen, and stages the
  survivors as fresh leads.
- **It's genuinely multi-tenant.** Every row is owned by a tenant; every query in
  both runtimes unconditionally filters `WHERE tenant_id`. One deployment cleanly
  serves many users with hard data isolation.
- **It's yours to keep.** Export a real backup (Markdown + settings) any time;
  delete your account and every row it owns cascades away in a single statement.
- **It's a single-binary-feeling deploy.** Postgres + a .NET API that also serves
  the SPA + a Python cron worker — three containers, one `docker compose up`.

## Architecture

A polyglot backend behind one dependency-free vanilla-JS single-page app:

- **API — ASP.NET Core (.NET 10):** magic-link auth, opaque server-side sessions,
  CRUD, and it serves the SPA. Dapper + Npgsql over Postgres; DbUp migrations run
  on startup. Minimal APIs on Kestrel.
- **Poller — Python:** a cron worker that fetches and scores job listings and
  writes new leads. Reuses the original `applytrack` fetchers (`httpx` + `psycopg3`).
- **Postgres:** the two runtimes never call each other — **the database schema is
  the contract.** The .NET API owns auth/sessions + CRUD and migrates the schema;
  the poller writes leads and reads profiles/seen/users. Both filter `tenant_id`.

```
            ┌─────────────────────────────────┐
Browser ──► │ ASP.NET Core (.NET 10, Kestrel)  │
 (the SPA)  │  • serves the SPA + JSON API     │──┐
            │  • magic-link auth + sessions    │  │
            │  • CRUD + criteria + blacklist   │  │
            └─────────────────────────────────┘  ├──► Postgres  (shared schema
            ┌─────────────────────────────────┐  │              = the contract)
 Cron  ───► │ Python poller                    │──┘
            │  • fetch + score + dedupe leads  │
            │  • drain the on-demand poll queue│
            └─────────────────────────────────┘
```

The decoupling is deliberate: the API can answer "Poll now" instantly by enqueuing
a request, while the poller drains that queue out of band. Neither runtime blocks
on the other; the only thing they share is the database.

## Quickstart (Docker)

```sh
cp .env.example .env        # optional: edit the Postgres credentials / API port
docker compose up --build   # brings up db + api + poller
```

Open **http://localhost:8080**.

To sign in, enter your email. In the default configuration the magic link is
**printed to the API logs** instead of being mailed (zero email setup needed):

```sh
docker compose logs api | grep magic-link
```

Open that link and you're in. The first account created is tenant `1`.

> **Tip:** the poller is the third service (`poller`). `docker compose up` starts
> all three; if you only bring up `db` + `api`, no leads will ever be discovered
> because nothing drains the queue or runs the scheduled poll.

## How it works

**Sign-in (magic link).** `POST /api/auth/request` always returns `200 {ok:true}`
— whether or not the address exists — so the surface can't be used to enumerate
accounts. Behind that uniform response, a known/valid address gets a single-use,
15-minute token (only its SHA-256 is stored). `GET /api/auth/verify` consumes the
token, mints a 30-day **server-side** session (not a JWT — so logout is instant
revocation), sets an `HttpOnly` cookie, and redirects to `/` so the token leaves
the URL and browser history.

**The tenancy choke-point.** A middleware resolves the session cookie to a
`TenantContext` and is the only thing that lets `/api/*` through. Repositories are
injected from DI already scoped to the caller's tenant, so endpoint code physically
can't query another tenant's rows.

**Optimistic concurrency.** Each application carries a `version`. Writes accept
`?expected_version=` and answer **409 Conflict** on a mismatch, driving the SPA's
overwrite-confirm flow — two tabs can't silently clobber each other.

**Discovery.** The poller fetches sources once per pass, scores each listing
against the tenant's criteria, drops blacklisted companies, dedupes against the
`seen` ledger, and inserts the rest as `lead`-status applications.

## Configuration

All configuration is environment variables (see [`.env.example`](./.env.example)):

| Variable | Default | Purpose |
| --- | --- | --- |
| `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` | `applytrack` | Postgres credentials, shared by `db`, `api`, and `poller`. |
| `API_PORT` | `8080` | Host port the API publishes (the container always listens on 8080). |
| `DRAIN_INTERVAL` | `60` | Seconds between drains of the on-demand poll queue (the SPA's "Poll now" button). |
| `POLL_INTERVAL` | `3600` | Seconds between full multi-tenant polls. |
| `ConnectionStrings__Postgres` | _(compose default)_ | Override to point the API at an external Postgres. |
| `DATABASE_URL` | _(compose default)_ | Override to point the poller at an external Postgres (libpq URL). |
| `APPLYTRACK_DIR` | `./applications` | Default folder the `import-md` command reads when `--dir` is omitted. |

## API reference

All `/api/*` routes except the auth handshake require a valid session cookie;
unauthenticated calls get **401** with a `{"detail": "..."}` body. `/health` is
open. Error bodies are uniform `{"detail": "..."}` across 400/404/409/500.

### Auth

| Method | Path | Notes |
| --- | --- | --- |
| `POST` | `/api/auth/request` | Body `{email}`. Always `200 {ok:true}` (no account enumeration). Per-IP rate-limited. |
| `GET`  | `/api/auth/verify?token=…` | Consumes a single-use token, sets the session cookie, 302 → `/`. |
| `POST` | `/api/auth/logout` | Drops the session row (instant revocation) and clears the cookie. |
| `GET`  | `/api/auth/me` | `{email}` for the current session, else 401. |

### Applications

| Method | Path | Notes |
| --- | --- | --- |
| `GET`    | `/api/apps` | List the tenant's applications. |
| `GET`    | `/api/stats` | Counts by `{status, lane}`. |
| `GET`    | `/api/apps/{name}` | One application: `{filename, raw, fields, version, material}`. |
| `POST`   | `/api/apps` | Create from structured fields → `201 {filename}`. |
| `PUT`    | `/api/apps/{name}?expected_version=…` | Update structured fields (409 on version mismatch). |
| `PUT`    | `/api/apps/{name}/raw?expected_version=…` | Replace the full Markdown document. |
| `DELETE` | `/api/apps/{name}` | Delete → `204`. |
| `POST`   | `/api/poll` | Enqueue an on-demand poll → `{count:0}`. Rate-limited; the worker drains it. |

### Criteria & blacklist

| Method | Path | Notes |
| --- | --- | --- |
| `GET`    | `/api/criteria` | The tenant's discovery criteria (defaults when unset). |
| `PUT`    | `/api/criteria` | Normalize + store posted criteria (junk dropped, score clamped). |
| `GET`    | `/api/blacklist` | List blacklisted companies. |
| `POST`   | `/api/blacklist` | Add a company; flips its open leads to `passed`. |
| `POST`   | `/api/apps/{name}/blacklist` | Blacklist the company on a given application. |
| `DELETE` | `/api/blacklist/{company}` | Remove a company. |

### Account

| Method | Path | Notes |
| --- | --- | --- |
| `GET`    | `/api/account/export` | A zip: one Markdown file per application + `settings.json`. |
| `DELETE` | `/api/account` | Delete the account; every owned row cascades away. |

### Not in v1

`GET /api/apps/{name}/check-link` and `POST /api/apps/{name}/draft` answer **501**
with a `{detail}` body the SPA surfaces as a clean toast (see [Roadmap](#roadmap)).

## Data model

The schema is migrated by **DbUp** from idempotent `.sql` scripts under
`api/ApplyTrack.Api/Migrations/`, run automatically on API startup:

| Table | Holds |
| --- | --- |
| `users` | Accounts. A user's `id` **is** its `tenant_id` (tenants are users). |
| `applications` | The tracked applications. `UNIQUE (tenant_id, name)`; `version` for optimistic locking. |
| `search_profiles` | Per-tenant discovery criteria the poller reads. |
| `blacklist` | Per-tenant blocked companies. |
| `magic_tokens` | SHA-256 of issued login tokens, with expiry. Single-use. |
| `sessions` | Opaque server-side sessions (instant revocation on logout). |
| `seen` | The dedupe ledger — listings already surfaced, so leads don't repeat. |
| `poll_requests` | The on-demand "Poll now" queue the worker drains. |

Account deletion relies on `ON DELETE CASCADE` foreign keys (migrations
`0005`/`0006`/`0009`): one `DELETE FROM users` removes every dependent row.

## The discovery poller

The poller is a single container running two loops with no cron daemon
(see [`docker/poller-entrypoint.sh`](./docker/poller-entrypoint.sh)):

- **Fast lane** — drains the on-demand queue every `DRAIN_INTERVAL` seconds, so the
  "Poll now" button doesn't wait for the hourly pass.
- **Slow lane** — a full multi-tenant poll every `POLL_INTERVAL` seconds.

A transient board/DB failure can't kill either loop; the next tick retries. Prefer
host cron or a systemd timer? Run the CLI directly and drop the service:

| Command | What it does |
| --- | --- |
| `applytrack poll` | Full poll across every active tenant (the hourly cron). |
| `applytrack poll --drain` | Service only the on-demand poll queue (the fast cron). |
| `applytrack poll --tenant <id>` | Poll a single tenant. |
| `applytrack poll --limit <n>` | Cap results scanned per source (default 40). |
| `applytrack import-md --dir <path> --tenant <id>` | One-shot Markdown import. |

Each accepts `--database-url` (a libpq URL), falling back to `DATABASE_URL` / the
`POSTGRES_*` env vars.

## Security & hardening

OSApplyTrack is built to face the public internet behind a reverse proxy:

- **No account enumeration.** `POST /api/auth/request` returns an identical `200`
  for known, unknown, and malformed addresses.
- **Single-use, short-lived tokens.** Login tokens are 15-minute, one-shot, and
  stored only as SHA-256. Sessions are opaque and server-side, so logout revokes
  instantly (no stranded JWTs).
- **Hard tenant isolation.** Repositories are DI-scoped per tenant; every query
  filters `tenant_id`. There is no endpoint path that reads across tenants.
- **Strict security headers on every response** (custom middleware): a tight
  `Content-Security-Policy` (`script-src 'self'`, no inline scripts),
  `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`, and HSTS once the request is HTTPS.
- **Output sanitization.** User Markdown is rendered with `marked` and scrubbed
  through **DOMPurify** before it touches the DOM — defense in depth against stored
  XSS even though the poller already strips HTML at ingestion.
- **Rate limiting.** The magic-link and poll endpoints are per-IP fixed-window
  rate-limited so the always-200 auth surface can't be abused for spam or probing.
- **SSRF-hardened link probing.** The link prober refuses to connect to
  private/loopback/link-local/reserved addresses and re-checks every redirect hop,
  so a hostile listing URL can't pivot into your network.
- **Behind HTTPS.** Front the API with a TLS-terminating reverse proxy (Caddy,
  nginx, or `tailscale serve`). The API honors `X-Forwarded-Proto`, so the session
  cookie's `Secure` flag is set automatically. Don't expose Kestrel directly.
- **Change the default password.** For any deployment reachable beyond `localhost`,
  change `POSTGRES_PASSWORD` (and the matching connection string) from the
  `applytrack` default before first boot — the bundled value is local-dev only.
- **Dependency CVE watch.** [`.forgejo/workflows/audit.yml`](./.forgejo/workflows/audit.yml)
  runs `dotnet list package --vulnerable --include-transitive` and `pip-audit` on
  every push/PR and weekly, failing the build on a known-vulnerable dependency. Run
  the same two commands locally any time.

## Your data

- **Export** — `GET /api/account/export` returns a zip: one Markdown file per
  application plus a `settings.json` with your criteria and blacklist. A real
  backup, and the door's never locked.
- **Delete** — `DELETE /api/account` removes your account and, via
  `ON DELETE CASCADE`, every row that belongs to it (applications, search profile,
  blacklist, seen ledger, queued polls, sessions, tokens) in one statement.

## First-run import (optional)

If you're coming from the original single-user `applytrack`, import your existing
Markdown applications. **Sign in first** — `tenant_id` is a real foreign key to your
user account (so deleting the account cascades cleanly), which means a tenant must
exist before any data is written under it. Then point the importer at your
`applications/` folder and **your** tenant id (find it in the API logs or the
`users` table — it's not necessarily `1` if other accounts exist):

```sh
docker compose run --rm \
  -v "$PWD/applications:/data" \
  --entrypoint applytrack \
  poller import-md --dir /data --tenant <your-tenant-id>
```

## Local development

Run Postgres in a container and the two runtimes on the host:

```sh
docker compose up -d db

# API (reads appsettings.json → localhost Postgres)
cd api && dotnet run --project ApplyTrack.Api

# Poller (one-shot poll; needs DATABASE_URL or the POSTGRES_* / PG* env vars)
pip install -e '.[dev]'
DATABASE_URL=postgresql://applytrack:applytrack@localhost:5432/applytrack applytrack poll
```

## Tests

```sh
# .NET — xUnit + Testcontainers (needs a running Docker daemon)
cd api && dotnet test

# Python — pytest (offline; no DB/network), plus lint + types
pytest
ruff check .
mypy src
```

The .NET suite drives the live HTTP stack with `WebApplicationFactory` against a
throwaway Postgres (Testcontainers), including the auth spine and cross-tenant
isolation. The Python suite is fully offline (fakes for the DB and HTTP transport).

## Project layout

```
api/                      the .NET solution
  ApplyTrack.Api/         Minimal API host
    Endpoints/            auth, apps, criteria, blacklist, account
    Middleware/           tenancy choke-point, security headers, error mapping
    Migrations/           DbUp .sql scripts (the schema = the contract)
    wwwroot/              the vanilla-JS SPA (served by the API)
  ApplyTrack.Api.Tests/   xUnit + Testcontainers
src/applytrack/           the Python poller + CLI
docker/                   poller entrypoint (two-cadence loop)
docker-compose.yml        db + api + poller
Dockerfile.poller         the poller image
```

## Roadmap

v1 is intentionally focused. Deferred, with clean seams already in place:

- **Real email delivery.** The default `IEmailSender` writes the magic link to the
  console; swap in an SMTP/HTTP sender behind the interface for a real deployment.
- **Link checking.** `/api/apps/{name}/check-link` returns 501 today; the
  SSRF-hardened prober already exists in the poller for when it's enabled.
- **LLM cover-letter drafting.** `/api/apps/{name}/draft` returns 501; the
  materials/LLM engine is out of v1 scope.

## Contributing

Issues and PRs welcome. Please keep the cross-runtime contract intact (every query
filters `tenant_id`), add tests for new behavior, and keep the SPA dependency-free.
Both test suites and the dependency audit run in CI.

## License

[Apache-2.0](./LICENSE). Copyright 2026 Aaron K. Clark.
