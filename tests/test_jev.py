# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Jev's opt-in semantic fit (#300): off without a key or an opt-in, keyword scoring as
the fallback on any failure, and the keyword guards (#81 / #85 / #86) intact beneath it."""

from __future__ import annotations

import json

import httpx
import pytest

from applytrack import poll
from applytrack.criteria import Criteria
from applytrack.poll import JevJudge, Listing, classify, jev_judge_for, score_and_stage
from tests.test_poll import FakeRepo

KEYWORDS = [".net", "asp.net", "backend engineer", "rag", "ml engineer", "go"]


class JevRepo(FakeRepo):
    def __init__(self, *, opted_in: bool = True, **kw: object) -> None:
        super().__init__(profile=Criteria(keywords=KEYWORDS, min_fit_score=55), **kw)  # type: ignore[arg-type]
        self._opted_in = opted_in

    def jev_classify_enabled(self) -> bool:
        return self._opted_in


def _judge(answer: object, seen: list[dict] | None = None, status: int = 200) -> JevJudge:
    """A judge on a mock transport. ``answer`` maps a title to Jev's probability."""

    def handler(request: httpx.Request) -> httpx.Response:
        body = json.loads(request.content)
        if seen is not None:
            seen.append({"url": str(request.url), "auth": request.headers["authorization"],
                         "body": body})
        if status != 200:
            return httpx.Response(status, json={"error": "nope"})
        p = answer(body["state"]["posting"]["title"]) if callable(answer) else answer
        return httpx.Response(200, json={"answers": {"fit": {"type": "noul", "noul": p}},
                                         "usage": {"input_tokens": 1, "output_tokens": 1}})

    client = httpx.Client(transport=httpx.MockTransport(handler))
    return JevJudge("test-key", client=client)


# An account executive whose only "match" is a benefits blurb and a storage product --
# the #81 case -- and a real ASP.NET role, the #85 case.
AE = Listing(company="Acme", role="Senior Account Executive", link="https://acme.example/ae",
             description="Leveraging our storage coverage, we encourage every average rep.")
ASP = Listing(company="Initech", role="Senior ASP.NET Backend Engineer",
              link="https://initech.example/asp", description="Build ASP.NET Core services.")
DATA = Listing(company="Globex", role="Data Platform Engineer", link="https://globex.example/d",
               description="Own the pipelines behind our warehouse.")


def test_no_key_means_no_judge_even_when_opted_in(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TYPESAFE_API_KEY", raising=False)
    assert jev_judge_for(JevRepo()) is None


def test_a_key_without_the_tenant_opting_in_means_no_judge(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TYPESAFE_API_KEY", "k")
    assert jev_judge_for(JevRepo(opted_in=False)) is None
    assert jev_judge_for(FakeRepo()) is None  # a repo that cannot even say


def test_key_and_opt_in_make_a_judge(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TYPESAFE_API_KEY", "k")
    judge = jev_judge_for(JevRepo())
    assert isinstance(judge, JevJudge)
    judge.close()


def test_without_jev_the_keyword_score_decides_exactly_as_before(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("TYPESAFE_API_KEY", raising=False)
    repo = JevRepo()
    score_and_stage(repo, repo.load_profile(), [AE, ASP, DATA])
    assert [f.company for f in repo.added] == ["Initech"]
    assert "Matched keywords: .net, asp.net" in repo.added[0].notes


def test_jev_fit_replaces_the_keyword_score_both_ways(monkeypatch: pytest.MonkeyPatch) -> None:
    fits = {"Senior Account Executive": 0.04, "Senior ASP.NET Backend Engineer": 0.93,
            "Data Platform Engineer": 0.81}
    monkeypatch.setattr(poll, "jev_judge_for", lambda repo: _judge(fits.get))
    repo = JevRepo()
    score_and_stage(repo, repo.load_profile(), [AE, ASP, DATA])
    # No keyword names "Data Platform Engineer"; Jev's yes stages it all the same.
    assert sorted(f.company for f in repo.added) == ["Globex", "Initech"]
    by = {f.company: f for f in repo.added}
    assert by["Globex"].score == "81"
    assert "Fit 81/100 by Jev." in by["Globex"].notes
    assert "Fit 93/100 by Jev. Matched keywords: .net, asp.net" in by["Initech"].notes


def test_jev_no_rejects_a_keyword_match(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(poll, "jev_judge_for", lambda repo: _judge(0.2))
    repo = JevRepo()
    score_and_stage(repo, repo.load_profile(), [ASP])
    assert repo.added == []


def test_the_request_is_the_documented_rest_call() -> None:
    seen: list[dict] = []
    judge = _judge(0.7, seen)
    long = Listing(company="C", role="R", description="x" * 10_000)
    assert judge.fit(long, ["rag", ""]) == 70
    (req,) = seen
    assert req["url"] == "https://api.typesafe.ai/v1/systemone"
    assert req["auth"] == "Bearer test-key"
    assert req["body"]["model"] == "jev-latest"
    assert req["body"]["questions"]["fit"]["type"] == "noul"
    assert req["body"]["state"]["profile"] == {"keywords": ["rag"]}
    assert len(req["body"]["state"]["posting"]["description"]) == 6000


@pytest.mark.parametrize("status", [401, 429, 500])
def test_an_error_falls_back_to_keywords_for_the_rest_of_the_run(
    monkeypatch: pytest.MonkeyPatch, status: int
) -> None:
    seen: list[dict] = []
    monkeypatch.setattr(poll, "jev_judge_for", lambda repo: _judge(0.99, seen, status=status))
    repo = JevRepo()
    score_and_stage(repo, repo.load_profile(), [AE, ASP, DATA])
    assert [f.company for f in repo.added] == ["Initech"]  # the keyword verdicts
    assert len(seen) == 1  # one failure ends Jev for the pass: no timeout per listing
    assert "Jev" not in repo.added[0].notes


@pytest.mark.parametrize("answer", [None, "yes", 1.7, -0.1])
def test_a_malformed_answer_falls_back_to_keywords(answer: object) -> None:
    judge = _judge(answer)
    assert judge.fit(ASP, KEYWORDS) is None
    assert judge.broken


def test_an_unreachable_service_falls_back_to_keywords(monkeypatch: pytest.MonkeyPatch) -> None:
    def refuse(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("refused", request=request)

    judge = JevJudge("k", client=httpx.Client(transport=httpx.MockTransport(refuse)))
    monkeypatch.setattr(poll, "jev_judge_for", lambda repo: judge)
    repo = JevRepo()
    score_and_stage(repo, repo.load_profile(), [AE, ASP])
    assert [f.company for f in repo.added] == ["Initech"]


# -- named regression guards for the fallback: the keyword matcher Jev falls back to ---


def test_guard_81_rag_does_not_fire_inside_storage_leveraging_coverage() -> None:
    assert classify(AE.role, AE.description, ["rag"]) == (0, [])


@pytest.mark.parametrize("title", ["Senior ASP.NET Core Developer", "VB.NET Developer",
                                   "ADO.NET Engineer"])
def test_guard_85_the_framework_prefix_before_dot_net_still_matches(title: str) -> None:
    assert classify(title, "", [".net"])[1] == [".net"]


def test_guard_86_suffixes_only_for_long_keywords() -> None:
    assert classify("Backend Engineers", "", ["backend engineer"])[1] == ["backend engineer"]
    assert classify("x", "the debate rages on", ["rag"]) == (0, [])
    assert classify("x", "we are going places", ["go"]) == (0, [])
    assert classify("x", "mled and mls", ["ml"]) == (0, [])
