#!/usr/bin/env sh
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
#
# Run the .NET Testcontainers suite against a local Podman socket.
#
# Testcontainers speaks the Docker Remote API; Podman exposes a compatible socket,
# but it is not discovered from /var/run/docker.sock on many developer machines
# (especially macOS podman-machine and rootless Linux). This wrapper sets the
# runtime configuration for this test process only.
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$ROOT"

if [ -z "${DOCKER_HOST:-}" ]; then
  if [ -n "${PODMAN_SOCKET:-}" ]; then
    socket_path="$PODMAN_SOCKET"
  elif command -v podman >/dev/null 2>&1; then
    socket_path="$(podman machine inspect --format '{{.ConnectionInfo.PodmanSocket.Path}}' 2>/dev/null | head -n 1 || true)"
    if [ -z "$socket_path" ]; then
      socket_path="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/podman/podman.sock"
    fi
  else
    echo "podman is not on PATH and DOCKER_HOST is not set." >&2
    echo "Install Podman or set DOCKER_HOST/PODMAN_SOCKET before running this script." >&2
    exit 2
  fi

  if [ ! -S "$socket_path" ]; then
    echo "Podman socket not found: $socket_path" >&2
    echo "Start Podman first: 'podman machine start' on macOS, or enable podman.socket on Linux." >&2
    exit 2
  fi

  export DOCKER_HOST="unix://$socket_path"
fi

# Rootless Podman commonly cannot run Testcontainers' privileged resource reaper.
# The tests dispose their shared Postgres container explicitly via xUnit fixture
# teardown, so disabling Ryuk is the least surprising local default.
export TESTCONTAINERS_RYUK_DISABLED="${TESTCONTAINERS_RYUK_DISABLED:-true}"

exec dotnet test api/ApplyTrack.slnx "$@"
