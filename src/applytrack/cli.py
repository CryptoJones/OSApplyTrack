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


def _print_multi(results: dict[int, list[str]], scope: str) -> None:
    total = sum(len(names) for names in results.values())
    print(
        f"applytrack poll: {total} new lead(s) added across {len(results)} {scope}."
    )
    for tid, names in sorted(results.items()):
        for name in names:
            print(f"  + [{tid}] {name}")


def _poll(args: argparse.Namespace) -> int:
    from applytrack.db import PollRepo
    from applytrack.importer import connect
    from applytrack.poll import run_poll
    from applytrack.worker import drain_requests, run_all_tenants

    with connect(args.database_url) as conn:
        # Each lead/seen-key stands alone: a slug collision must skip one listing,
        # not abort the run, so commit per-statement rather than one big txn. The
        # on-demand drain also relies on autocommit to claim its queue rows.
        conn.autocommit = True
        if args.drain:
            # Fast cron: service the on-demand /api/poll queue, polling only the
            # tenants whose button was pressed since the last drain.
            _print_multi(drain_requests(conn, limit_per_source=args.limit), "queued tenant(s)")
            return 0
        if args.tenant is None:
            # Cron mode: fan out across every active tenant, fetching sources once.
            _print_multi(run_all_tenants(conn, limit_per_source=args.limit), "active tenant(s)")
            return 0
        repo = PollRepo(conn, args.tenant)
        profile = repo.load_profile()
        added = run_poll(repo, profile, limit_per_source=args.limit)
    print(f"applytrack poll: {len(added)} new lead(s) added for tenant {args.tenant}.")
    for name in added:
        print(f"  + {name}")
    return 0


def _import_md(args: argparse.Namespace) -> int:
    from applytrack.importer import connect, import_markdown

    data_dir = Path(args.dir).expanduser().resolve()
    with connect(args.database_url) as conn:
        imported = import_markdown(conn, data_dir, args.tenant)
    print(
        f"applytrack import-md: {len(imported)} application(s) "
        f"imported into tenant {args.tenant}.")
    for name in imported:
        print(f"  + {name}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="applytrack", description=__doc__)
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_poll = sub.add_parser("poll", help="fetch new matching leads from job boards")
    p_poll.add_argument(
        "--tenant", type=int, default=None,
        help="tenant_id to poll for (default: every active tenant — cron mode)")
    p_poll.add_argument(
        "--drain", action="store_true",
        help="service the on-demand poll queue (poll_requests) — run on a fast cron")
    p_poll.add_argument("--limit", type=int, default=40, help="max results per source to scan")
    p_poll.add_argument(
        "--database-url", default=None,
        help="libpq connection URL; falls back to DATABASE_URL / POSTGRES_* env")
    p_poll.set_defaults(func=_poll)

    p_import = sub.add_parser(
        "import-md", help="one-shot: load existing Markdown apps into Postgres")
    p_import.add_argument("--dir", default=str(_default_dir()), help="applications data folder")
    p_import.add_argument("--tenant", type=int, default=1, help="tenant_id to import into")
    p_import.add_argument(
        "--database-url", default=None,
        help="libpq connection URL; falls back to DATABASE_URL / POSTGRES_* env")
    p_import.set_defaults(func=_import_md)

    args = parser.parse_args(argv)
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())
