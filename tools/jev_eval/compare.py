# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Keyword ``classify()`` vs Jev (TypeSafe System One) on a cached corpus (#300).

    python tools/jev_eval/gather.py --out raw.json      # a fresh pull (see gather.py)
    TYPESAFE_API_KEY=... python tools/jev_eval/compare.py  # run both, write results
    python tools/jev_eval/compare.py --offline            # re-score from cached answers

Both classifiers read the same ``corpus.json`` -- the same bytes -- and are
scored against the hand labels in ``labels.json``. Jev is asked the two question
shapes the issue names: a ``noul`` (calibrated yes/no) and a ``score`` (an ordered
rubric, which maps onto ``min_fit_score``). Every Jev answer is cached in
``jev_answers.json`` so the numbers can be recomputed without spending tokens.

The key is read from ``TYPESAFE_API_KEY`` and never written anywhere.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import httpx

from applytrack.poll import classify

HERE = Path(__file__).resolve().parent
URL = os.environ.get("TYPESAFE_BASE_URL", "https://api.typesafe.ai") + "/v1/systemone"
MODEL = os.environ.get("JEV_MODEL", "jev-latest")

FIT_INSTRUCTIONS = (
    "`profile.keywords` lists the kinds of work a job seeker does. Is `posting` a job whose "
    "actual day-to-day work is one of those kinds of work? Judge the role itself -- what the "
    "hired person will spend their time doing. A posting that only mentions a keyword in the "
    "company blurb, the product description, the benefits, or a list of tools the team happens "
    "to use is NOT a match: a salesperson, recruiter, product manager or support agent at an AI "
    "or data company is not doing AI or data work."
)
NOUL = {
    "type": "noul",
    "instructions": FIT_INSTRUCTIONS,
    "criteria": {
        "true": "The role's own work is one of the kinds of work in `profile.keywords`",
        "false": "A different kind of job, even if it shares words with `profile.keywords`",
    },
}
SCORE = {
    "type": "score",
    "instructions": FIT_INSTRUCTIONS,
    "criteria": [
        "Unrelated: the role's work is a different kind of job (sales, support, HR, design, "
        "management, operations...), whatever words it shares with the keywords",
        "Adjacent: a technical role, but its core work is outside the keywords "
        "(e.g. frontend, mobile, DevOps, security, a niche platform)",
        "Partial: the role's work overlaps one of the keyword areas but is not centred on it",
        "Match: the role's day-to-day work is squarely one of the kinds of work in the keywords",
    ],
}
SHAPES = {"noul": NOUL, "score": SCORE}
NOUL_THRESHOLD = 0.5


def load() -> tuple[dict, list[dict], dict[str, dict]]:
    corpus = json.loads((HERE / "corpus.json").read_text(encoding="utf-8"))
    labels = json.loads((HERE / "labels.json").read_text(encoding="utf-8"))
    return corpus["profile"], corpus["listings"], labels


def ask(client: httpx.Client, key: str, profile: dict, item: dict, shape: str) -> dict:
    body = {
        "model": MODEL,
        "state": {
            "profile": {"keywords": profile["keywords"]},
            "posting": {
                "title": item["role"],
                "company": item["company"],
                "location": item["location"],
                "description": item["description"],
            },
        },
        "questions": {"fit": SHAPES[shape]},
    }
    for attempt in range(4):
        started = time.monotonic()
        res = client.post(URL, json=body, headers={"Authorization": f"Bearer {key}"})
        if res.status_code in (408, 429) or res.status_code >= 500:
            time.sleep(2**attempt)
            continue
        res.raise_for_status()
        out = res.json()
        out["answers"]["fit"].pop("legend", None)  # our own rubric echoed back
        out["latency_s"] = round(time.monotonic() - started, 3)
        return out
    res.raise_for_status()
    raise RuntimeError("unreachable")


def run_jev(profile: dict, listings: list[dict], cache_path: Path) -> dict:
    key = os.environ.get("TYPESAFE_API_KEY", "")
    if not key:
        sys.exit("TYPESAFE_API_KEY is not set (or pass --offline to reuse jev_answers.json)")
    cache = json.loads(cache_path.read_text()) if cache_path.exists() else {}
    todo = [(it, s) for it in listings for s in SHAPES if f"{it['id']}:{s}" not in cache]
    print(f"{len(todo)} Jev calls to make")
    with httpx.Client(timeout=60.0) as client, ThreadPoolExecutor(max_workers=6) as pool:
        futures = {pool.submit(ask, client, key, profile, it, s): (it, s) for it, s in todo}
        for n, fut in enumerate(futures, 1):
            it, s = futures[fut]
            try:
                cache[f"{it['id']}:{s}"] = fut.result()
            except Exception as exc:  # noqa: BLE001 - record and carry on
                print(f"  {it['id']}:{s} failed: {exc}")
            if n % 50 == 0:
                cache_path.write_text(json.dumps(cache, indent=1, sort_keys=True))
                print(f"  {n}/{len(todo)}")
    cache_path.write_text(json.dumps(cache, indent=1, sort_keys=True))
    return cache


