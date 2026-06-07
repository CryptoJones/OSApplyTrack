# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""File-backed CRUD over one Markdown file per job application.

Each application is a single ``*.md`` file: a YAML frontmatter block of
structured fields (company, role, lane, status, dates, ...) followed by a
free-text Markdown notes body. The web layer (:mod:`applytrack.web.app`)
wraps this; the CLI imports it for ``serve``.

Every read/write/delete routes through :meth:`AppStore.safe_name`, which
rejects path traversal so a request can never escape the data directory.
"""

from __future__ import annotations

import os
import re
from collections.abc import Iterator
from dataclasses import asdict, dataclass
from datetime import date
from pathlib import Path
from typing import Any

import yaml

# Pipeline stages, in order. The UI colour-codes and orders by this list.
#   lead    discovered/triage      ready   materials drafted, awaiting your submit
#   applied you submitted          screen  recruiter/phone screen
#   onsite  interview loop         offer / rejected / passed
STATUSES = ["lead", "ready", "applied", "screen", "onsite", "offer", "rejected", "passed"]
# Which strength the role leads with — mirrors the cover-letter \lane switch.
LANES = ["dotnet", "devrel", "ai"]

_ILLEGAL_FILENAME_CHARS = re.compile(r'[\\/:*?"<>|\x00-\x1f]')
_FRONTMATTER_RE = re.compile(r"^---\n(.*?)\n---\n?(.*)$", re.DOTALL)


class AppError(Exception):
    """Raised for bad application names or content the store rejects."""


class AppNotFoundError(AppError):
    """Raised when a requested application does not exist."""


class AppConflictError(Exception):
    """Raised when a file changed on disk since the caller last read it.

    Deliberately NOT an :class:`AppError`: the web layer maps it to HTTP 409
    (the write is valid, the *base version* is stale), whereas AppError -> 400.
    """


@dataclass
class AppFields:
    """The structured contents of a single job-application note."""

    company: str
    role: str = ""
    lane: str = "ai"
    status: str = "lead"
    link: str = ""
    location: str = ""
    salary: str = ""
    source: str = ""
    contact: str = ""
    contact_email: str = ""
    applied: str = ""
    followup: str = ""
    created: str = ""
    score: str = ""
    notes: str = ""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> AppFields:
        def s(key: str) -> str:
            val = data.get(key, "")
            return "" if val is None else str(val).strip()

        lane = s("lane").lower() or "ai"
        status = s("status").lower() or "lead"
        return cls(
            company=s("company"),
            role=s("role"),
            lane=lane if lane in LANES else "ai",
            status=status if status in STATUSES else "lead",
            link=s("link"),
            location=s("location"),
            salary=s("salary"),
            source=s("source"),
            contact=s("contact"),
            contact_email=s("contact_email"),
            applied=s("applied"),
            followup=s("followup"),
            created=s("created"),
            score=s("score"),
            notes=str(data.get("notes", "")).rstrip(),
        )


@dataclass
class AppSummary:
    """Lightweight listing entry for the sidebar."""

    filename: str
    company: str
    role: str
    lane: str
    status: str
    contact: str
    contact_email: str
    applied: str
    followup: str
    score: str
    link: str
    snippet: str


def today() -> str:
    return date.today().isoformat()


def filename_for(fields: AppFields) -> str:
    """Build the slug filename from company + role (disk-free).

    Shared with the poller's DB writer so a lead staged by the Python runtime gets
    the exact same ``name`` the .NET API's ``Slug.FilenameFor`` would mint.
    """
    stem = f"{fields.company} {fields.role}".strip() or fields.company
    cleaned = _ILLEGAL_FILENAME_CHARS.sub(" ", stem).strip()
    cleaned = re.sub(r"\s+", "-", cleaned).lower()
    if not cleaned:
        raise AppError("company/role produces an empty filename")
    return f"{cleaned}.md"


def parse_app(md: str) -> AppFields:
    """Parse a file's YAML frontmatter + Markdown body into fields."""
    match = _FRONTMATTER_RE.match(md.lstrip("﻿"))
    if match:
        front_raw, body = match.group(1), match.group(2)
        try:
            data = yaml.safe_load(front_raw) or {}
        except yaml.YAMLError:
            data = {}
        if not isinstance(data, dict):
            data = {}
    else:
        data, body = {}, md
    data["notes"] = body.strip()
    return AppFields.from_dict(data)


def render_fields(f: AppFields) -> str:
    """Render structured fields back into frontmatter + body Markdown."""
    front: dict[str, Any] = {
        "company": f.company,
        "role": f.role,
        "lane": f.lane,
        "status": f.status,
        "link": f.link,
        "location": f.location,
        "salary": f.salary,
        "source": f.source,
        "contact": f.contact,
        "contact_email": f.contact_email,
        "applied": f.applied,
        "followup": f.followup,
        "created": f.created or today(),
        "score": f.score,
    }
    front_yaml = yaml.safe_dump(front, sort_keys=False, allow_unicode=True).rstrip()
    body = f.notes.strip()
    return f"---\n{front_yaml}\n---\n\n{body}\n" if body else f"---\n{front_yaml}\n---\n"


