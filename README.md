# OSApplyTrack

> ### Your job hunt, self-hosted and on autopilot.

### ▶ [Try out the app here →](https://w3b.cryptojones.dev/OSApplyTrack/)

> Live demo instance. Sign in with a magic link — each visitor gets their own tenant.

[![CI](https://github.com/CryptoJones/OSApplyTrack/actions/workflows/ci.yml/badge.svg)](https://github.com/CryptoJones/OSApplyTrack/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/CryptoJones/OSApplyTrack?logo=github&sort=semver)](https://github.com/CryptoJones/OSApplyTrack/releases/latest)
[![GHCR](https://img.shields.io/badge/ghcr.io-images-2496ED.svg?logo=docker&logoColor=white)](https://github.com/CryptoJones?tab=packages&repo_name=OSApplyTrack)
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
job search. Your data is yours: one-click export to a single JSON snapshot you can
import into any other instance, one-call account deletion, no lock-in, no
telemetry, no SaaS.

<p align="center">
  <img src="docs/screenshot.png" alt="OSApplyTrack — the multi-lane pipeline dashboard with auto-discovered leads" width="900">
</p>

<p align="center">
  <img src="docs/mobile.png" alt="OSApplyTrack application detail view on a phone" width="280">
  <br>
  <em>The responsive list and detail view support touch, keyboard, and screen readers.</em>
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
- [Cover letters](#cover-letters)
- [Security & hardening](#security--hardening)
- [Your data](#your-data)
- [Accessibility](#accessibility)
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
  boards — plus any Greenhouse/Lever board or **custom RSS/Atom feed** you point it
  at — scores them against your saved criteria, drops anything from a blacklisted
  company, dedupes against what you've already seen, and stages the survivors as
  fresh leads.
- **It shows the right lead first.** Sort the list by fit score, date posted, or
  company name — on top of the default pipeline order and the lane/status filters.
- **It drafts your cover letters.** Point it at any OpenAI-compatible LLM — a local
  Ollama/vLLM model (so your résumé never leaves the box, $0 per draft) or a hosted
  provider — and generate a letter per application, tailored from your uploaded
  résumé. Set a reusable multi-line signature once in **Settings · AI**; it is
  appended exactly to every new or regenerated letter. Download the result as
  Markdown or PDF. Keys are yours, encrypted at rest.
- **It's genuinely multi-tenant.** Every row is owned by a tenant; every query in
  both runtimes unconditionally filters `WHERE tenant_id`. One deployment cleanly
  serves many users with hard data isolation.
- **It's yours to keep.** Export your whole account as one JSON snapshot and import
  it into another instance any time — applications, criteria, and blacklist travel
  together, so you're never locked in. Delete your account and every row it owns
  cascades away in a single statement.
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

<p align="center">
  <img src="docs/architecture.drawio.png" alt="OSApplyTrack architecture — the browser SPA hits the ASP.NET Core API and a cron-driven Python poller, both sharing one Postgres schema" width="820">
</p>

The decoupling is deliberate: the API can answer "Poll now" instantly by enqueuing
a request, while the poller drains that queue out of band. Neither runtime blocks
on the other; the only thing they share is the database.

## Quickstart (Docker)

```sh
cp .env.example .env        # optional: edit the Postgres credentials / API port
docker compose up --build   # brings up db + api + poller
```

Open **http://localhost:8080**.

> **Contested port?** 8080 is popular (vLLM, llama.cpp, and plenty of dev tools
> default to it). If it's already taken, Docker refuses the bind loudly
> (`address already in use`) instead of silently winning the race — relocate by
> setting `API_PORT` in `.env`. The [quadlet units](deploy/quadlet/) add an
> explicit `ExecStartPre` listener check for the same reason.

Prefer prebuilt images? Each release publishes both runtimes to the GitHub
Container Registry, so you can skip the local build:

```sh
docker pull ghcr.io/cryptojones/osapplytrack-api:latest
docker pull ghcr.io/cryptojones/osapplytrack-poller:latest
```

To sign in, enter your email. In the default configuration the magic link is
**printed to the API logs** instead of being mailed (zero email setup needed):

```sh
docker compose logs api | grep magic-link
```

Open that link and you're in. The first account created is tenant `1`. To mail
the link instead, set the `Email__*` variables (see [Configuration](#configuration)).

> **Tip:** the poller is the third service (`poller`). `docker compose up` starts
> all three; if you only bring up `db` + `api`, no leads will ever be discovered
> because nothing drains the queue or runs the scheduled poll.

### Production containers

Use the separate hardened stack for self-hosting. It consumes versioned release
images, keeps Postgres on an internal Docker network with no host port, and binds
Kestrel to host loopback for a same-host TLS reverse proxy:

```sh
cp .env.production.example .env.production
# Replace every CHANGE-ME value and set your real HTTPS origin/hostname.
docker compose --env-file .env.production \
  -f docker-compose.production.yml up -d
```

Production startup deliberately fails if `OSAPPLYTRACK_VERSION`,
`POSTGRES_PASSWORD`, `APP_PUBLIC_BASE_URL`, or `ALLOWED_HOSTS` is missing. Pin
`OSAPPLYTRACK_VERSION` to a released version rather than `latest`; generate a
unique database password, and generate an independent `APPLYTRACK_SECRETS_KEY`
if tenants may store LLM API keys. Keep `.env.production` out of source control.
`openssl rand -hex 32` produces a connection-string-safe value for either secret.

The API and poller images run as unprivileged users. In the production stack they
also have read-only root filesystems, all Linux capabilities dropped,
`no-new-privileges`, and only a bounded in-memory `/tmp`; neither runtime receives
a host or named writable volume. Postgres alone owns the persistent `pgdata`
volume. Front `127.0.0.1:${API_PORT:-8080}` with Caddy, nginx, or another
TLS-terminating reverse proxy—do not expose Kestrel or the database directly.

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

**Sorting.** The sidebar defaults to pipeline order (still-open roles first, passed
ones last) and can reorder by **fit score**, **date posted**, or **company name**
from the *Sort by* control. Sorting is a client-side view over the same list
payload — it composes with the search box and the lane/status filters, and your
choice is remembered per browser (never sent to the server).

**Autofill.** When entering a lead by hand, paste the posting link and hit
**⤓ Autofill**: the server fetches the page (`POST /api/scrape` — SSRF-guarded,
rate-limited) and fills the still-empty fields from the page's
schema.org `JobPosting` JSON-LD, falling back to OpenGraph/`<title>` heuristics.
Fields you already typed are never overwritten.

## Configuration

All configuration is environment variables (see [`.env.example`](./.env.example)):

| Variable | Default | Purpose |
| --- | --- | --- |
| `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` | `applytrack` / `JanewayDidNothingWrong` / `applytrack` | Postgres credentials, shared by `db`, `api`, and `poller`. Override the password for any deployment exposed beyond local development. |
| `API_PORT` | `8080` | Host port the API publishes (the container always listens on 8080). |
| `DRAIN_INTERVAL` | `60` | Seconds between drains of the on-demand poll queue (the SPA's "Poll now" button). |
| `POLL_INTERVAL` | `3600` | Seconds between full multi-tenant polls. |
| `ConnectionStrings__Postgres` | _(compose default)_ | Override to point the API at an external Postgres. |
| `DATABASE_URL` | _(compose default)_ | Override to point the poller at an external Postgres (libpq URL). |
| `FORWARDED_HEADERS_KNOWN_PROXY` / `FORWARDED_HEADERS_KNOWN_NETWORK` | _(empty)_ | Source IP or CIDR of a trusted reverse proxy when it is not on loopback. |
| `APPLYTRACK_DIR` | `./applications` | Default folder the `import-md` command reads when `--dir` is omitted. |
| `Llm__BaseUrl` / `Llm__Model` / `Llm__ApiKey` | _(empty)_ | Instance-default cover-letter LLM — any OpenAI-compatible endpoint (a local Ollama/vLLM/LM Studio model or a hosted provider). `ApiKey` is blank for a keyless local model. Each tenant can override these in **Settings · AI**, including a reusable multi-line signature. See [Cover letters](#cover-letters). |
| `APPLYTRACK_SECRETS_KEY` | _(empty)_ | Master key (AES-256-GCM) that encrypts each tenant's **own** stored LLM API key at rest. Leave unset to disable per-tenant keys — the instance default above is still used. |
| `Email__Host` / `Email__Port` / `Email__Username` / `Email__Password` / `Email__From` / `Email__FromName` | `Host` empty, `Port` `587`, `FromName` `OSApplyTrack` | SMTP relay for magic-link login emails. Leave `Email__Host` unset to log links to the console instead of sending (zero email config). Set it to relay through any SMTP provider — a local relay, your mail provider, or a transactional service (Resend/SendGrid/Mailgun/SES). Port 465 = implicit TLS, else STARTTLS; blank username = unauthenticated. Deliverability to Gmail/Outlook needs a relay whose IP has PTR + SPF/DKIM/DMARC. |

## API reference

All `/api/*` routes except the auth handshake require a valid session cookie;
unauthenticated calls get **401** with a `{"detail": "..."}` body. The `/health`
and `/health/ready` probes are open. Error bodies are uniform `{"detail": "..."}`
across 400/404/409/500.

### Health & probes

Two unauthenticated endpoints, split so a database hiccup drains traffic without
killing the process:

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/health` | **Liveness.** Cheap and static — `200 {"status":"ok"}`. Never touches the database, so a transient DB blip can't trip it into a restart loop. Point your orchestrator's liveness probe here. |
| `GET` | `/health/ready` | **Readiness.** Opens Postgres and runs `SELECT 1`. `200 {"status":"ready","database":"connected"}` when the DB is reachable (and startup migrations have run); `503 {"status":"unavailable","database":"disconnected"}` otherwise. Point load balancers / deploy gates here so traffic only arrives once the DB is up. |

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
| `GET`    | `/api/apps` | List the tenant's applications. Returns a tenant-scoped `ETag`; send it as `If-None-Match` for a cheap `304` when unchanged. Clients without validators still receive the original bare JSON array. |
| `GET`    | `/api/stats` | Counts by `{status, lane}`. |
| `GET`    | `/api/apps/{name}` | One application: `{filename, raw, fields, version, material}`. |
| `POST`   | `/api/apps` | Create from structured fields → `201 {filename}`. |
| `PUT`    | `/api/apps/{name}?expected_version=…` | Update structured fields (409 on version mismatch). |
| `PUT`    | `/api/apps/{name}/raw?expected_version=…` | Replace the full Markdown document. |
| `DELETE` | `/api/apps/{name}` | Delete → `204`. |
| `POST`   | `/api/apps/{name}/draft` | Draft a tailored cover letter via the configured LLM; saves it and returns `{ok, material}`. Rate-limited. |
| `GET`    | `/api/apps/{name}/cover-letter.pdf` | Download the saved cover letter as a PDF. |
| `POST`   | `/api/poll` | Enqueue an on-demand poll → `{count:0}`. Rate-limited; the worker drains it. |
| `POST`   | `/api/scrape` | Body `{url}`. Fetch a posting page server-side (SSRF-guarded) and extract `{company, role, location, salary, source, description}` for the editor's Autofill. Rate-limited; 502 when the page can't be read. |

### Criteria & blacklist

| Method | Path | Notes |
| --- | --- | --- |
| `GET`    | `/api/criteria` | The tenant's discovery criteria (defaults when unset), including `ats_boards` and `rss_feeds`. |
| `PUT`    | `/api/criteria` | Normalize + store posted criteria (junk dropped, score clamped, non-http(s) feed URLs discarded). |
| `GET`    | `/api/blacklist` | List blacklisted companies. |
| `POST`   | `/api/blacklist` | Add a company; flips its open leads to `passed`. |
| `POST`   | `/api/apps/{name}/blacklist` | Blacklist the company on a given application. |
| `DELETE` | `/api/blacklist/{company}` | Remove a company. |

### Account

| Method | Path | Notes |
| --- | --- | --- |
| `GET`    | `/api/account/export` | One JSON snapshot: every application + criteria + blacklist. |
| `GET`    | `/api/account/export/shared` | Anonymized opportunity list for a peer (`format: applytrack-shared`): slug, company, role, link, location, source — **no personal state**. |
| `POST`   | `/api/account/import` | Load a snapshot (upsert by slug, one transaction) — or a shared list: every entry lands as a fresh `lead`, slugs you already track are skipped. |
| `DELETE` | `/api/account` | Delete the account; every owned row cascades away. |

### Materials (cover letters)

| Method | Path | Notes |
| --- | --- | --- |
| `GET`    | `/api/resume` | The tenant's résumé brief — the only facts the drafter may assert. |
| `PUT`    | `/api/resume` | Compatibility endpoint for structured résumé JSON. |
| `POST`   | `/api/resume/upload` | Upload a text-based PDF résumé (`multipart/form-data`, `resume` file, max 5 MB) and store the extracted text as the model brief. |
| `GET`    | `/api/llm-settings` | The tenant's endpoint override, model, reusable `cover_letter_signature`, and the instance default. The API key is **write-only** — never returned, only a `has_api_key` flag. |
| `PUT`    | `/api/llm-settings` | Set `base_url` / `model` / `api_key` / `cover_letter_signature` (omit `api_key` or the signature to leave it untouched; blank clears either) and `cover_letters_enabled` (omit to keep; `false` disables all drafting for the tenant). |
| `DELETE` | `/api/apps/{name}/cover-letter` | Discard a generated letter → `204`. |

### Not in v1

`GET /api/apps/{name}/check-link` answers **501** with a `{detail}` body the SPA
surfaces as a clean toast (see [Roadmap](#roadmap)). Cover-letter drafting
(`POST /api/apps/{name}/draft`) is implemented — see [Cover letters](#cover-letters).

## Data model

The schema is migrated by **DbUp** from idempotent `.sql` scripts under
`api/ApplyTrack.Api/Migrations/`, run automatically on API startup:

| Table | Holds |
| --- | --- |
| `users` | Accounts. A user's `id` **is** its `tenant_id` (tenants are users). |
| `applications` | The tracked applications. `UNIQUE (tenant_id, name)`; `version` for optimistic locking. |
| `search_profiles` | Per-tenant discovery criteria the poller reads — keywords, filters, enabled sources, ATS boards, and custom RSS feeds. |
| `blacklist` | Per-tenant blocked companies. |
| `magic_tokens` | SHA-256 of issued login tokens, with expiry. Single-use. |
| `sessions` | Opaque server-side sessions (instant revocation on logout). |
| `seen` | The dedupe ledger — listings already surfaced, so leads don't repeat. |
| `poll_requests` | The on-demand "Poll now" queue the worker drains. |
| `resume_profiles` | Per-tenant résumé brief — the facts the cover-letter drafter feeds the LLM. |
| `llm_settings` | Per-tenant LLM endpoint, model, cover-letter toggle, and multi-line signature; a tenant's own API key is stored **AES-256-GCM-encrypted** at rest. |
| `cover_letters` | Generated cover letters, one per application (`FK → applications ON DELETE CASCADE`). |

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

### Custom RSS feeds

Beyond the built-in sources and the Greenhouse/Lever board followers, **Settings ·
Criteria** takes any RSS 2.0 or Atom feed URL — a company's careers feed, a niche
board, a saved search that publishes one. Up to 25 per account. Items are scored
against the same keywords and minimum fit as every other source, and land with
`source: auto:rss:<host>`.

The company name is read from the item title where the feed provides one
(`Company: Role` or `Role at Company`); otherwise it falls back to the feed's own
title, so a single-company careers feed works without any per-feed configuration.

A feed URL is user-supplied and fetched server-side, so it goes through the same
SSRF guard as the link prober: http(s) on a default port only, every redirect hop
re-validated, connections pinned to public addresses, a 2 MB response cap, and
XXE/entity-expansion-safe parsing (`defusedxml`). A feed that fails is logged and
skipped — it never aborts the poll. When several tenants follow the same feed, the
multi-tenant pass fetches it once.

## Cover letters

OSApplyTrack drafts a tailored cover letter per application from an uploaded
résumé you control — provider-agnostic, and built so your data can stay on-prem.

> **⚠ Any LLM — or none at all.** The backend is hard-required to work with **any
> OpenAI-compatible endpoint**; no vendor is baked in. And the whole engine is
> **optional**: untick *Enable cover-letter drafting* in **Settings · AI** and the
> app hides every drafting affordance and never calls a model for your account.

- **Bring your own model.** The drafter calls an OpenAI-compatible
  `POST {base_url}/chat/completions`, so the same code points at a free local model
  (Ollama, vLLM, LM Studio) or any hosted provider (OpenAI, OpenRouter, Together,
  Groq, …). A local model means **$0 per draft** and the résumé never leaves the box.
- **Operator default + per-tenant override.** The instance sets a default endpoint
  via `Llm__BaseUrl` / `Llm__Model` / `Llm__ApiKey`; each tenant can override any
  field in the **Settings · AI** tab (override just the model, keep the URL, set
  the cover-letter toggle, or save a multi-line signature).
- **Your résumé is the only source of truth.** The **Settings · Résumé** tab uploads
  a text-based PDF and stores the extracted résumé text as the model brief. The LLM
  is told these are the *only* facts it may assert, so it can't invent employers or
  metrics.
- **Your signature is deterministic.** The model is instructed to stop after the
  body paragraphs. The saved signature is appended server-side exactly as entered,
  so drafts do not fall back to placeholders such as “The Candidate.”
- **Keys encrypted at rest.** A tenant's own API key is write-only: sealed with
  AES-256-GCM under `APPLYTRACK_SECRETS_KEY` and never echoed back. Without that
  master key the per-tenant-key path is disabled (the instance default still works).
- **Generate from the application sheet.** Each app gets a **Generate cover letter**
  action; the result renders inline with copy / download `.md` or PDF / regenerate /
  discard. PDF output is generated server-side from the saved letter. Letters are
  stored per application and are excluded from the export snapshot by design.

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
- **Encrypted secrets at rest.** A tenant's own LLM API key is sealed with
  **AES-256-GCM** under an operator master key (`APPLYTRACK_SECRETS_KEY`) before it
  reaches the database, and is never returned by the API — only a `has_api_key`
  flag. With no master key configured, the per-tenant-key path is disabled rather
  than storing anything in the clear.
- **Rate limiting.** The magic-link and poll endpoints are per-IP fixed-window
  rate-limited so the always-200 auth surface can't be abused for spam or probing.
- **Bounded API input.** Ordinary JSON mutations are capped at 1 MiB, application
  strings/notes have explicit length ceilings, and criteria/résumé collections
  reject excessive cardinality with a clear `400` instead of growing rows or LLM
  prompts without limit.
- **SSRF-hardened outbound fetches.** Every server-side fetch of a URL a user
  supplied — the link prober, the Autofill scraper, and custom RSS feeds — refuses
  to connect to private/loopback/link-local/reserved addresses, re-checks every
  redirect hop, and caps the response size, so a hostile listing or feed URL can't
  pivot into your network.
- **Behind HTTPS.** Front the API with a TLS-terminating reverse proxy (Caddy,
  nginx, or `tailscale serve`). Same-host loopback proxies are trusted by default.
  For a proxy container or remote load balancer, set
  `FORWARDED_HEADERS_KNOWN_PROXY` to its source IP or
  `FORWARDED_HEADERS_KNOWN_NETWORK` to its CIDR. Forwarded headers from every other
  address are ignored, so a direct caller cannot forge its rate-limit IP or HTTPS
  state. Don't expose Kestrel directly.
- **Change the default password.** For any deployment reachable beyond `localhost`,
  change `POSTGRES_PASSWORD` (and the matching connection string) from the
  bundled development default before first boot — the documented value is not a
  production secret. The hardened `docker-compose.production.yml` has no password
  default and refuses to start until one is supplied.
- **Dependency CVE watch.** [`.forgejo/workflows/audit.yml`](./.forgejo/workflows/audit.yml)
  runs `dotnet list package --vulnerable --include-transitive` and `pip-audit` on
  every push/PR and weekly, failing the build on a known-vulnerable dependency. Run
  the same two commands locally any time.

## Your data

- **Export** — `GET /api/account/export` returns a single JSON snapshot of your
  whole account: every application (all fields + its slug, so apply links survive a
  move), your search criteria, and your company blacklist. A real backup, and the
  door's never locked.
- **Import** — `POST /api/account/import` loads a snapshot back. Applications
  upsert by slug (an incoming app overwrites a matching local one, new slugs are
  added, untouched apps stay), so re-importing is idempotent. The whole load runs in
  one transaction — a mid-import failure leaves your account untouched. Use it to
  migrate from one instance to another: export here, import there.
- **Share** — `GET /api/account/export/shared` exports a peer-shareable
  *opportunity list*: only the facts of each posting (company, role, link,
  location, source, plus the slug for de-dup). Status, notes, contacts, dates,
  score, and salary are stripped at the source. A peer imports the file and every
  entry lands as a fresh `lead`; anything they already track is skipped, never
  overwritten. All three live in **Settings · Account**.
- **Delete** — `DELETE /api/account` removes your account and, via
  `ON DELETE CASCADE`, every row that belongs to it (applications, search profile,
  blacklist, seen ledger, queued polls, sessions, tokens) in one statement.

## Accessibility

The web interface targets **WCAG 2.2 Level AA**. It provides semantic screen-reader
navigation, complete keyboard operation, visible focus, labeled controls, live status
announcements, reduced-motion and high-contrast modes, light/dark/system colors,
adjustable 100%/125%/150% text size, comfortable or compact spacing, and responsive
reflow through 400% browser zoom. The **Settings · Accessibility** panel detects system
preferences before sign-in and lets each browser override color, contrast, motion,
text size, and density. Preferences are stored only in the current browser.

Pull requests run Playwright and axe-core checks against login, application, editor,
settings, validation, and responsive workflows. See the
[accessibility statement and manual test matrix](docs/accessibility.md), or use the
Accessibility problem issue template to report a barrier without sharing private data.

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

# API (reads appsettings.json → localhost Postgres) — serves the whole app on
# http://localhost:5049 (per launchSettings.json; the Docker setup uses 8080).
# In the default configuration the magic-link login URL is printed to this
# console; click it to sign in.
cd api && dotnet run --project ApplyTrack.Api

# Poller (one-shot poll; needs DATABASE_URL or the POSTGRES_* / PG* env vars)
pip install -e '.[dev]'
DATABASE_URL=postgresql://applytrack:JanewayDidNothingWrong@localhost:5432/applytrack applytrack poll
```

**Enable cover-letter drafting (optional).** Drafting stays off until an
OpenAI-compatible endpoint **and model** are set. For local testing, point the API
at [Ollama](https://ollama.com) (`ollama serve`, then `ollama pull llama3.1:8b`):

```sh
cd api
Llm__BaseUrl=http://localhost:11434/v1 Llm__Model=llama3.1:8b \
  dotnet run --project ApplyTrack.Api
```

See [Cover letters](#cover-letters) for hosted providers, per-tenant keys, and the
**Settings · AI** tab — and `.env.example` for the same settings, annotated.

## Tests

```sh
# .NET — xUnit + Testcontainers (needs a running Docker daemon)
cd api && dotnet test

# Python — pytest (offline; no DB/network), plus lint + types
pytest
ruff check .
mypy src

# Web — Playwright keyboard/responsive tests + axe-core WCAG checks
npm ci
npx playwright install chromium
npm run test:web
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
docker-compose.production.yml  hardened self-hosting stack
Dockerfile.poller         the poller image
```

## Roadmap

v1 is intentionally focused. Deferred, with clean seams already in place:

- **Link checking.** `/api/apps/{name}/check-link` returns 501 today; the
  SSRF-hardened prober already exists in the poller for when it's enabled.
- **Richer cover-letter output.** The materials engine already ships plain-text/
  Markdown letters ([Cover letters](#cover-letters)) with direct Markdown and PDF
  downloads; future work can add richer templates and provider-specific formats.

## Contributing

Issues and PRs welcome. Please keep the cross-runtime contract intact (every query
filters `tenant_id`), add tests for new behavior, and keep the SPA dependency-free.
Both test suites and the dependency audit run in CI.

## License

[Apache-2.0](./LICENSE). Copyright 2026 Aaron K. Clark.

Proudly Made in Nebraska. Go Big Red! 🌽 https://xkcd.com/2347/
