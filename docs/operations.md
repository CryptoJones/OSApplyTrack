# Operating an instance: admin CLI, backup & restore

For whoever runs a self-hosted OSApplyTrack for more than one person. Two parts: the
**admin CLI** (see and manage accounts), and the **backup & restore runbook** (the
whole instance: the database *and* the key that decrypts it).

A tenant's own backup is a different thing: **Settings · Account · Export**
(`GET /api/account/export`, version 2 since 1.62.0) is one account's data, with no
secrets, for that person to keep or move. This page is about losing the box.

## Admin CLI

There is no admin HTTP API, on purpose: a web route that can disable accounts or hand
out auto-apply is one more thing to steal a session for. The CLI ships inside the api
image and runs where the owner's connection string already is — the **api** container.
Never run it in the agent or poller container: their Postgres roles
(`applytrack_agent`, `applytrack_poller`, #345) cannot read or change accounts, and the
CLI says so and stops.

| Command | What it does |
| --- | --- |
| `admin users` | Every account: id, email, status, whether it is on the auto-apply allowlist, created. |
| `admin usage [<user>]` | Per account: applications, cover letters, agent evidence rows and screenshot bytes, live sessions, last sign-in, last seen, last poll. |
| `admin allow <user> [note...]` | Put the account on the auto-apply allowlist (`agent_allowlist`). |
| `admin disallow <user>` | Take it off. The agent stops judging it and never claims its queued runs. |
| `admin disable <user>` | Set `users.status` to `disabled`: its sessions and API tokens stop working on their next request, no sign-in link is mailed, and the poller and the agent skip it. Nothing is deleted. |
| `admin enable <user>` | Set it back to `active`. Sessions that have not expired work again. |

`<user>` is an account id or its email (any case). Exit codes: `0` done, `1` no such
account or a database error, `2` bad usage. Output is space-aligned, one account per
line, so `awk` works on it. Times are UTC; `-` means never.

```sh
# Compose (either compose file)
docker compose exec api dotnet ApplyTrack.Api.dll admin users
docker compose exec api dotnet ApplyTrack.Api.dll admin allow ada@example.com the operator

# Quadlet
podman exec applytrack-api dotnet ApplyTrack.Api.dll admin usage

# From a checkout (reads appsettings.json / ConnectionStrings__Postgres)
dotnet run --project api/ApplyTrack.Api -- admin users
```

*Last poll* is stamped by the poller at the end of each tenant's pass (full, ATS-only
or on-demand) since 1.63.0; it stays `-` until the first pass after the upgrade, and
for a tenant whose pass keeps failing (see the poller's log). *Last sign-in* and *last
seen* come from the account's sessions, which the retention sweep deletes a day after
they expire — an account idle for longer than a session lasts shows `-`.

## Backup

A backup is **two things**, and one without the other is not a backup:

1. **The database** — a `pg_dump` of the `applytrack` database.
2. **The encryption key** — `APPLYTRACK_SECRETS_KEY`, or the key file the api generated
   when that is unset (`/var/lib/applytrack/secrets.key` in the `secrets` volume).
   Résumé PDFs, cover letters, packet answers and posting text, evidence screenshots,
   and each tenant's LLM key, Telegram bot token and board passwords and sessions are
   stored sealed with it ([Encryption at rest](../README.md#encryption-at-rest)).
   **Lose the key and those columns are unrecoverable** — no one, including this
   project, can decrypt them. Everything else (applications, criteria, history) still
   restores.

Keep the key somewhere other than the dump: a password manager or a separate secrets
store. A dump and its key side by side in one bucket is a plaintext backup with extra
steps. Also keep the env files (`.env.production`, or `applytrack.env`, `agent.env`
and `poller.env` for quadlet): they hold the owner, agent and poller role passwords and
whatever else you configured.

### Compose

```sh
# The database, custom format (compressed, and restorable table by table).
docker compose -f docker-compose.production.yml exec -T db \
  pg_dump -U applytrack -Fc applytrack > "applytrack-$(date +%F).dump"

# The key. The production file requires APPLYTRACK_SECRETS_KEY, so it is in
# .env.production: store that value (and the file) in your secrets store. With the
# quickstart docker-compose.yml and no key set, copy out the generated one:
docker compose cp api:/var/lib/applytrack/secrets.key ./applytrack-secrets.key
chmod 600 ./applytrack-secrets.key
```

### Quadlet

```sh
podman exec applytrack-db pg_dump -U applytrack -Fc applytrack > "applytrack-$(date +%F).dump"

# The key: the APPLYTRACK_SECRETS_KEY line in ~/.config/applytrack/applytrack.env, or,
# when it is unset, the generated key file in the applytrack-secrets volume:
podman volume export applytrack-secrets -o applytrack-secrets.tar
```

`pg_dump` takes a consistent snapshot while the app runs; nothing needs stopping. A
nightly systemd timer or cron job running the dump line, plus an off-host copy, is
enough for most instances.

## Restore

Restore into an empty database, then let the api put the roles and grants back.

1. **Stop everything that writes**: the api, the agent and the poller (leave `db`
   running).
   - Compose: `docker compose -f docker-compose.production.yml stop api agent poller`
   - Quadlet: `systemctl --user stop applytrack-api applytrack-agent applytrack-poller`
2. **Put the key back first**, where the api, the agent and the poller all see it:
   - Production compose: the same `APPLYTRACK_SECRETS_KEY` in `.env.production` (its
     containers are read-only and have no key volume; the env value is the only way).
   - Quickstart compose with a generated key: copy it back into the `secrets` volume,
     `docker compose cp ./applytrack-secrets.key api:/var/lib/applytrack/secrets.key`.
   - Quadlet: the same `APPLYTRACK_SECRETS_KEY` line in `applytrack.env`, `agent.env` and
     `poller.env`, or, for a generated key,
     `podman volume import applytrack-secrets applytrack-secrets.tar`.
3. **Recreate the database and load the dump.** `--no-owner --no-privileges` because the
   agent's and poller's roles may not exist on this server yet; the api re-creates them
   and re-applies every grant on boot.

   ```sh
   # Compose: replace `podman exec -i applytrack-db` with
   #   docker compose -f docker-compose.production.yml exec -T db
   podman exec -i applytrack-db dropdb -U applytrack --if-exists applytrack
   podman exec -i applytrack-db createdb -U applytrack -O applytrack applytrack
   podman exec -i applytrack-db pg_restore -U applytrack -d applytrack \
     --no-owner --no-privileges < applytrack-2026-09-26.dump
   ```

4. **Start the api first** and wait for `/health`. On boot it runs any migrations the
   dump predates, creates `applytrack_agent` / `applytrack_poller` from
   `AGENT_DB_PASSWORD` / `POLLER_DB_PASSWORD`, re-grants them, and sweeps the sealed
   columns. Check its log: a line ending `could not be read` means a sealed value is
   under a key this instance does not have — step 2 is wrong. Then start the agent and
   the poller.
5. **Re-apply what changed since the backup.** The dump has the account states of its
   own day: re-run `admin disable` for any account you disabled after it, and
   `admin disallow` for any allowlist removal.

**Mind the retention sweep when restoring an old dump.** It runs in the api five
minutes after boot and trims by age relative to *now* (screenshots older than
`Retention__ScreenshotDays`, audit events older than `Retention__AgentEventDays`). If you
are restoring to recover old evidence, start the api with `Retention__Enabled=false`
until you have what you need.

## Restore drill

A backup nobody has restored is a hope. Once a quarter, and after changing how you back
up, restore the latest dump and key into a throwaway Postgres — not the live one:

```sh
podman run -d --name at-drill -e POSTGRES_USER=applytrack -e POSTGRES_PASSWORD=drill \
  -e POSTGRES_DB=applytrack docker.io/library/postgres:17-alpine
sleep 5
podman exec -i at-drill pg_restore -U applytrack -d applytrack --no-owner --no-privileges \
  < applytrack-2026-09-26.dump

# Boot a throwaway api on it with the backed-up key: it must migrate, and its log must
# have no "could not be read" line. No port published; nothing reaches it.
podman run --rm -d --name at-drill-api --network container:at-drill \
  -e ConnectionStrings__Postgres='Host=127.0.0.1;Database=applytrack;Username=applytrack;Password=drill' \
  -e APPLYTRACK_SECRETS_KEY="$KEY_FROM_YOUR_SECRETS_STORE" -e Retention__Enabled=false \
  ghcr.io/cryptojones/osapplytrack-api:<version>
sleep 20; podman logs at-drill-api 2>&1 | grep -E 'could not be read|Now listening'

# The accounts and their counts should match production's `admin usage`.
podman exec at-drill-api dotnet ApplyTrack.Api.dll admin usage

podman rm -f at-drill-api at-drill
```

For a generated key file rather than an env key, mount it instead:
`-v applytrack-secrets:/var/lib/applytrack:ro` (after `podman volume import` into a
scratch volume name, if the live one is on the same host).

*Proudly Made in Nebraska. Go Big Red! 🌽 <https://xkcd.com/2347/>*