def mcnemar_exact(b: int, c: int) -> float:
    """Two-sided exact McNemar p: binomial test of min(b, c) against n = b + c, p = 0.5."""
    n = b + c
    if n == 0:
        return 1.0
    k = min(b, c)
    tail = sum(math.comb(n, i) for i in range(k + 1)) / 2**n
    return min(1.0, 2 * tail)


def decide(profile: dict, listings: list[dict], cache: dict) -> dict[str, dict[str, object]]:
    rows = {}
    for it in listings:
        score, hits = classify(it["role"], it["description"], profile["keywords"])
        noul = cache.get(f"{it['id']}:noul", {}).get("answers", {}).get("fit", {})
        sc = cache.get(f"{it['id']}:score", {}).get("answers", {}).get("fit", {})
        top = len(SCORE["criteria"]) - 1
        rows[it["id"]] = {
            "baseline_score": score,
            "baseline": bool(hits) and score >= profile["min_fit_score"],
            "hits": hits,
            "noul_p": noul.get("noul"),
            "noul": None if "noul" not in noul else noul["noul"] >= NOUL_THRESHOLD,
            # The rubric's expected score, rescaled to the 0-100 fit-score range, is held to
            # the same min_fit_score the keyword score is: the drop-in the issue describes.
            "score_fit": None if "score" not in sc else round(100 * sc["score"] / top),
            "score": None
            if "score" not in sc
            else 100 * sc["score"] / top >= profile["min_fit_score"],
        }
    return rows


def tokens(cache: dict, shape: str) -> tuple[int, int, int]:
    ins = outs = n = 0
    for k, v in cache.items():
        if k.endswith(f":{shape}"):
            ins += v.get("usage", {}).get("input_tokens", 0)
            outs += v.get("usage", {}).get("output_tokens", 0)
            n += 1
    return ins, outs, n


