# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Hourly job-discovery poller.

Pulls remote roles from public job-board APIs/feeds, scores each against the
user's keyword list, and adds *new* matches as ``lead`` ``.md`` files. It never
drafts a cover letter and never submits anything: drafting is on demand.

What counts as a match is no longer hardcoded — it comes from a tenant's
:class:`applytrack.criteria.Criteria`, which the .NET API persists as a
``search_profiles`` row and the poller loads through
:class:`applytrack.db.PollRepo`: the flat keyword list, the minimum fit score, a
remote-only toggle + location blocklist, which sources are enabled, and which
company ATS boards to scan.

This runtime no longer touches the disk store; it shares one Postgres with the
API and is driven against a tenant-scoped repo (see :class:`LeadRepo`).

Deduplication is load-bearing — a company must never be pinged twice. For each
run an in-memory ledger is seeded from the tenant's existing ``applications``
rows, and every listing is checked against it using two independent keys: the
normalized listing URL and the normalized ``company + role`` slug. If *either*
key has been seen, the listing is skipped. New keys are recorded even when
staging fails, so a transient error never causes a re-ping.
"""

from __future__ import annotations

import json
import logging
import re
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from typing import Protocol
from urllib.parse import urlsplit

# Stdlib import is the Element *type* only — never a parser entry point; all parsing
# below goes through defusedxml, which is XXE / billion-laughs safe.
from xml.etree.ElementTree import Element  # nosec B405

import defusedxml.ElementTree as ET  # hardened XML parser (forbids entities/external)
import httpx
import psycopg

from applytrack.criteria import AtsBoard, Criteria
from applytrack.linkcheck import BROWSER_HEADERS, is_reachable
from applytrack.store import AppFields

logger = logging.getLogger(__name__)

_TAG_RE = re.compile(r"<[^>]+>")
_WS_RE = re.compile(r"\s+")
_SLUG_RE = re.compile(r"[^a-z0-9]+")
_PARENS_RE = re.compile(r"\([^)]*\)")
_URL_RE = re.compile(r"https?://[^\s<>\"')]+")

# Signals that a role is remote-friendly, for the optional remote-only filter.
_REMOTE_HINTS = ("remote", "anywhere", "worldwide", "distributed", "work from home")


@dataclass
class Listing:
    """A raw role pulled from a job board, before scoring/dedup."""

    company: str
    role: str
    link: str = ""
    location: str = ""
    salary: str = ""
    source: str = ""
    description: str = ""


# A fetcher takes an http client + per-source scan cap and returns listings.
Fetcher = Callable[[httpx.Client, int], list[Listing]]


# -- text helpers -----------------------------------------------------------


def _strip_html(text: str) -> str:
    return _WS_RE.sub(" ", _TAG_RE.sub(" ", text or "")).strip()


def _norm_url(url: str) -> str:
    """Scheme/host/trailing-slash-insensitive URL key (query & fragment dropped)."""
    url = (url or "").strip()
    if not url:
        return ""
    parts = urlsplit(url if "//" in url else "//" + url)
    host = parts.netloc.lower().removeprefix("www.")
    path = parts.path.rstrip("/").lower()
    return f"{host}{path}" or url.lower()


def _norm_slug(company: str, role: str) -> str:
    """Normalized ``company + role`` key, robust to spacing/punctuation.

    Trailing parentheticals are dropped before keying, so the same role posted
    across locations collapses to one key and a company is not pinged once per
    city. This errs toward over-dedup, the safe direction for not annoying
    employers.
    """
    role = _PARENS_RE.sub(" ", role)
    return _SLUG_RE.sub("-", f"{company} {role}".lower()).strip("-")


def _norm_company(company: str) -> str:
    """Normalized company key for the blacklist (spacing/punctuation-insensitive)."""
    return _SLUG_RE.sub("-", (company or "").lower()).strip("-")


def _ats_label(slug: str) -> str:
    """Turn an ATS board slug into a display company name (``foo-bar`` -> ``Foo Bar``)."""
    return re.sub(r"[-_]+", " ", slug).strip().title() or slug


# -- scoring ----------------------------------------------------------------


def classify(title: str, description: str, keywords: Iterable[str]) -> tuple[int, list[str]]:
    """Score a role 0-100 against the flat keyword list.

    Returns ``(score, matched_keywords)``; ``([])`` score 0 when nothing matched.
    Title hits weigh more than body hits, mirroring the original lane scorer:
    base 50, +9 per keyword in the title, +3 per keyword found only in the body.
    """
    title_l = (title or "").lower()
    text_l = f"{title} {description}".lower()
    kws = [k.lower() for k in keywords if k]
    hits = [k for k in kws if k in text_l]
    if not hits:
        return 0, []
    title_hits = sum(1 for k in kws if k in title_l)
    body_hits = len(hits) - title_hits
    score = 50 + 9 * title_hits + 3 * body_hits
    return score, hits


def _looks_remote(item: Listing) -> bool:
    """Best-effort: does this listing read as remote-friendly?

    A blank location is treated as remote (most sources here are remote-only
    boards that omit it); otherwise we look for an explicit remote signal in the
    location or role text.
    """
    loc = (item.location or "").strip().lower()
    if not loc:
        return True
    blob = f"{loc} {item.role}".lower()
    return any(h in blob for h in _REMOTE_HINTS)


def _passes_location(item: Listing, criteria: Criteria) -> bool:
    """Apply the remote-only toggle + exclude-locations blocklist."""
    if criteria.remote_only and not _looks_remote(item):
        return False
    loc = (item.location or "").lower()
    return not any(term.lower() in loc for term in criteria.exclude_locations if term)


# -- ledger -----------------------------------------------------------------


@dataclass
class Seen:
    """The per-run dedup ledger, backed by the ``seen`` table.

    ``urls`` / ``slugs`` hold the normalized keys already pinged — loaded from the
    ``seen`` table and seeded from the tenant's existing applications. ``sink``, if
    set, persists each *newly* seen key back to the table so a company is never
    re-pinged on a later run, even after its lead is deleted.
    """

    urls: set[str]
    slugs: set[str]
    sink: Callable[[str, str], None] | None = None

    def has(self, url: str, slug: str) -> bool:
        nu = _norm_url(url)
        return (bool(nu) and nu in self.urls) or (bool(slug) and slug in self.slugs)

    def note(self, url: str, slug: str) -> tuple[str, str]:
        """Record keys in memory only; return the normalized keys newly added."""
        nu = _norm_url(url)
        new_url = nu if nu and nu not in self.urls else ""
        new_slug = slug if slug and slug not in self.slugs else ""
        if nu:
            self.urls.add(nu)
        if slug:
            self.slugs.add(slug)
        return new_url, new_slug

    def add(self, url: str, slug: str) -> None:
        """Record keys and persist the newly seen ones through ``sink``."""
        new_url, new_slug = self.note(url, slug)
        if self.sink is not None and (new_url or new_slug):
            self.sink(new_url, new_slug)


@dataclass
class Blacklist:
    """Companies to skip entirely, read from the ``blacklist`` table.

    Unlike :class:`Seen` (which blocks one URL/role), a blacklisted company is
    dropped wholesale, so no role from it is ever staged. The .NET API owns the
    writes (``/api/blacklist``); the poller only reads, comparing on the same
    normalized key.
    """

    companies: set[str]

    def has(self, company: str) -> bool:
        key = _norm_company(company)
        return bool(key) and key in self.companies


class LeadRepo(Protocol):
    """The tenant-scoped data access ``run_poll`` needs (see :class:`applytrack.db.PollRepo`).

    Kept narrow so the offline tests can drive ``run_poll`` with an in-memory
    fake — no Postgres, no network.
    """

    def iter_existing(self) -> Iterable[tuple[str, str, str]]:
        """Yield ``(link, company, role)`` for every application already stored."""
        ...

    def blacklist_companies(self) -> Iterable[str]:
        """Return the tenant's blacklisted company keys."""
        ...

    def load_seen(self) -> tuple[set[str], set[str]]:
        """Return the persisted ``(url_keys, slug_keys)`` dedup ledger."""
        ...

    def mark_seen(self, url_key: str, slug_key: str) -> None:
        """Persist newly seen normalized keys (either may be empty)."""
        ...

    def add_lead(self, fields: AppFields) -> str:
        """Stage one new lead and return its slug ``name``."""
        ...


