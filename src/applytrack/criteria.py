# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""User-editable discovery criteria for the poller.

The .NET API persists this data in ``search_profiles``; the poller reads it
through :class:`applytrack.db.PollRepo`.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field

LANES = ("dotnet", "devrel", "ai")

# Source ids the poller knows how to fetch without per-source configuration.
BUILTIN_SOURCES = (
    "remotive",
    "remoteok",
    "arbeitnow",
    "jobicy",
    "weworkremotely",
    "hn_whoishiring",
)

# ATS providers the board adder understands (public JSON boards, no auth).
ATS_PROVIDERS = ("greenhouse", "lever")

# The original per-lane keyword lists, flattened (order-preserving, de-duped) into
# one flat match list — the default the UI's keyword box starts from.
DEFAULT_KEYWORDS: tuple[str, ...] = (
    ".net", "dotnet", "c#", "csharp", "asp.net", "blazor", "entity framework",
    "ef core", "f#", "backend engineer", "back-end engineer", "backend developer",
    "web api", "microservices",
    "developer advocate", "developer relations", "devrel", "developer experience",
    "technical writer", "technical writing", "documentation engineer",
    "community manager", "developer evangelist", "evangelist", "dx engineer",
    "content engineer", "developer educator",
    "ai engineer", "agentic", "llm", "large language model", "machine learning",
    "ml engineer", "applied ai", "prompt engineer", "rag", "langchain",
    "generative ai", "genai", "ai/ml", "ai agent", "mlops",
)

# Only Remotive + RemoteOK are on by default, matching the original poller. New
# sources are opt-in (the user enables them in the Criteria panel).
DEFAULT_SOURCES: dict[str, bool] = {s: s in ("remotive", "remoteok") for s in BUILTIN_SOURCES}

MIN_SCORE_FLOOR = 0
MIN_SCORE_CEIL = 100


@dataclass
class AtsBoard:
    """A company's public ATS board to scan (provider + company slug)."""

    provider: str
    slug: str

    @classmethod
    def from_dict(cls, data: dict[str, object]) -> AtsBoard | None:
        provider = str(data.get("provider", "")).strip().lower()
        slug = str(data.get("slug", "")).strip()
        if provider not in ATS_PROVIDERS or not slug:
            return None
        return cls(provider=provider, slug=slug)


def _clean_list(values: object) -> list[str]:
    """Coerce arbitrary JSON into a de-duped, stripped, order-preserving str list."""
    if not isinstance(values, (list, tuple)):
        return []
    out: list[str] = []
    seen: set[str] = set()
    for v in values:
        s = str(v).strip()
        key = s.lower()
        if s and key not in seen:
            seen.add(key)
            out.append(s)
    return out


@dataclass
class Criteria:
    """Everything the poller needs to decide what to fetch and what to stage."""

    keywords: list[str] = field(default_factory=lambda: list(DEFAULT_KEYWORDS))
    default_lane: str = "ai"
    min_fit_score: int = 55
    remote_only: bool = False
    exclude_locations: list[str] = field(default_factory=list)
    sources: dict[str, bool] = field(default_factory=lambda: dict(DEFAULT_SOURCES))
    ats_boards: list[AtsBoard] = field(default_factory=list)

    # -- (de)serialization --------------------------------------------------

    @classmethod
    def from_dict(cls, data: dict[str, object]) -> Criteria:
        """Build a normalized Criteria from loose JSON, ignoring junk keys."""
        lane = str(data.get("default_lane", "ai")).strip().lower()
        raw_score = data.get("min_fit_score", 55)
        try:
            score = int(raw_score) if isinstance(raw_score, (int, float, str)) else 55
        except (TypeError, ValueError):
            score = 55

        raw_sources = data.get("sources", {})
        sources = dict(DEFAULT_SOURCES)
        if isinstance(raw_sources, dict):
            for key, on in raw_sources.items():
                if key in BUILTIN_SOURCES:
                    sources[key] = bool(on)

        raw_boards = data.get("ats_boards", [])
        boards: list[AtsBoard] = []
        if isinstance(raw_boards, (list, tuple)):
            seen: set[tuple[str, str]] = set()
            for entry in raw_boards:
                if not isinstance(entry, dict):
                    continue
                board = AtsBoard.from_dict(entry)
                if board and (board.provider, board.slug.lower()) not in seen:
                    seen.add((board.provider, board.slug.lower()))
                    boards.append(board)

        keywords = _clean_list(data.get("keywords"))
        return cls(
            keywords=keywords if keywords else list(DEFAULT_KEYWORDS),
            default_lane=lane if lane in LANES else "ai",
            min_fit_score=max(MIN_SCORE_FLOOR, min(MIN_SCORE_CEIL, score)),
            remote_only=bool(data.get("remote_only", False)),
            exclude_locations=_clean_list(data.get("exclude_locations")),
            sources=sources,
            ats_boards=boards,
        )

    def to_dict(self) -> dict[str, object]:
        return {
            "keywords": list(self.keywords),
            "default_lane": self.default_lane,
            "min_fit_score": self.min_fit_score,
            "remote_only": self.remote_only,
            "exclude_locations": list(self.exclude_locations),
            "sources": dict(self.sources),
            "ats_boards": [asdict(b) for b in self.ats_boards],
        }


# Defaults used when a tenant has no persisted profile row.
DEFAULT_CRITERIA = Criteria()
