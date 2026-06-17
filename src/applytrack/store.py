# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Pure helpers for Markdown application notes.

The .NET API owns CRUD now; this module only keeps the small codec shared by the
importer, poller, and DB writer.
"""

from __future__ import annotations

import re
from dataclasses import asdict, dataclass
from datetime import date
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
        raise ValueError("company/role produces an empty filename")
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