def _seed_from_existing(repo: LeadRepo, seen: Seen) -> None:
    """Fold every application already stored for the tenant into the ledger.

    Guarantees we never re-add a role you already have a row for — even one you
    created by hand in the SPA — using the same URL + ``company + role`` keys the
    live listings are checked against. In-memory only (``note``): existing rows are
    not re-persisted to the ``seen`` table."""
    for link, company, role in repo.iter_existing():
        seen.note(link, _norm_slug(company, role))


# -- fetchers: remote job boards --------------------------------------------


def _fmt_salary(lo: object, hi: object) -> str:
    def _int(v: object) -> int:
        try:
            return int(v) if isinstance(v, (int, float, str)) and v not in ("", None) else 0
        except (TypeError, ValueError):
            return 0

    lo_i, hi_i = _int(lo), _int(hi)
    if lo_i and hi_i:
        return f"${lo_i:,}–${hi_i:,}"
    return f"${lo_i:,}" if lo_i else (f"${hi_i:,}" if hi_i else "")


# Remotive's public JSON API is rationed to ~28 jobs; the website itself renders
# 35 fresh jobs per category into a `window.__INITIAL_SEARCH_RESULTS__` blob. We
# scrape page 1 of each relevant category to pull hundreds instead of dozens.
REMOTIVE_CATEGORIES = (
    "software-development",
    "data",
    "devops-sysadmin",
    "product",
    "all-others",
)
_REMOTIVE_BLOB_RE = re.compile(r"__INITIAL_SEARCH_RESULTS__\s*=\s*(\{)")