class AppStore:
    """CRUD over ``*.md`` applications in a single data folder."""

    def __init__(self, data_dir: Path | str) -> None:
        self.data_dir = Path(data_dir).expanduser()

    # -- naming / safety ----------------------------------------------------

    def safe_name(self, name: str) -> Path:
        """Resolve a user-supplied name to a path inside the data dir.

        Raises :class:`AppError` on anything that looks like traversal.
        """
        name = (name or "").strip()
        if not name or name in {".", ".."}:
            raise AppError("empty or invalid name")
        if "/" in name or "\\" in name or os.sep in name or (os.altsep and os.altsep in name):
            raise AppError(f"name may not contain path separators: {name!r}")
        if ".." in Path(name).parts:
            raise AppError(f"name may not contain '..': {name!r}")
        base = Path(name).name
        if base != name:
            raise AppError(f"unsafe name: {name!r}")
        if not base.endswith(".md"):
            base += ".md"
        target = (self.data_dir / base).resolve()
        if target.parent != self.data_dir.resolve():
            raise AppError(f"name escapes the data directory: {name!r}")
        return target

    def filename_for(self, fields: AppFields) -> str:
        return filename_for(fields)

    # -- reads --------------------------------------------------------------

    def _paths(self) -> Iterator[Path]:
        if not self.data_dir.is_dir():
            return
        for path in sorted(self.data_dir.glob("*.md")):
            if path.name.startswith("."):
                continue
            yield path

    def _summarize(self, path: Path, text: str | None = None) -> AppSummary:
        if text is None:
            text = path.read_text(encoding="utf-8")
        f = parse_app(text)
        snippet = re.sub(r"\s+", " ", f.notes).strip()
        if len(snippet) > 160:
            snippet = snippet[:157].rstrip() + "..."
        return AppSummary(
            filename=path.name,
            company=f.company or path.stem,
            role=f.role,
            lane=f.lane,
            status=f.status,
            contact=f.contact,
            contact_email=f.contact_email,
            applied=f.applied,
            followup=f.followup,
            score=f.score,
            link=f.link,
            snippet=snippet,
        )

    def list_apps(self) -> list[AppSummary]:
        order = {s: i for i, s in enumerate(STATUSES)}
        summaries = [self._summarize(p) for p in self._paths()]
        summaries.sort(key=lambda s: (order.get(s.status, 99), s.company.lower()))
        return summaries

    def stats(self) -> dict[str, dict[str, int]]:
        by_status: dict[str, int] = {}
        by_lane: dict[str, int] = {}
        for s in self.list_apps():
            by_status[s.status] = by_status.get(s.status, 0) + 1
            by_lane[s.lane] = by_lane.get(s.lane, 0) + 1
        return {"status": by_status, "lane": by_lane}

    def read_app(self, name: str) -> str:
        path = self.safe_name(name)
        if not path.is_file():
            raise AppNotFoundError(f"application not found: {name!r}")
        return path.read_text(encoding="utf-8")

    def read_fields(self, name: str) -> AppFields:
        return parse_app(self.read_app(name))

    def version(self, name: str) -> str:
        """Opaque token for a file's on-disk state (mtime + size)."""
        path = self.safe_name(name)
        if not path.is_file():
            return ""
        st = path.stat()
        return f"{st.st_mtime_ns}-{st.st_size}"

    # -- writes -------------------------------------------------------------

    def write_app(self, name: str, content: str, expected_version: str | None = None) -> str:
        path = self.safe_name(name)
        if expected_version is not None and path.is_file():
            current = self.version(name)
            if current != expected_version:
                raise AppConflictError(
                    f"{name!r} changed on disk (expected {expected_version!r}, found {current!r})"
                )
        self.data_dir.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path.name

    def create_app(self, fields: AppFields) -> str:
        if not fields.company.strip():
            raise AppError("an application requires a company")
        if not fields.created:
            fields.created = today()
        filename = self.filename_for(fields)
        path = self.safe_name(filename)
        if path.exists():
            raise AppError(f"an application named {filename!r} already exists")
        return self.write_app(filename, render_fields(fields))

    def update_app(
        self, name: str, fields: AppFields, expected_version: str | None = None
    ) -> str:
        self.read_app(name)  # ensure it exists
        return self.write_app(name, render_fields(fields), expected_version=expected_version)

    def delete_app(self, name: str) -> None:
        path = self.safe_name(name)
        if not path.is_file():
            raise AppNotFoundError(f"application not found: {name!r}")
        path.unlink()
