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
| **Step 6** | Encryption at rest, operator allowlist & captcha code fallback (v1.26–v1.27) | ✅ done |
| **Step 7** | Answer bank, two-way Telegram codes & worker heartbeats (v1.28–v1.29) | ✅ done |
| **Step 8** | Ready lane in bulk, queued prepare & salary unit conversions (v1.30) | ✅ done |
| **Step 9** | Mobile list scroll, cookie consent, editable name fields & upload verifications (v1.31) | ✅ done |
| **Step 10** | Pipeline view & "Apply later" trap prevention (v1.32) | ✅ done |

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

## Agentic auto-apply

- ✅ **Step 1 — the draft reads the posting** (#91): `POST /api/apps/{name}/draft`
  fetches the job description through the SSRF-hardened fetcher and puts it in the
  prompt; best-effort, honest "(not available)" when it can't.
- ✅ **Step 2 — the agent's own fit verdict** (#92): `agent_settings` (off by
  default, its own 70-point bar, per-pass/per-day caps, standing answers) and
  `agent_events` (the audit trail, keyed to the user, so a deleted application never
  erases the record). `StructuredCompleter` gets JSON out of any chat model with one
  repair prompt and a hard failure after that; `Disqualifiers` veto in code before a
  token is spent; `FitJudge` judges against the résumé brief. `AgentWorker` is the
  first `BackgroundService` — a second container from the api image with
  `Agent__Enabled=true`, its own small pool, per-tenant advisory lock under
  `applytrack:agent:`, and the migrator now serializes on an advisory lock. Settings ·
  Agent tab, a Fit-verdict block on the sheet, and `POST /api/apps/{name}/verdict`
  for judging by hand. Stages nothing.