def _extract_js_object(html: str, start_brace: int) -> str | None:
    """Return the balanced ``{...}`` JSON object beginning at ``start_brace``."""
    depth = 0
    in_str = False
    esc = False
    for i in range(start_brace, len(html)):
        ch = html[i]
        if in_str:
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                in_str = False
        elif ch == '"':
            in_str = True
        elif ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return html[start_brace : i + 1]
    return None


def _remotive_hits(html: str) -> list[dict[str, object]]:
    m = _REMOTIVE_BLOB_RE.search(html)
    if not m:
        return []
    blob = _extract_js_object(html, m.start(1))
    if not blob:
        return []
    try:
        data = json.loads(blob)
    except ValueError:
        return []
    results = data.get("results") if isinstance(data, dict) else None
    envelope = results[0] if isinstance(results, list) and results else results
    hits = envelope.get("hits") if isinstance(envelope, dict) else None
    return [h for h in hits if isinstance(h, dict)] if isinstance(hits, list) else []


def _remotive_location(hit: dict[str, object]) -> str:
    locs = hit.get("locations")
    if isinstance(locs, list):
        names = [str(x.get("name", x)) if isinstance(x, dict) else str(x) for x in locs]
        return ", ".join(n for n in names if n)
    return str(locs) if locs else ""


def _listing_from_remotive_hit(hit: dict[str, object]) -> Listing | None:
    company = str(hit.get("company_name", "")).strip()
    role = str(hit.get("title", "")).strip()
    if not company or not role:
        return None
    skills = hit.get("skills")
    skill_txt = " ".join(str(s) for s in skills) if isinstance(skills, list) else ""
    desc = " ".join(
        str(hit.get(k, "")) for k in ("category", "occupation", "seniority", "job_type")
    )
    return Listing(
        company=company,
        role=role,
        link=str(hit.get("url", "")).strip(),
        location=_remotive_location(hit),
        salary=str(hit.get("salary", "") or "").strip(),
        source="remotive",
        description=f"{skill_txt} {desc}".strip(),
    )


