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

# 2. Secrets/env (NOT in the unit files): connection strings + keys
mkdir -p ~/.config/applytrack
cat > ~/.config/applytrack/applytrack.env <<'EOF'
POSTGRES_USER=applytrack
POSTGRES_DB=applytrack
POSTGRES_PASSWORD=<generate one>
ConnectionStrings__Postgres=Host=applytrack-db;Port=5432;Database=applytrack;Username=applytrack;Password=<same>
DATABASE_URL=postgresql://applytrack:<same>@applytrack-db:5432/applytrack
# Optional cover-letter engine + per-tenant key encryption — see README.
#Llm__BaseUrl=
#Llm__Model=
#APPLYTRACK_SECRETS_KEY=
# Optional email (magic-link login). Leave Email__Host unset to log links to the
# console instead of sending. To relay through any SMTP provider (e.g. Resend):
#Email__Host=smtp.resend.com
#Email__Port=465
#Email__Username=resend
#Email__Password=<your SMTP password / API key>
#Email__From=apply@your-verified-domain
#Email__FromName=OSApplyTrack
EOF
chmod 600 ~/.config/applytrack/applytrack.env

# 3. Go
systemctl --user daemon-reload
systemctl --user start applytrack-api.service applytrack-poller.service
loginctl enable-linger "$USER"   # keep it running after logout
```

## Port guard rail

`applytrack-api.container` publishes host port **8080** by default and ships an
`ExecStartPre` check that **refuses to start when something else already
listens on that port** — a clear journal error instead of silently winning the
bind race against whatever usually lives there (vLLM, llama.cpp, and half of
all dev tools default to 8080). To relocate, edit `PublishPort` (host side
only — the container side stays 8080) **and the port in the ExecStartPre
line**, then `systemctl --user daemon-reload && systemctl --user restart
applytrack-api`.
