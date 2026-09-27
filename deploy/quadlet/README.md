# Quadlet deployment (rootless podman + systemd)

The compose file is the quickstart; these units are the long-haul deployment —
rootless podman containers managed by systemd via
[Quadlet](https://docs.podman.io/en/latest/markdown/podman-systemd.unit.5.html),
so the stack survives reboots, restarts on failure, and logs to the journal.

## Install

```sh
# 1. Units
mkdir -p ~/.config/containers/systemd
cp deploy/quadlet/applytrack* ~/.config/containers/systemd/

# 2. Secrets/env (NOT in the unit files): one file per trust level (#345).
#    applytrack.env: db + api. The owner role, plus the passwords of the agent's and
#    the poller's own roles, which the api creates (and re-passwords) on every boot.
mkdir -p ~/.config/applytrack
cat > ~/.config/applytrack/applytrack.env <<'EOF'
POSTGRES_USER=applytrack
POSTGRES_DB=applytrack
POSTGRES_PASSWORD=<generate one>
ConnectionStrings__Postgres=Host=applytrack-db;Port=5432;Database=applytrack;Username=applytrack;Password=<same>
# The agent's and the poller's own roles: independent random values.
AGENT_DB_PASSWORD=<generate another>
POLLER_DB_PASSWORD=<generate a third>
# Optional cover-letter engine — see README.
#Llm__BaseUrl=
#Llm__Model=
# Encryption-at-rest key. Leave unset and the api generates one on first run into the
# applytrack-secrets volume (back that volume up with the database); set it to manage
# the key yourself, and then put the same line in agent.env and poller.env.
# APPLYTRACK_SECRETS_KEY_PREVIOUS=<old> rotates it — see README.
#APPLYTRACK_SECRETS_KEY=
# Public origin — the URL you browse to (scheme, no trailing slash). The api listens
# on 127.0.0.1:8080 only: put a TLS reverse proxy in front and use its https origin.
# Sign-in links are built from this, never the request Host header (#337); required
# once Email__Host is set. AllowedHosts pins the Host headers the api answers at all.
App__PublicBaseUrl=<https://apply.example.com>
AllowedHosts=<apply.example.com;localhost>
# A proxy that reaches the api from a non-loopback address must be trusted, or the
# session cookie loses Secure (the api logs the exact peer address to put here).
#ForwardedHeaders__KnownProxies__0=
#ForwardedHeaders__KnownNetworks__0=
# Optional email (magic-link login). Leave Email__Host unset to log links to the
# console instead of sending. To relay through any SMTP provider (e.g. Resend):
#Email__Host=smtp.resend.com
#Email__Port=465
#Email__Username=resend
#Email__Password=<your SMTP password / API key>
#Email__From=apply@your-verified-domain
#Email__FromName=OSApplyTrack
EOF

#    agent.env: the agent worker sits next to the browser, so never the owner's password.
cat > ~/.config/applytrack/agent.env <<'EOF'
ConnectionStrings__Postgres=Host=applytrack-db;Port=5432;Database=applytrack;Username=applytrack_agent;Password=<AGENT_DB_PASSWORD>
App__PublicBaseUrl=<same as applytrack.env>
# The same values as applytrack.env, when you set them there:
#APPLYTRACK_SECRETS_KEY=
#Llm__BaseUrl=
#Llm__Model=
#Llm__ApiKey=
# The agent sends email notifications (#355), so it needs the Email__* lines too.
#Email__Host=
#Email__Port=
#Email__Username=
#Email__Password=
#Email__From=
EOF

#    poller.env: the discovery poller parses the open web, so never the owner's password.
cat > ~/.config/applytrack/poller.env <<'EOF'
DATABASE_URL=postgresql://applytrack_poller:<POLLER_DB_PASSWORD>@applytrack-db:5432/applytrack
#APPLYTRACK_SECRETS_KEY=
#TYPESAFE_API_KEY=
EOF
chmod 600 ~/.config/applytrack/*.env

# 3. Go
systemctl --user daemon-reload
cp docker/browser/seccomp_profile.json ~/.config/applytrack/
systemctl --user start applytrack-api.service applytrack-poller.service applytrack-proxy.service applytrack-browser.service applytrack-agent.service
loginctl enable-linger "$USER"   # keep it running after logout
```

## Admin and backups

Accounts are managed with the admin CLI in the api container
(`podman exec applytrack-api dotnet ApplyTrack.Api.dll admin users`), and a backup is a
`pg_dump` of `applytrack-db` **plus** the encryption key (the `APPLYTRACK_SECRETS_KEY`
line, or `podman volume export applytrack-secrets`). Commands, restore and a restore
drill: [`docs/operations.md`](../../docs/operations.md).

## Upgrading a deploy from before 1.55.13 (#345)

1.55.13 gives the agent and the poller their own Postgres roles and env files, runs
the api, agent and poller containers read-only as uid 1654 with no capabilities,
and publishes the api on **127.0.0.1:8080** instead of every interface. Before
restarting on the new units:

1. Add `AGENT_DB_PASSWORD=` and `POLLER_DB_PASSWORD=` (fresh random values) to
   `applytrack.env`, and remove `DATABASE_URL` from it (only the poller read it).
2. Create `agent.env` and `poller.env` as above (`chmod 600`). If `applytrack.env`
   sets `APPLYTRACK_SECRETS_KEY`, `Llm__*`, `Email__*` or `TYPESAFE_API_KEY`, copy them across.
3. Copy the new units, `systemctl --user daemon-reload`, restart `applytrack-api`
   **first** (it creates both roles and their grants), wait for `/health`, then
   restart `applytrack-agent` and `applytrack-poller`.
4. The api is no longer on the LAN. Reach it through a TLS reverse proxy on the
   host (trusting it with `ForwardedHeaders__KnownProxies__0`), or — knowingly
   sending the session cookie without `Secure` over plain HTTP — set `PublishPort`
   back to `8080:8080` in your copy of the unit.

Without `agent.env`/`poller.env` those two units do not start. A wrong password
shows in their journals as `password authentication failed for user
"applytrack_agent"` (or `_poller`); the agent waits up to five minutes for its
role, the poller retries every tick.

Images 1.55.13 through 1.65.2 shipped the Playwright driver executable by root
only, so an agent on these units logged `Win32Exception (13) ... trying to start
process '/app/.playwright/node/linux-x64/node' ... Permission denied` on every
packet build and submission (#392). Use 1.65.3 or later; if you ran the agent as
`User=root` to get past it, put it back on `User=1654:1654`.

## Port guard rail

`applytrack-api.container` publishes host port **8080** (loopback only) by default and ships an
`ExecStartPre` check that **refuses to start when something else already
listens on that port** — a clear journal error instead of silently winning the
bind race against whatever usually lives there (vLLM, llama.cpp, and half of
all dev tools default to 8080). To relocate, edit `PublishPort` (host side
only — the container side stays 8080) **and the port in the ExecStartPre
line**, then `systemctl --user daemon-reload && systemctl --user restart
applytrack-api`.
