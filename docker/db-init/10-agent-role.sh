#!/bin/sh
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
#
# Runs once, when the Postgres data volume is first initialised: create the agent
# worker's least-privilege role when the operator set AGENT_DB_PASSWORD. The grants
# themselves are applied by the API on every boot (Migrations/Always/agent_role.sql),
# after the tables exist. Without AGENT_DB_PASSWORD nothing happens and the agent
# runs as the owner role, as in the quickstart compose.
set -eu
[ -n "${AGENT_DB_PASSWORD:-}" ] || exit 0
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    -v pw="$AGENT_DB_PASSWORD" <<'EOSQL'
CREATE ROLE applytrack_agent LOGIN PASSWORD :'pw';
EOSQL
echo "created role applytrack_agent"