def report(listings: list[dict], labels: dict, rows: dict, cache: dict) -> str:
    lines = []
    n = len(listings)
    pos = sum(1 for it in listings if labels[it["id"]]["fit"])
    lines.append(f"corpus: {n} listings, {pos} labelled fit, {n - pos} labelled not fit\n")
    lines.append("| classifier | correct | accuracy | TP | FP | FN | TN | precision | recall |")
    lines.append("|---|---|---|---|---|---|---|---|---|")
    for name in ("baseline", "noul", "score"):
        tp = fp = fn = tn = 0
        for it in listings:
            got, want = rows[it["id"]][name], labels[it["id"]]["fit"]
            if got is None:
                got = False
            tp += got and want
            fp += got and not want
            fn += (not got) and want
            tn += (not got) and not want
        prec = tp / (tp + fp) if tp + fp else float("nan")
        rec = tp / (tp + fn) if tp + fn else float("nan")
        lines.append(
            f"| {name} | {tp + tn}/{n} | {100 * (tp + tn) / n:.1f}% | {tp} | {fp} | {fn} | {tn} "
            f"| {prec:.2f} | {rec:.2f} |"
        )
    lines.append("")
    for name in ("noul", "score"):
        b = c = 0
        for it in listings:
            want = labels[it["id"]]["fit"]
            base_ok = rows[it["id"]]["baseline"] == want
            jev_ok = bool(rows[it["id"]][name]) == want
            b += jev_ok and not base_ok
            c += base_ok and not jev_ok
        lines.append(
            f"- **{name} vs baseline** — discordant pairs: Jev right {b}, baseline right {c}; "
            f"McNemar exact p = {mcnemar_exact(b, c):.2g}"
        )
    lines.append("")
    for name in ("noul", "score"):
        ins, outs, k = tokens(cache, name)
        if k:
            lines.append(
                f"- **{name} tokens** — {ins + outs:,} over {k} calls: "
                f"{(ins + outs) / k:,.0f} per posting ({ins / k:,.0f} in, {outs / k:,.1f} out)"
            )
    lat = sorted(v.get("latency_s", 0) for v in cache.values())
    if lat:
        lines.append(f"- **latency** — median {lat[len(lat) // 2]:.2f}s per call")

    # The listings with no keyword hit were hand-picked (likely misses plus bait), so the
    # stratum the baseline itself selected -- every listing with at least one hit -- is
    # reported on its own as the unbiased comparison.
    hit = [it for it in listings if rows[it["id"]]["hits"]]
    lines.append(f"\n**Keyword-hit stratum only** ({len(hit)} listings with at least one hit):\n")
    for name in ("baseline", "noul", "score"):
        ok = sum(bool(rows[it["id"]][name]) == labels[it["id"]]["fit"] for it in hit)
        lines.append(f"- {name}: {ok}/{len(hit)} ({100 * ok / len(hit):.1f}%)")
    for name in ("noul", "score"):
        b = sum(
            bool(rows[it["id"]][name]) == labels[it["id"]]["fit"] != rows[it["id"]]["baseline"]
            for it in hit
        )
        c = sum(
            rows[it["id"]]["baseline"] == labels[it["id"]]["fit"] != bool(rows[it["id"]][name])
            for it in hit
        )
        lines.append(f"- {name} vs baseline: Jev right {b}, baseline right {c}; "
                     f"p = {mcnemar_exact(b, c):.2g}")

    lines.append("\n**Threshold sweeps** (correct out of all listings):\n")
    def base_ok(t: int) -> int:
        return sum(
            (bool(r["hits"]) and r["baseline_score"] >= t) == labels[i]["fit"]
            for i, r in rows.items()
        )

    sweep = ", ".join(f"{t}: {base_ok(t)}" for t in range(40, 75, 5))
    lines.append(f"- baseline min_fit_score — {sweep}")
    sweep = ", ".join(
        f"{t}: {sum(((r['noul_p'] or 0) >= t) == labels[i]['fit'] for i, r in rows.items())}"
        for t in (0.3, 0.4, 0.5, 0.6, 0.7)
    )
    lines.append(f"- noul probability — {sweep}")
    sweep = ", ".join(
        f"{t}: {sum(((r['score_fit'] or 0) >= t) == labels[i]['fit'] for i, r in rows.items())}"
        for t in range(40, 80, 5)
    )
    lines.append(f"- score (0-100) — {sweep}")
    return "\n".join(lines)


def repeat_check(profile: dict, listings: list[dict], cache: dict, n: int) -> None:
    """Re-ask the first ``n`` listings and compare with the cached answers (determinism)."""
    key = os.environ.get("TYPESAFE_API_KEY", "")
    if not key:
        sys.exit("TYPESAFE_API_KEY is not set")
    dn, ds, flips = [], [], 0
    with httpx.Client(timeout=60.0) as client:
        for it in listings[:n]:
            a = ask(client, key, profile, it, "noul")["answers"]["fit"]["noul"]
            s = ask(client, key, profile, it, "score")["answers"]["fit"]["score"]
            a0 = cache[f"{it['id']}:noul"]["answers"]["fit"]["noul"]
            s0 = cache[f"{it['id']}:score"]["answers"]["fit"]["score"]
            dn.append(abs(a - a0))
            ds.append(abs(s - s0))
            flips += (a >= NOUL_THRESHOLD) != (a0 >= NOUL_THRESHOLD)
    print(f"repeat of {n} listings: noul max |Δp| {max(dn):.3f}, mean {sum(dn) / n:.4f}; "
          f"score max |Δ| {max(ds):.3f}; decision flips {flips}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--offline", action="store_true", help="reuse jev_answers.json only")
    ap.add_argument("--limit", type=int, default=0, help="only the first N listings")
    ap.add_argument("--repeat", type=int, default=0, help="re-ask N listings, report drift")
    args = ap.parse_args()
    profile, listings, labels = load()
    if args.limit:
        listings = listings[: args.limit]
    cache_path = HERE / "jev_answers.json"
    if args.repeat:
        repeat_check(profile, listings, json.loads(cache_path.read_text()), args.repeat)
        return
    if args.offline:
        cache = json.loads(cache_path.read_text()) if cache_path.exists() else {}
    else:
        cache = run_jev(profile, listings, cache_path)
    rows = decide(profile, listings, cache)
    (HERE / "decisions.json").write_text(json.dumps(rows, indent=1, sort_keys=True))
    print(report(listings, labels, rows, cache))


if __name__ == "__main__":
    main()
