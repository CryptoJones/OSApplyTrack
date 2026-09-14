# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""MyGreenhouse as a source (#218): the sign-in by emailed code, the Inertia search,
the mapping onto listings, the per-tenant gather — all offline."""

from __future__ import annotations

import json
from datetime import datetime, timedelta, timezone
from email.message import EmailMessage
from email.utils import format_datetime

import httpx

from applytrack import mygreenhouse, secrets
from applytrack.criteria import Criteria
from applytrack.mygreenhouse import Portal, PortalAccount, code_in_message, to_listing
from applytrack.poll import Listing
from applytrack.worker import run_all_tenants

SIGN_IN_HTML = (
    '<html><head><meta name="csrf-token" content="tok-1" /></head>'
    '<body><div id="app" data-page="{&quot;component&quot;:&quot;users/sign_in&quot;,'
    '&quot;version&quot;:&quot;v-abc&quot;,&quot;props&quot;:{}}"></div></body></html>'
)

POST = {
    "id": 6128122004, "title": "Documentation Engineer", "companyName": "Arize AI",
    "publicUrl": "https://job-boards.greenhouse.io/arizeai/jobs/6128122004", "urlToken": "arizeai",
    "locations": ["San Francisco, CA"], "workType": "remote", "payRanges": None,
}


def _portal_handler(*, session_ok: str = "sess-1", posts: list[dict] | None = None):
    """A fake my.greenhouse.io: sign-in page, the two sign-in posts, the Inertia search."""
    log: list[str] = []
    state = {"code": "zqp5wtzv"}

    def handler(req: httpx.Request) -> httpx.Response:
        log.append(f"{req.method} {req.url.path}?{req.url.query.decode()}")
        if req.method == "GET" and req.url.path == "/users/sign_in":
            return httpx.Response(200, text=SIGN_IN_HTML)
        if req.method == "POST" and req.url.path == "/users/sign_in":
            assert req.headers["x-inertia"] == "true"
            assert req.headers["x-csrf-token"] == "tok-1"
            assert json.loads(req.content)["email"] == "ada@example.com"
            return httpx.Response(302, headers={"location": "/users/sign_in"})
        if req.method == "POST" and req.url.path == "/users/submit_code":
            body = json.loads(req.content)
            if body["code"] != state["code"]:
                return httpx.Response(302, headers={"location": "/users/sign_in"})
            return httpx.Response(302, headers={
                "location": "/", "set-cookie": f"_session_id={session_ok}; Path=/; HttpOnly"})
        if req.method == "GET" and req.url.path == "/jobs/search":
            if req.headers.get("x-inertia") != "true":
                return httpx.Response(200, text=SIGN_IN_HTML)  # the page renders for anyone
            cookie = req.headers.get("cookie", "")
            if f"_session_id={session_ok}" not in cookie:
                return httpx.Response(302, headers={"location": "/users/sign_in"})
            if req.headers.get("x-inertia-version") != "v-abc":
                return httpx.Response(409, headers={"x-inertia-location": "/jobs/search"})
            page = int(req.url.params.get("page", "1"))
            q = req.url.params.get("query", "")
            data = posts if posts is not None else [POST]
            return httpx.Response(200, json={"component": "jobs", "props": {
                "jobPosts": data if page == 1 else [],
                "moreResultsAvailable": page == 1 and bool(q) is False, "page": page}})
        return httpx.Response(404)

    return handler, log


def test_sign_in_asks_for_the_code_takes_it_from_the_mailbox_and_keeps_the_session() -> None:
    handler, log = _portal_handler()
    client = httpx.Client(transport=httpx.MockTransport(handler), base_url="https://my.greenhouse.io")
    portal = Portal(client, PortalAccount(email="ada@example.com"))
    asked_at: list[float] = []

    def code_source(sent: float) -> str | None:
        asked_at.append(sent)
        return "zqp5wtzv"

    session = portal.sign_in(code_source)
    assert session == "sess-1"
    assert asked_at and asked_at[0] > 0
    assert [entry.split("?")[0] for entry in log] == [
        "GET /users/sign_in", "POST /users/sign_in", "GET /users/sign_in",
        "POST /users/submit_code",
    ]


def test_a_refused_code_is_a_sign_in_failure_not_a_session() -> None:
    handler, _ = _portal_handler()
    client = httpx.Client(transport=httpx.MockTransport(handler), base_url="https://my.greenhouse.io")
    portal = Portal(client, PortalAccount(email="ada@example.com"))
    try:
        portal.sign_in(lambda _: "wrong")
    except mygreenhouse.NeedsSignIn as exc:
        assert "refused the security code" in str(exc)
    else:  # pragma: no cover
        raise AssertionError("expected NeedsSignIn")


def test_search_sends_the_inertia_headers_recovers_from_a_new_build_and_maps_posts() -> None:
    handler, log = _portal_handler()
    client = httpx.Client(transport=httpx.MockTransport(handler), base_url="https://my.greenhouse.io")
    portal = Portal(client, PortalAccount(email="ada@example.com", session="sess-1"))
    portal._version = "stale"  # a build the portal no longer serves: 409, then retry
    posts, more = portal.search("ai engineer", page=1, remote_only=True)
    assert [p["id"] for p in posts] == [6128122004]
    assert more is False  # a keyword search has no second page in this fake
    searches = [entry for entry in log if entry.startswith("GET /jobs/search?query=")]
    assert len(searches) == 2  # the stale-version try, then the retry
    assert "work_type%5B%5D=remote" in searches[-1] or "work_type[]=remote" in searches[-1]


def test_a_bounced_session_raises_needs_sign_in() -> None:
    handler, _ = _portal_handler()
    client = httpx.Client(transport=httpx.MockTransport(handler), base_url="https://my.greenhouse.io")
    portal = Portal(client, PortalAccount(email="ada@example.com", session="old-and-void"))
    try:
        portal.search("", page=1)
    except mygreenhouse.NeedsSignIn:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected NeedsSignIn")


def test_a_post_maps_onto_a_listing_that_links_to_the_employers_board() -> None:
    listing = to_listing(POST)
    assert listing == Listing(
        company="Arize AI", role="Documentation Engineer",
        link="https://job-boards.greenhouse.io/arizeai/jobs/6128122004",
        location="San Francisco, CA (Remote)", salary="", source="mygreenhouse",
    )
    assert to_listing({"title": "x"}) is None
    built = to_listing(
        {"id": 1, "urlToken": "acme", "title": "Eng", "companyName": "Acme", "locations": []})
    assert built is not None and built.link == "https://job-boards.greenhouse.io/acme/jobs/1"


def test_the_code_is_read_only_from_greenhouses_mail_sent_after_the_request() -> None:
    def mail(when: datetime, body: str) -> bytes:
        m = EmailMessage()
        m["From"] = "Greenhouse <no-reply@greenhouse.io>"
        m["Subject"] = "Here's your MyGreenhouse security code"
        m["Date"] = format_datetime(when)
        m.set_content(body)
        return bytes(m)

    now = datetime.now(timezone.utc)
    text = (
        "Hi Aaron,\n\nYour security code is:\n\n******** szo8ve06 ********\n\n"
        "Enter this code on the sign-in page."
    )
    assert code_in_message(mail(now, text), since=now.timestamp() - 5) == "szo8ve06"
    # An older code — the one a previous send issued — is never the answer.
    assert code_in_message(mail(now - timedelta(minutes=9), text), since=now.timestamp()) is None
    assert code_in_message(mail(now, "Thanks for applying!"), since=now.timestamp() - 5) is None


def test_seal_round_trips_in_the_apis_format() -> None:
    token = secrets.seal("hunter2", "master-key")
    assert token.startswith("enc:v1:") and token.count(":") == 3
    assert secrets.unseal(token, "master-key") == "hunter2"
    try:
        secrets.unseal(token, "other-key")
    except secrets.SealError:
        pass
    else:  # pragma: no cover
        raise AssertionError("a foreign key must not open the token")


class _Repo:
    """An in-memory tenant with a MyGreenhouse account and a mailbox."""

    def __init__(self, tenant_id: int, session: str = "") -> None:
        self.tenant_id = tenant_id
        self.saved: list[tuple[str, object]] = []
        self.staged: list[str] = []
        self._session = session

    def load_profile(self) -> Criteria:
        return Criteria(
            keywords=["documentation engineer"], sources={"mygreenhouse": True}, remote_only=True
        )

    def portal_account(self) -> PortalAccount | None:
        return PortalAccount(email="ada@example.com", session=self._session)

    def save_portal_session(self, session: str, expires_at: object) -> None:
        self.saved.append((session, expires_at))

    def mailbox(self) -> mygreenhouse.Mailbox:
        return mygreenhouse.Mailbox("imap.example.com", 993, "ada@example.com", "app-pw")

    # LeadRepo
    def iter_existing(self):
        return iter(())

    def blacklist_companies(self) -> list[str]:
        return []

    def load_seen(self) -> tuple[set[str], set[str]]:
        return set(), set()

    def mark_seen(self, url_key: str, slug_key: str) -> None:
        pass

    def add_lead(self, fields) -> str:  # type: ignore[no-untyped-def]
        self.staged.append(fields.role)
        return f"{fields.company}.md"


def test_the_worker_gathers_the_portal_per_tenant_and_signs_in_without_a_session(
    monkeypatch,  # type: ignore[no-untyped-def]
) -> None:
    handler, log = _portal_handler()
    monkeypatch.setattr(mygreenhouse, "read_code", lambda mailbox, sent: "zqp5wtzv")
    real_client = httpx.Client

    def fake_client(**kw):  # type: ignore[no-untyped-def]
        kw.pop("transport", None)
        return real_client(transport=httpx.MockTransport(handler), **kw)

    monkeypatch.setattr("applytrack.worker.httpx.Client", fake_client)
    repo = _Repo(7)
    results = run_all_tenants(
        tenant_ids=[7], repo_for=lambda _: repo, gathered={}, verify_links=False
    )
    assert repo.saved and repo.saved[0][0] == "sess-1"
    assert repo.staged == ["Documentation Engineer"]
    assert results[7] == ["Arize AI.md"]
    assert any(entry.startswith("POST /users/submit_code") for entry in log)