- ✅ **Step 3 — prepared packets and the Ready-to-submit queue** (#93) **+ the moo**
  (#99): a `proceed` now becomes an `agent_packets` row — the ATS form (Greenhouse's
  real one via the public Job Board API, the standard set for everyone else), the
  drafted answers (deterministic ones from your facts, screening ones from the
  model, anything unanswerable flagged, EEO never touched), the letter — and parks
  the application in `ready`, the status that was defined but unreachable since
  the single-user app. The sheet shows the packet with every answer editable
  (`?expected_version=` 409 flow on the packet's own version), a `role="alert"`
  summary of what still needs you, and **Copy answers and open the posting** for
  every ATS. `notification_settings` + `TelegramNotifier`: one 🐮 per packet
  (`notified_at` claim), Settings · Notifications with a test button, `#app=` deep
  links. `POST /api/apps/{name}/packet/prepare` does it by hand.
- ✅ **Step 4 — human-triggered browser submission, dry-run by default** (#94): the
  only step that touches an employer. `Microsoft.Playwright` in the api, driving a
  **separate** browser container (`docker/browser`, Playwright's image running
  `run-server`) over `ws://` — the api/agent images keep their hardened profile.
  Containment: the scraper's URL/address pre-flight, an `internal: true` browser
  network whose only exit is a squid forward proxy (`docker/proxy`) that does the
  DNS and refuses private destinations, route interception, and a least-privilege
  `applytrack_agent` Postgres role (no DELETE, no sessions/magic_tokens; grants in
  the RunAlways `Migrations/Always/agent_role.sql`, `Migrations__Mode=wait` for the
  container that cannot migrate). `submit_requests` (claimed by UPDATE … SKIP
  LOCKED), `agent_evidence` (screenshot + confirmation), the résumé PDF kept and
  attached from memory. **Dry run by default:** fill, screenshot, don't click; the
  moo now says "filled in and ready for you to click Apply". `POST
  /api/apps/{name}/submit`, `GET …/evidence`, the Submit / dry-run buttons on the
  sheet. Tests drive the real submitter through a real Playwright server at a
  loopback fixture form and assert on the POST it receives.
- ✅ **Step 5 — Lever, Ashby, and the unknown long tail** (#95): `FormDiscoverer`
  visits a form read-only in the browser container and enumerates its controls by
  accessible name — Lever and Ashby (one hop past the posting: `/apply`,
  `/application`) get real questions this way; the same `PacketQuestion` shape
  Greenhouse's API gives, so the drafter and the submitter need no per-ATS code.
  The generic adapter (fill by label, refuse to click on an unmapped required
  field) is behind the per-tenant `long_tail` opt-in (0023). Workday stays
  manual, permanently: detected, packet prepared, routed to copy-and-open.
- ✅ **Step 6 — Encryption at rest, operator allowlist & captcha code fallback** (#117, #156, #165, #166; v1.26–v1.27):
  Sensitive columns sealed with AES-256-GCM at rest (résumés, cover letters, packet
  answers, evidence screenshots, tenant API keys, bot tokens). Production audit closed
  out with first confirmed end-to-end Greenhouse submission. Auto-apply gated per account
  by `agent_allowlist` table (operator opt-in only). Greenhouse 428 captcha fallback:
  browser session parks, records `awaiting_code`, moos via Telegram, and finishes when
  candidate submits code via `POST /api/apps/{name}/security-code`.
- ✅ **Step 7 — Answer bank, two-way Telegram codes & worker heartbeats** (#170, #172, #173, #174, #175, #178, #181; v1.28–v1.29):
  **Answer Bank** (`answer_bank` table, `GET/PUT/DELETE /api/answers`, Settings · Answers):
  reusable screening questions and answers across forms, editable once, reused everywhere
  without model calls. **Two-way Telegram replies**: candidate replies directly to the
  🔐 Telegram moo with the security code; worker polls Telegram updates to resume parked
  browser runs. **IMAP Mailbox code reader**: parked runs read Greenhouse security codes
  directly from an IMAP mailbox (`mailbox_settings`). **Worker heartbeat registry**:
  `agent_workers` table records worker heartbeats so API knows when browser-capable
  workers are available. Form discovery handles iframes, late renders, single Name boxes,
  and text-only labels.
- ✅ **Step 8 — Ready lane in bulk, queued prepare & salary unit conversions** (#180, #183–#192, #194; v1.30.0):
  **Bulk Ready lane**: Card checkboxes and bulk action toolbar (**Prepare selected**,
  **Submit selected**, **Pass selected**, **Submit all clean**; `POST /api/ready/actions`).
  **Promotion after the flip (`ReadyPromoter`)**: turning dry-run off promotes all clean
  Ready packets immediately and on each worker pass. **Queued prepare**:
  `POST /api/apps/{name}/packet/prepare` delegates to worker (`prepare: true`) to run
  discovery, drafting, and dry run in one claim where the browser is. **Salary unit &
  currency conversions**: standing answers support `salary_period` (`annual`, `monthly`,
  `hourly`) and `salary_currency`, converting within currency (annual ÷ 12, annual ÷ 2080)
  and refusing cross-currency. **Aggregator link resolution**: poller follows aggregator
  listings (RemoteOK, Remotive, We Work Remotely) to employer postings, marking unresolved
  postings `aggregator`. **Heartbeat timer**: independent 30-second timer for worker
  heartbeat; UI shows "Worker last seen...". **Parked run test harness**:
  `IBrowserSubmitter` seam and worker-level tests for parked-on-code, Telegram reply,
  and mailbox paths.
- ✅ **Step 9 — Mobile list scroll, cookie consent, editable name fields & upload verifications** (#195, #199, #200–#205, #206, #207, #209; v1.30.1–v1.31.2):
  **Mobile scroll & touch**: dedicated scroll container with `overscroll-behavior: contain`
  prevents pull-to-refresh swipe interference on mobile Safari (#201). **Terms checkbox sizing**:
  Sign-in terms of service checkbox sized to 1.25rem with flex label wrapping (#199).
  **Editable first/last names**: name parser strips middle initials from last name ("Aaron K. Clark" →
  First: "Aaron", Last: "Clark"); First Name and Last Name become first-class rows in Answer
  Bank, honored during fill-time profile refresh (#200). **Cookie consent dismissal**:
  automatic dismissal of OneTrust, Cookiebot, Zoho, Workable, and "Accept all" dialogs
  before Apply, on new tabs, and before form fill (#202). **Provider re-detection & closed postings**:
  ATS provider re-evaluated at submit time; HTTP 404/410 reported as "gone" rather than
  "no form found" (#203). **Résumé attach verification**: failed attach treated as unmapped;
  submitter observes 2.5s for late `uploadFile` errors; sweeps `[role=group][aria-required]`
  wrapper elements; retries "Enter manually" (#195, #205). **Web component deep discovery (Zoho Recruit)**:
  inspects up to 12 ancestor levels for `label`, `data-label`, or row labels; detects
  required stars (`*`); disambiguates shared IDs; unlabelled questions and captchas never
  sent to LLM; social profiles mapped strictly (#207).
- ✅ **Step 10 — Pipeline view & "Apply later" trap prevention** (#210, #211, #212; v1.32.0):
  **Pipeline View**: clicking PIPELINE label in dashboard strip opens live submit queue
  drawer (`GET /api/pipeline`), evaluating worker gates in advance (phase, will do, reason,
  then_submit flag), Ready holding reasons, and "Submit all clean", auto-refreshing
  every 15 seconds. **"Apply later" trap prevention**: ignores lone email boxes as application
  forms; ignores buttons matching "later / send me the link / remind / subscribe / alert"
  across English, French, German, Spanish, and Portuguese; recognizes email sign-in gates
  vs clean dry runs.

## Backlog / ideas

- ✅ **Security: keep instance LLM API keys bound to trusted endpoints** (issue #47) — tenant
  `base_url` overrides currently inherit the instance `Llm__ApiKey` when the
  tenant does not store its own key. Change `EffectiveLlmConfig.Resolve` so an
  instance key is used only with the instance URL, or explicitly bind keys to an
  allowlisted origin. Also SSRF-check tenant LLM `base_url` before sending résumé
  data to it.
- ⬜ **Security: cap LLM response bodies** (issue #48) — `OpenAiCompatibleLlmClient` reads
  success/error bodies as full strings. Switch to `ResponseHeadersRead`, enforce
  a content-length ceiling, and stream with a hard byte cap before JSON parsing or
  error-detail logging.
- ✅ **Security: restrict forwarded-header trust** (issue #49) — `UseForwardedHeaders` accepts
  `X-Forwarded-For` / `X-Forwarded-Proto` only from configured `KnownProxies` / `KnownIPNetworks`.
- ✅ **Security/stability: add API request and field limits** (issue #50) — global JSON body caps
  plus per-field/per-list ceilings for applications, criteria keywords/excludes/ATS boards, résumé
  sections, links, and highlights.
- ✅ **Security: make Python link-check SSRF guard connect by validated IP** (issue #51) — the
  poller connects by validated IP address to close the DNS rebinding TOCTOU window.
- ✅ **Scalability/stability: serialize tenant poll runs** (issue #52) — per-tenant advisory
  lock serializes overlapping fast drain and scheduled poll passes.
- ✅ **Deployment hardening: split dev compose from production defaults** (issue #53) —
  hardened `docker-compose.production.yml` with non-root containers, no DB host port,
  `cap_drop`, and read-only root filesystems.
- ✅ **Scalability: paginate/delta-refresh applications** (issue #54) — `/api/apps` supports
  ETags / `If-None-Match` returning `304 Not Modified` for fast, low-overhead list validation.
- ⬜ **`tailscale serve` front-end** — rainy-day: serve over Tailscale instead of a
  self-signed TLS + reverse proxy (see the `plan.md` appendix).