def fetch_remotive(client: httpx.Client, limit: int) -> list[Listing]:
    out: list[Listing] = []
    seen_ids: set[object] = set()
    for cat in REMOTIVE_CATEGORIES:
        if len(out) >= limit:
            break
        try:
            r = client.get(f"https://remotive.com/remote-jobs/{cat}")
            r.raise_for_status()
        except httpx.HTTPError:
            continue
        for hit in _remotive_hits(r.text):
            jid = hit.get("id", hit.get("url"))
            if jid in seen_ids:
                continue
            seen_ids.add(jid)
            listing = _listing_from_remotive_hit(hit)
            if listing:
                out.append(listing)
                if len(out) >= limit:
                    break
    return out


def fetch_remoteok(client: httpx.Client, limit: int) -> list[Listing]:
    # RemoteOK blocks non-browser agents and prefixes the array with a legal
    # notice; the client already sends a browser User-Agent (see _gather).
    r = client.get("https://remoteok.com/api")
    r.raise_for_status()
    rows = [row for row in r.json() if isinstance(row, dict) and row.get("position")]
    out: list[Listing] = []
    for j in rows[:limit]:
        out.append(
            Listing(
                company=str(j.get("company", "")).strip(),
                role=str(j.get("position", "")).strip(),
                link=str(j.get("url", "")).strip(),
                location=str(j.get("location", "") or "Remote").strip(),
                salary=_fmt_salary(j.get("salary_min"), j.get("salary_max")),
                source="remoteok",
                description=_strip_html(str(j.get("description", ""))),
            )
        )
    return out


def fetch_arbeitnow(client: httpx.Client, limit: int) -> list[Listing]:
    r = client.get("https://www.arbeitnow.com/api/job-board-api")
    r.raise_for_status()
    out: list[Listing] = []
    for j in r.json().get("data", [])[:limit]:
        tags = j.get("tags") or []
        types = j.get("job_types") or []
        extra = " ".join(str(t) for t in [*tags, *types])
        loc = str(j.get("location", "") or ("Remote" if j.get("remote") else "")).strip()
        out.append(
            Listing(
                company=str(j.get("company_name", "")).strip(),
                role=str(j.get("title", "")).strip(),
                link=str(j.get("url", "")).strip(),
                location=loc,
                source="arbeitnow",
                description=f"{extra} {_strip_html(str(j.get('description', '')))}".strip(),
            )
        )
    return out


def fetch_jobicy(client: httpx.Client, limit: int) -> list[Listing]:
    r = client.get("https://jobicy.com/api/v2/remote-jobs", params={"count": min(limit, 50)})
    r.raise_for_status()
    out: list[Listing] = []
    for j in r.json().get("jobs", [])[:limit]:
        body = str(j.get("jobExcerpt", "") or j.get("jobDescription", ""))
        out.append(
            Listing(
                company=str(j.get("companyName", "")).strip(),
                role=str(j.get("jobTitle", "")).strip(),
                link=str(j.get("url", "")).strip(),
                location=str(j.get("jobGeo", "") or "Remote").strip(),
                salary=str(j.get("annualSalaryMin", "") or "").strip(),
                source="jobicy",
                description=_strip_html(body),
            )
        )
    return out


# We Work Remotely category RSS feeds. Item titles are "Company: Role".
WWR_FEEDS = (
    "https://weworkremotely.com/categories/remote-programming-jobs.rss",
    "https://weworkremotely.com/categories/remote-devops-sysadmin-jobs.rss",
)


def _rss_text(item: Element, tag: str) -> str:
    el = item.find(tag)
    return (el.text or "").strip() if el is not None and el.text else ""


