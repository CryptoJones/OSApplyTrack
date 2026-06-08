# applytrack → open-source, multi-tenant, self-hostable (.NET 10 API + Python poller)

## Status / what changed (revised 2026-06-07)

This plan was originally a **commercial** multi-tenant SaaS, all-Python (FastAPI +
SQLAlchemy). Revised per CryptoJones into an **open-source, self-hostable** app
with a **polyglot** backend:

- **Open-source, not commercial.** License → **Apache-2.0** (same as the original
  applytrack — no relicense). **No Stripe / billing.**
- **Core API → .NET 10** — ASP.NET Core Minimal APIs on Kestrel. (".NET Core" is
  just **.NET** since v5; latest LTS is .NET 10, Nov 2025.)
- **Discovery poller stays Python** (polyglot). The existing `poll.py` — 8 source
  fetchers + HTML scraping + `classify()` scoring/dedup — runs as a cron worker
  against the **same Postgres**. We do **not** rewrite the fetchers in C#.
- **Data layer → Dapper + Npgsql** in .NET: hand-written SQL mapped to records,
  migrations via **DbUp** (embedded, idempotent `.sql` scripts), manual
  `version`-column optimistic locking. (No EF Core.)
- **Keep the existing UI verbatim.** `web/static/{index.html,app.js,app.css}` —
  the vanilla-JS SPA, including the Criteria/blacklist panels — is carried over
  untouched and served from the .NET app's `wwwroot/`. **Hard design pin:** the
  .NET API must preserve the **exact endpoint URLs + JSON request/response shapes**
  the SPA already calls (the 8 data endpoints, `?expected_version=`, the 409
  conflict flow). The SPA is the contract the .NET API is written *against*.
- **Multi-tenant stays** (self-hostable, multi-user). Only monetization is dropped.
- AI cover-letter/materials engine (`materials.py` + LaTeX résumé) stays **out of
  v1** — heaviest cost, scariest data; later optional module.

## Architecture

```
            ┌─────────────────────────────────┐
Browser ──► │ ASP.NET Core (.NET 10, Kestrel)  │
 (SPA,      │  • serves wwwroot/ = the SPA     │
  UNCHANGED)│    verbatim                      │
            │  • JSON API (Dapper + Npgsql)    │──┐
            │  • magic-link auth, server-side  │  │
            │    sessions, tenancy choke-point │  ├──► Postgres
            └─────────────────────────────────┘  │   (shared schema =
            ┌─────────────────────────────────┐  │    the cross-runtime
 Cron  ───► │ Python poller (worker.py)        │──┘    contract)
            │  • run_poll() per tenant         │
            │  • psycopg3 direct to DB         │
            │  • reuses all 8 fetchers as-is   │
            └─────────────────────────────────┘
```

**Two runtimes, one database.** The Postgres **schema is the contract** between
the .NET API and the Python poller — neither process calls the other. The .NET
side owns auth/sessions and CRUD; the Python side reads profiles/seen/users and
writes new leads. Both **unconditionally filter `WHERE tenant_id`**.

Ownership split:
- **.NET writes/owns:** `users`, `magic_tokens`, `sessions`; CRUD on
  `applications`; edits to `search_profiles` and `blacklist`.
- **Python writes:** new rows in `applications` (leads), `seen` entries.
- **Python reads:** `users` (active list), `search_profiles`, `seen`, `blacklist`.
- **Shared tables:** `applications`, `search_profiles`, `seen`, `blacklist`.

## Repo layout after the split

