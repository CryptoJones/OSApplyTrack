# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Pull live listings through the poller's own fetchers and cache them (#300).

    python tools/jev_eval/gather.py --limit 100 --out raw.json

Every built-in public source is fetched once; the result is written as JSON so
that the comparison (``compare.py``) and both classifiers read byte-identical
input. ``corpus.json`` is a frozen selection from one such pull (2026-09-22; see
RESULTS.md for how it was chosen) -- a fresh pull is a new corpus that needs its own
labels, not a drop-in replacement. Contact details are scrubbed from descriptions:
postings are public, but the corpus has no business carrying anyone's email address or
phone number.
"""

from __future__ import annotations

import argparse
import json
import re
from dataclasses import asdict

from applytrack.poll import SOURCE_FETCHERS, _gather

_EMAIL_RE = re.compile(r"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")
_PHONE_RE = re.compile(r"(?<!\d)(?:\+?\d[\s().-]?){9,14}\d(?!\d)")


def scrub(text: str) -> str:
    return _PHONE_RE.sub("[phone]", _EMAIL_RE.sub("[email]", text or ""))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=80)
    ap.add_argument("--out", default="raw.json")
    args = ap.parse_args()
    out = []
    for name, fetch in SOURCE_FETCHERS.items():
        got = _gather([fetch], args.limit)
        print(f"{name}: {len(got)}")
        for item in got:
            row = asdict(item)
            row["description"] = scrub(row["description"])
            out.append(row)
    with open(args.out, "w", encoding="utf-8") as fh:
        json.dump(out, fh, ensure_ascii=False, indent=1)
    print(f"wrote {len(out)} listings to {args.out}")


if __name__ == "__main__":
    main()