def fetch_weworkremotely(client: httpx.Client, limit: int) -> list[Listing]:
    out: list[Listing] = []
    for feed in WWR_FEEDS:
        if len(out) >= limit:
            break
        try:
            r = client.get(feed)
            r.raise_for_status()
            root = ET.fromstring(r.content)
        except (httpx.HTTPError, ET.ParseError):
            continue
        for item in root.iter("item"):
            title = _rss_text(item, "title")
            company, _, role = title.partition(":")
            company, role = company.strip(), role.strip()
            if not company or not role:
                continue
            region = _rss_text(item, "region")
            out.append(
                Listing(
                    company=company,
                    role=role,
                    link=_rss_text(item, "link"),
                    location=region or "Remote",
                    source="weworkremotely",
                    description=_strip_html(_rss_text(item, "description")),
                )
            )
            if len(out) >= limit:
                break
    return out


# -- fetcher: Hacker News "Who is hiring?" ----------------------------------


def _latest_whoishiring_id(client: httpx.Client) -> str | None:
    """Find the objectID of the most recent monthly "Who is hiring?" thread."""
    r = client.get(
        "https://hn.algolia.com/api/v1/search",
        params={"tags": "story,author_whoishiring", "query": "who is hiring", "hitsPerPage": 10},
    )
    r.raise_for_status()
    best: tuple[int, str] | None = None
    for hit in r.json().get("hits", []):
        title = str(hit.get("title", "")).lower()
        if "who is hiring" not in title or "wants to be hired" in title:
            continue
        created = int(hit.get("created_at_i", 0))
        oid = str(hit.get("objectID", ""))
        if oid and (best is None or created > best[0]):
            best = (created, oid)
    return best[1] if best else None


def _parse_hn_comment(text_html: str) -> Listing | None:
    """Best-effort parse of one "Who is hiring?" comment into a listing.

    These are free-form, but the convention is a pipe-delimited first line like
    ``Company | Role | Location | REMOTE | ...``. We require at least a company
    and a role from that header; prose-only comments are skipped (the keyword
    filter downstream would mostly drop them anyway).
    """
    if not text_html:
        return None
    first_para = re.split(r"<p>|\n", text_html, maxsplit=1)[0]
    header = _strip_html(first_para)
    parts = [p.strip() for p in header.split("|") if p.strip()]
    if len(parts) < 2:
        return None
    company, role = parts[0], parts[1]
    if not company or not role or len(company) > 80:
        return None
    location = next((p for p in parts[2:] if any(h in p.lower() for h in _REMOTE_HINTS)), "")
    if not location:
        location = parts[2] if len(parts) > 2 else ""
    url_match = _URL_RE.search(text_html)
    return Listing(
        company=company,
        role=role,
        link=url_match.group(0) if url_match else "",
        location=location,
        source="hn_whoishiring",
        description=_strip_html(text_html)[:600],
    )


def fetch_hn_whoishiring(client: httpx.Client, limit: int) -> list[Listing]:
    thread_id = _latest_whoishiring_id(client)
    if not thread_id:
        return []
    r = client.get(f"https://hn.algolia.com/api/v1/items/{thread_id}")
    r.raise_for_status()
    children = r.json().get("children", [])
    out: list[Listing] = []
    for child in children:
        if not isinstance(child, dict) or not child.get("text"):
            continue
        listing = _parse_hn_comment(str(child["text"]))
        if listing:
            out.append(listing)
            if len(out) >= limit:
                break
    return out


# -- fetchers: company ATS boards (Greenhouse / Lever) ----------------------


