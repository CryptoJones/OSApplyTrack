# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Command-line entry point for the applytrack poller.

`applytrack poll` runs the job-discovery poller once (intended to be wired to an
hourly cron / systemd timer). The web UI is served by the .NET API in this
rewrite, so `serve`/`gencert` no longer live here.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path


def _default_dir() -> Path:
    return Path(os.environ.get("APPLYTRACK_DIR") or (Path.cwd() / "applications"))


def _poll(args: argparse.Namespace) -> int:
    from applytrack.poll import run_poll

    data_dir = Path(args.dir).expanduser().resolve()
    added = run_poll(data_dir, limit_per_source=args.limit)
    print(f"applytrack poll: {len(added)} new lead(s) added.")
    for name in added:
        print(f"  + {name}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="applytrack", description=__doc__)
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_poll = sub.add_parser("poll", help="fetch new matching leads from job boards")
    p_poll.add_argument("--dir", default=str(_default_dir()), help="applications data folder")
    p_poll.add_argument("--limit", type=int, default=40, help="max results per source to scan")
    p_poll.set_defaults(func=_poll)

    args = parser.parse_args(argv)
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())