```
api/            # new .NET solution (ApplyTrack.Api)
  ApplyTrack.Api.csproj
  Program.cs            # Minimal API wireup, DI, static files
  Endpoints/*.cs        # apps, criteria, blacklist, auth, account
  Data/Repo.cs          # ApplicationRepo (Dapper), tenant-scoped
  Data/Slug.cs          # safe_name → slug validation choke-point
  Auth/*.cs             # magic-link, sessions, tenant provider
  Migrations/*.sql      # DbUp scripts 0001_…, 0002_…
  wwwroot/              # the existing SPA, moved verbatim
  ApplyTrack.Api.Tests/ # xUnit + Testcontainers-Postgres
src/applytrack/ # Python, trimmed to the poller + CLI
  poll.py worker.py db.py criteria.py linkcheck.py cli.py
  (store.py/web/ removed once .NET owns the API)
```

## Cross-cutting: license = Apache-2.0 (set up alongside Step 0)
- The project **stays Apache-2.0** (same as the original applytrack) — no
  relicense. Ship a full `LICENSE` (Apache-2.0 text) at the repo root; set
  `[project] license = {text="Apache-2.0"}` in `pyproject.toml`;
  `<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>` in the `.csproj`.
- Every source file (Python + the new C#) carries
  `SPDX-License-Identifier: Apache-2.0` and `Copyright 2026 Aaron K. Clark`.

## Build order (each step independently shippable)

### Step 0 — Test scaffolding + repo split + license setup (do first)
- **Python side already has 27 tests** (criteria/poll/fetchers) — keep them; they
  guard the poller through the split. Preserve the `run_poll(listings=)` offline
  seam.
- **New .NET test project:** xUnit + **Testcontainers-Postgres** (disposable PG
  per test run), DbUp `upgrade` in a fixture, truncate between tests.
- Stand up the empty .NET solution + DbUp migration runner so Step 1 has a home.
- Add the Apache-2.0 `LICENSE` + license metadata here while the file set is small.

### Step 1 — .NET API skeleton + Postgres storage ⭐ (start here; single-user, no auth yet)
Goal: the existing SPA loads and works **identically**, now served by .NET off
Postgres, with a hardcoded bootstrap `tenant_id = 1` (auth lands in Step 2).

- **`applications` table** (DbUp `0001_applications.sql`):
  ```
  id bigserial PK, tenant_id bigint NOT NULL,
  name text NOT NULL,                       -- the slug, public key within tenant
  company/role/lane/status/link/location/salary/source/contact/contact_email text,
  applied/followup/created/score text,      -- keep the SPA's free-text shape
  notes text, version bigint NOT NULL DEFAULT 1,
  created_at/updated_at timestamptz DEFAULT now(),
  UNIQUE (tenant_id, name), INDEX (tenant_id, status)
  ```
  `applied/followup/created/score` stay **text** at the column edge to match the
  SPA's free-text fields — real `date`/`int` is cleaner but ripples into the SPA;
  defer.
- **`ApplicationRepo(IDbConnection conn, long tenantId)`** (Dapper) replaces the
  Python `AppStore`. Same logical methods: `Create/Update/Delete/Read/ReadFields/
  List/Stats/Version`. Every query **unconditionally** includes
  `WHERE tenant_id = @tenantId`.
  - **Slug choke-point** (`Data/Slug.cs`): the C# heir to `safe_name` — validates/
    normalizes the slug (reject empty/`.`/`..`/separators). Single naming control.
  - **Optimistic lock — same contract, new mechanism.** `Update(..., expectedVersion)`
    → `UPDATE applications SET …, version = version + 1 WHERE id = @id AND
    tenant_id = @t AND version = @expected`; **0 rows affected → 409 Conflict**.
    The SPA's `?expected_version=` param + `saveWithConflict` 409-confirm flow keep
    working unchanged (version stays opaque to the SPA).
  - `List`/`Stats` are plain SQL (`GROUP BY status` for stats).
- **Minimal API endpoints** (`Endpoints/Apps.cs`) — **byte-for-byte the URLs/shapes
  the SPA calls:** `GET /api/apps`, `GET/PUT /api/apps/{name}`,
  `GET/PUT /api/apps/{name}/raw`, `GET /api/stats`, plus `GET/PUT /api/criteria`
  and `GET/POST/DELETE /api/blacklist`. Verify each against `app.js` before moving
  on.
  - `.../raw` round-trips markdown: render a row → the same frontmatter+body text
    the SPA edits. (Port the tiny render/parse codec to C#, or keep markdown
    round-trip Python-side via the import/export CLI and have `.raw` compose from
    columns — choose the C# port; it's ~40 lines and keeps the API self-contained.)
- **Serve the SPA:** drop `web/static/*` into `wwwroot/`; `app.UseDefaultFiles()` +
  `app.UseStaticFiles()`. No SPA edits.
- **`criteria`/`blacklist` tables** now (read by Python in Steps 3–4):
  `search_profiles(tenant_id, keywords jsonb, sources jsonb, min_score int,
  remote_only bool, exclude_locations jsonb, default_lane text, active bool)` and
  `blacklist(tenant_id, company text, PK(tenant_id, company))`. `/api/criteria`
  edits the one profile row; `/api/blacklist` edits the table. These replace
  `.criteria.json` / `.blacklist.json`.
- **Migration of existing data — stays Python.** Keep an `applytrack import-md
  --dir applications --tenant 1` CLI that reuses the existing `parse_app` to upsert
  the live Markdown into Postgres (psycopg3). The Markdown codec already exists in
  Python; no reason to reimplement it in C# for a one-shot.
- **Out of v1 here:** `/api/apps/{name}/draft` (materials/LLM) returns `501`; the
  SPA's draft button is hidden/disabled. On-demand `/api/poll` — see Step 4.
- **Tests (.NET):** repo CRUD, optimistic-lock 409, list/stats, endpoint-shape
  contract tests; **(Python):** existing 27 stay green.
- **Milestone:** identical app, on Postgres, served by .NET. Ship this first.

### Step 2 — Auth + tenancy spine (.NET)
- Migrations: `users(id, email citext UNIQUE, status, created_at)`,
  `magic_tokens(user_id, token_sha256, expires_at, used_at)`,
  `sessions(id opaque, user_id, expires_at)`. `tenant_id == user.id` for v1 (the
  name future-proofs orgs).
- **Single tenancy choke-point** (`Auth/TenantProvider.cs`): resolves the current
  tenant from the session cookie — the *only* place a `tenant_id` enters the
  system. DI hands endpoints a **pre-scoped `ApplicationRepo`**; endpoints never
  see `tenant_id`, never hold a raw connection. (Optional belt-and-suspenders:
  Postgres RLS via `SET LOCAL app.tenant_id`; repo discipline is the primary
  control and ships on its own.)
- **Magic-link** (`Endpoints/Auth.cs` + an `IEmailSender`):
  `POST /api/auth/request {email}` (upsert user, store **sha256** of token,
  email link, **always 200** to prevent enumeration, rate-limit),
  `GET /api/auth/verify?token=` (validate unexpired+unused, mint **opaque
  server-side session**, set cookie, redirect to `/` stripping the token),
  `POST /api/auth/logout`, `GET /api/auth/me`. Server-side sessions (not JWT) →
  instant revocation.
- **Cookie:** `HttpOnly; Secure; SameSite=Lax; Path=/`. CSRF defense =
  `SameSite=Lax` + JSON-only `fetch` mutations; no separate CSRF token in v1
  (document it).
- **Email:** lean — SMTP via `MailKit` or a transactional HTTP provider; in dev,
  print the link to stderr/console.
- **SPA:** the *only* additive change to the kept UI — `api()` handles `401` →
  small login view (~40 LOC, no framework); `boot()` calls `/api/auth/me`; the 5s
  poll loop bounces to login on 401. The 8 data endpoints keep their shapes, so
  this is genuinely the only SPA delta in the whole plan.
- **Tests:** request→verify→session happy path; expired/used token rejection;
  **mandatory cross-tenant isolation test** (user A cannot touch user B's `name`).

### Step 3 — Per-user search profiles (Python poller reads them)
- `search_profiles` already exists (Step 1). Now the **Python poller consumes it**
  instead of hardcoded constants.
- **`poll.py`:** `DEFAULT_KEYWORDS`/`min_fit_score` become **defaults seeded on
  signup** (a row written by .NET at user creation), not the live source.
  `classify(title, desc, keywords)` already takes keywords ✓. `run_poll(...)` →
  `run_poll(repo, profile, *, fetchers=None, listings=None, verify_links=None)`.
  **`listings=`/`fetchers=` seams preserved verbatim** so the 27 offline tests pass
  a constructed `profile` + fixed `listings`, no network/DB.
- **`db.py` (Python, psycopg3):** thin tenant-scoped reader/writer mirroring the
  .NET schema — load a profile, insert leads, all `WHERE tenant_id`. This is the
  Python half of the shared-schema contract.
- Materials wiring stays out of `run_poll` (new leads → `status="lead"`).
- Profile editing is the existing `/api/criteria` panel (no new UI).
- **Tests:** `classify` with custom keywords (have it); `run_poll` honoring
  `min_score`/`sources` offline (have it) — re-point fixtures at a `profile`.

### Step 4 — Per-tenant cron + `seen` table (Python)
- Migration `seen(tenant_id, kind ('url'|'slug'), key, PK(tenant_id,kind,key))`
  replacing `.seen.json`. The `Seen` adapter keeps the **same `has`/`add`
  interface** so `run_poll`'s loop body is untouched; `_norm_url`/`_norm_slug`
  unchanged. `_seed_from_existing(repo, seen)` via
  `SELECT link, company, role WHERE tenant_id`.
- **`worker.py`:** `run_all_tenants()` selects active users, builds a per-tenant
  repo+profile **inside** the loop (preserving `WHERE tenant_id` — cron does **not**
  bypass the choke-point), runs `run_poll`, commits, isolates per-tenant failures.
  The one privileged query (`SELECT id FROM users WHERE status='active'`) lives
  here.
- **Fetch-once optimization (key risk):** public boards (esp. RemoteOK) rate-limit
  a single IP. Gather each source **once per run**, then run classify/dedup/insert
  **per tenant** over the shared listing set. Split `run_poll` into `_gather` +
  `score_and_stage(repo, profile, listings)` — the `listings=` seam makes this
  trivial.
- **`cli.py`:** `poll` drops `--dir`, gains `--tenant <id>` or default (all
  active); run hourly by **system cron / systemd timer** (replaces the user-level
  units).
- **On-demand poll button:** the SPA's poll button posts `/api/poll`; in the split,
  .NET can't run Python inline. v1 options, pick one: (a) **enqueue** a
  `poll_requests(tenant_id, requested_at)` row the worker drains each minute
  [recommended — keeps runtimes decoupled]; (b) .NET shells out to the worker; or
  (c) hide the button and rely on cron. Default to (a).
- **Tests:** dedup across runs (offline), seed-from-existing skip, multi-tenant
  isolation.

### Step 5 — Self-host packaging + data export/delete (replaces billing)
- **No Stripe.** Instead, make it *easy to self-host and trust*:
  - **`docker-compose.yml`:** `api` (.NET), `poller` (Python cron), `db`
    (Postgres) — one `docker compose up`. `.env.example` for connection string,
    email creds, base URL.
  - **README rewrite:** self-host quickstart, the two-runtime model, env vars,
    `import-md` first-run migration, Apache-2.0 badge.
- **Data export + import** (landed) `GET /api/account/export` → one JSON snapshot
  (every application + criteria + blacklist); `POST /api/account/import` loads it
  back, upserting applications by slug in a single transaction. The round-trip lets
  a user migrate between instances, not just back up — no lock-in. SPA gets Export
  /Import buttons in the masthead.
- **Data delete** `DELETE /api/account` → FK `ON DELETE CASCADE` from `users`
  drops applications/seen/profiles/sessions/tokens/blacklist in one statement; SPA
  confirms.

## Critical files
- **New .NET:** `api/Program.cs`, `Endpoints/{Apps,Auth,Account}.cs`,
  `Data/{Repo,Slug}.cs`, `Auth/*`, `Migrations/*.sql`, `wwwroot/` (the SPA),
  `ApplyTrack.Api.Tests/`.
- **Python, trimmed:** `poll.py` (`run_poll(repo, profile)`, split `_gather` +
  `score_and_stage`), `worker.py` (new, per-tenant), `db.py` (new, psycopg3),
  `criteria.py` (defaults seed), `cli.py` (`import-md`, per-tenant `poll`),
  `linkcheck.py` (unchanged).
- **Removed from Python once .NET owns the API:** `store.py`, `web/` (the SPA
  files move to `wwwroot/`, the FastAPI app + Basic-Auth middleware retire).
- **License (Apache-2.0):** every source header + `LICENSE` + `pyproject.toml` + `.csproj`.

## Main risks
- **Two runtimes sharing one schema.** Mitigation: the schema *is* the contract;
  changes go through DbUp; the Python `db.py` and .NET `Repo.cs` are the only
  writers and both filter `tenant_id`. A schema-shape test on each side guards
  drift.
- **`version` semantics swap** (mtime-size → int): trivial on a fresh DB; the real
  risk is omitting `tenant_id` from the optimistic `UPDATE` — the repo choke-point
  guards isolation *and* the lock.
- **Auth before storage is solid** entangles two hard changes — Step 1 as a
  shippable single-user-on-PG milestone keeps the rollout safe.
- **Cron HTTP volume / board rate-limits** — mitigated by fetch-once-per-run.
- **SPA contract drift** — the kept UI is unforgiving about endpoint shapes; Step 1
  contract tests against `app.js`'s actual calls are mandatory.

## Verification
- **Step 0/1:** .NET + Python tests green; `applytrack import-md --dir applications
  --tenant 1` then `dotnet run` → the **existing SPA** loads all apps;
  create/edit/delete and the 409-conflict flow work identically on Postgres.
- **Step 2:** request a magic link (dev: link to console), verify → session cookie
  set, app loads; logout → 401 → login view. Cross-tenant isolation test green.
- **Step 3:** edit a profile's keywords via the Criteria panel; Python
  `run_poll(repo, profile, listings=[...])` offline yields leads filtered by
  `min_score`/`sources`.
- **Step 4:** `applytrack poll` (all tenants) twice offline → second run adds zero
  (dedup + seed-from-existing). Per-tenant failure doesn't abort others. Poll
  button enqueues; worker drains it.
- **Step 5:** `docker compose up` brings up all three; `GET /api/account/export`
  returns a JSON snapshot that `POST /api/account/import` round-trips on another
  instance; `DELETE /api/account` cascades to zero rows.

---

# Appendix — Secure remote access (already shipped on the Python app)

> The self-signed-TLS + HTTP-Basic-Auth LAN access task is **already implemented
> and committed** on the current Python/FastAPI app (cert gen, `0.0.0.0` guard,
> Basic-Auth middleware, systemd `EnvironmentFile`). In the .NET rewrite it is
> **subsumed**, not re-ported:
> - **TLS:** Kestrel terminates HTTPS natively (`UseHttps`, dev certs via
>   `dotnet dev-certs`); no `openssl` shell-out.
> - **Auth:** the **Step 2 magic-link + sessions** replace Basic Auth entirely —
>   there's no longer an "exposed without auth" state to guard against, because
>   the app requires login.
> - **Gold-standard alternative still stands:** front it with `tailscale serve`
>   for a real trusted cert + identity-based access, app bound to the tailnet, no
>   `0.0.0.0`. (See the `tailscale-serve-todo` memory.)