def fetch_greenhouse(client: httpx.Client, limit: int, slug: str) -> list[Listing]:
    r = client.get(
        f"https://boards-api.greenhouse.io/v1/boards/{slug}/jobs", params={"content": "true"}
    )
    r.raise_for_status()
    company = _ats_label(slug)
    out: list[Listing] = []
    for j in r.json().get("jobs", [])[:limit]:
        loc = j.get("location") or {}
        out.append(
            Listing(
                company=company,
                role=str(j.get("title", "")).strip(),
                link=str(j.get("absolute_url", "")).strip(),
                location=str(loc.get("name", "")).strip() if isinstance(loc, dict) else "",
                source=f"greenhouse:{slug}",
                description=_strip_html(str(j.get("content", ""))),
            )
        )
    return out


def fetch_lever(client: httpx.Client, limit: int, slug: str) -> list[Listing]:
    r = client.get(f"https://api.lever.co/v0/postings/{slug}", params={"mode": "json"})
    r.raise_for_status()
    rows = r.json()
    company = _ats_label(slug)
    out: list[Listing] = []
    for j in rows[:limit] if isinstance(rows, list) else []:
        cats = j.get("categories") or {}
        location = str(cats.get("location", "")).strip() if isinstance(cats, dict) else ""
        team = str(cats.get("team", "")).strip() if isinstance(cats, dict) else ""
        out.append(
            Listing(
                company=company,
                role=str(j.get("text", "")).strip(),
                link=str(j.get("hostedUrl", "")).strip(),
                location=location,
                source=f"lever:{slug}",
                description=f"{team} {_strip_html(str(j.get('descriptionPlain', '')))}".strip(),
            )
        )
    return out


SOURCE_FETCHERS: dict[str, Fetcher] = {
    "remotive": fetch_remotive,
    "remoteok": fetch_remoteok,
    "arbeitnow": fetch_arbeitnow,
    "jobicy": fetch_jobicy,
    "weworkremotely": fetch_weworkremotely,
    "hn_whoishiring": fetch_hn_whoishiring,
}


def make_ats_fetcher(board: AtsBoard) -> Fetcher | None:
    """Bind a company slug to its provider's fetcher, yielding a plain Fetcher."""
    slug = board.slug
    if board.provider == "greenhouse":
        return lambda client, limit: fetch_greenhouse(client, limit, slug)
    if board.provider == "lever":
        return lambda client, limit: fetch_lever(client, limit, slug)
    return None


def build_fetchers(criteria: Criteria) -> list[Fetcher]:
    """The fetchers to run for this config: enabled sources + ATS boards."""
    out: list[Fetcher] = [
        SOURCE_FETCHERS[name]
        for name, on in criteria.sources.items()
        if on and name in SOURCE_FETCHERS
    ]
    for board in criteria.ats_boards:
        fetcher = make_ats_fetcher(board)
        if fetcher is not None:
            out.append(fetcher)
    return out


def _gather(fetchers: Iterable[Fetcher], limit: int) -> list[Listing]:
    """Run each fetcher, tolerating per-source failures (offline, 4xx, parse)."""
    listings: list[Listing] = []
    with httpx.Client(timeout=20.0, follow_redirects=True, headers=BROWSER_HEADERS) as client:
        for fetch in fetchers:
            try:
                listings.extend(fetch(client, limit))
            except (httpx.HTTPError, ValueError, KeyError):
                continue
    return listings


# -- orchestration ----------------------------------------------------------


def run_poll(
    repo: LeadRepo,
    profile: Criteria,
    *,
    limit_per_source: int = 40,
    fetchers: Iterable[Fetcher] | None = None,
    listings: Iterable[Listing] | None = None,
    verify_links: bool | None = None,
) -> list[str]:
    """Fetch this tenant's enabled sources, then score and stage. Returns slug names.

    ``profile`` is the tenant's :class:`~applytrack.criteria.Criteria` (the caller
    loads it via :meth:`applytrack.db.PollRepo.load_profile`); ``repo`` is the
    tenant-scoped data access (see :class:`LeadRepo`). Network is only touched
    when ``listings`` is not supplied; pass a fixed ``listings`` iterable to run
    fully offline (tests, replay).

    When ``verify_links`` is on, each candidate's posting URL is probed as a
    browser before it becomes an entry, and unreachable/dead links are dropped.
    It defaults to on for live runs and off when ``listings`` is supplied.

    The multi-tenant worker (:mod:`applytrack.worker`) bypasses this and calls
    :func:`score_and_stage` directly over a shared, fetched-once listing set.
    """
    if verify_links is None:
        verify_links = listings is None

    if listings is None:
        if fetchers is None:
            fetchers = build_fetchers(profile)
        listings = _gather(fetchers, limit_per_source)

    return score_and_stage(repo, profile, listings, verify_links=verify_links)


