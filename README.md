# OSApplyTrack

[![License: Apache-2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](./LICENSE)

Open-source, multi-tenant, self-hostable job-application tracker. Track every
application through its pipeline (lead → applied → screen → onsite → offer), keep
per-user search criteria and a company blacklist, and let a background poller
discover fresh remote roles from public job boards and stage them as leads.

It's a polyglot backend behind one vanilla-JS single-page app:

- **API — ASP.NET Core (.NET 10):** magic-link auth, server-side sessions, CRUD,
  and it serves the SPA. Dapper + Npgsql over Postgres; DbUp migrations.
- **Poller — Python:** a cron worker that fetches and scores job listings and
  writes new leads. Reuses the original `applytrack` fetchers.
- **Postgres:** the two runtimes never call each other — **the database schema is
  the contract**. Every query in both runtimes unconditionally filters
  `WHERE tenant_id`.

```
            ┌─────────────────────────────────┐
Browser ──► │ ASP.NET Core (.NET 10, Kestrel)  │
 (the SPA)  │  • serves the SPA + JSON API     │──┐
            │  • magic-link auth + sessions    │  │
            └─────────────────────────────────┘  ├──► Postgres  (shared schema
            ┌─────────────────────────────────┐  │              = the contract)
 Cron  ───► │ Python poller                    │──┘
            │  • fetch + score job leads       │
            └─────────────────────────────────┘
```

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

**Email.** The default sender writes the magic link to the console. For a real
deployment, swap in an SMTP/HTTP sender behind `IEmailSender` (deferred for v1).

**Behind HTTPS.** Front the API with a TLS-terminating reverse proxy
(Caddy, nginx, or `tailscale serve`). The API honors `X-Forwarded-Proto`, so the
session cookie gets its `Secure` flag automatically. Don't expose Kestrel directly
to the internet.

## First-run import (optional)

If you're coming from the original single-user `applytrack`, import your existing
Markdown applications. **Sign in first** — `tenant_id` is a real foreign key to your
user account (so deleting the account cascades cleanly), which means a tenant must
exist before any data is written under it. Then point the importer at your
`applications/` folder and your tenant id:

```sh
docker compose run --rm \
  -v "$PWD/applications:/data" \
  --entrypoint applytrack \
  poller import-md --dir /data --tenant 1
```

## Your data

- **Export** — `GET /api/account/export` returns a zip: one Markdown file per
  application plus a `settings.json` with your criteria and blacklist. A real
  backup, and the door's never locked.
- **Delete** — `DELETE /api/account` removes your account and, via
  `ON DELETE CASCADE`, every row that belongs to it (applications, search profile,
  blacklist, seen ledger, queued polls, sessions, tokens) in one statement.

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

The poller CLI:

- `applytrack poll` — full poll across every active tenant (the hourly cron).
- `applytrack poll --drain` — service only the on-demand poll queue (the fast cron).
- `applytrack poll --tenant <id>` — poll a single tenant.
- `applytrack import-md --dir <path> --tenant <id>` — one-shot Markdown import.

### Tests

```sh
# .NET — xUnit + Testcontainers (needs a running Docker daemon)
cd api && dotnet test

# Python — pytest (offline; no DB/network), plus lint + types
pytest
ruff check .
mypy src
```

## License

[Apache-2.0](./LICENSE). Copyright 2026 Aaron K. Clark.