def _select_for_profile(
    gathered: dict[str, list[Listing]], profile: Criteria
) -> list[Listing]:
    """Pick the listings for one tenant from a per-source gather, by enabled source.

    Lets the worker fetch each source once and route the shared buckets per tenant,
    so ``score_and_stage`` stays source-agnostic and a tenant only ever sees roles
    from the sources/boards it actually enabled."""
    out: list[Listing] = []
    for name, on in profile.sources.items():
        if on:
            out.extend(gathered.get(name, []))
    for board in profile.ats_boards:
        out.extend(gathered.get(f"{board.provider}:{board.slug}", []))
    return out


def score_and_stage(
    repo: LeadRepo,
    profile: Criteria,
    listings: Iterable[Listing],
    *,
    verify_links: bool = False,
) -> list[str]:
    """Dedupe, filter, score, and stage ``listings`` for one tenant. Returns slug names.

    The dedup ledger is loaded from the ``seen`` table and seeded from the tenant's
    existing applications; each newly seen key is persisted so a company is never
    re-pinged on a later run. Network is touched only when ``verify_links`` is on.
    """
    url_keys, slug_keys = repo.load_seen()
    seen = Seen(url_keys, slug_keys, sink=repo.mark_seen)
    _seed_from_existing(repo, seen)
    blacklist = Blacklist({_norm_company(c) for c in repo.blacklist_companies()})

    verify_client = (
        httpx.Client(timeout=12.0, follow_redirects=True, headers=BROWSER_HEADERS)
        if verify_links
        else None
    )
    added: list[str] = []
    try:
        for item in listings:
            if not item.company or not item.role:
                continue
            # Blacklisted companies are dropped wholesale — never seen, never staged.
            if blacklist.has(item.company):
                continue
            slug = _norm_slug(item.company, item.role)
            if seen.has(item.link, slug):
                continue
            # Record keys up front so a later failure still can't cause a re-ping.
            seen.add(item.link, slug)

            if not _passes_location(item, profile):
                continue

            score, hits = classify(item.role, item.description, profile.keywords)
            if not hits or score < profile.min_fit_score:
                continue

            # Block dead postings: don't create an entry we can't actually open.
            if (
                verify_client is not None
                and item.link
                and not is_reachable(item.link, client=verify_client)
            ):
                continue

            fields = _to_fields(item, profile.default_lane, score, hits)
            try:
                name = repo.add_lead(fields)
            except psycopg.errors.UniqueViolation:
                logger.debug(
                    "skipping duplicate lead for %s: %s",
                    item.company,
                    item.role,
                    exc_info=True,
                )
                continue
            # Leads stage as-is; the cover letter is drafted on demand later.
            added.append(name)
    finally:
        if verify_client is not None:
            verify_client.close()

    return added


def _to_fields(item: Listing, lane: str, score: int, hits: list[str]) -> AppFields:
    snippet = item.description[:280].rstrip()
    matched = ", ".join(hits[:6])
    notes = (
        f"**Auto-discovered** via {item.source}. "
        f"Matched keywords: {matched}.\n\n{snippet}"
    ).strip()
    return AppFields(
        company=item.company,
        role=item.role,
        lane=lane,
        status="lead",
        link=item.link,
        location=item.location,
        salary=item.salary,
        source=f"auto:{item.source}",
        score=str(min(100, max(0, score))),
        notes=notes,
    )
