# Jev vs keyword `classify()`: results (#300)

**Summary:** Jev wins clearly. It got 226 of 257 hand-labelled live listings right. The keyword score, at its production floor of 55, got 184. Jev was right on 60 listings the keyword score got wrong, and wrong on 18 it got right (McNemar exact p = 2e-6). That is about 1,360 input tokens per posting. At TypeSafe's published $0.042 per million input tokens, a posting costs **$0.000057**, or about 6¢ per thousand new listings. Jev is now an opt-in option for each tenant. Keyword `classify()` stays the default, and the poller falls back to it on any error.

## Method

- **Corpus** (`corpus.json`): 712 live listings were pulled on 2026-09-22 by the poller's own fetchers (`gather.py`) from remotive, remoteok, arbeitnow, jobicy, weworkremotely, remotefirstjobs and workanywhere. They were de-duplicated by `company + role`. Hacker News was left out: its parser puts the location where the role should be. The corpus keeps **every listing with at least one keyword hit (203)**, plus **54 with no hit**. The 54 were picked by hand, some as likely misses ("Data Engineer", "Backend Software Engineer") and some as bait ("Go Live Engineer", "Sales Engineer, Storage & Analysis", account executives). Descriptions are capped at 6,000 characters, and email addresses and phone numbers are scrubbed. Both classifiers read the same bytes.
- **Profile:** the production owner's `search_profiles` keywords (48 of them: .NET/backend, DevRel/docs, AI/ML, data) and `min_fit_score` 55, read with a read-only SELECT.
- **Labels** (`labels.json`): each listing was judged by hand, each with a one-line reason, on one question: is this posting's own work the kind of work the keywords describe? Four families count as yes:
  - backend or .NET engineering
  - developer relations or technical writing
  - AI/ML engineering
  - data analysis, data science or data engineering

  Sales, support, product management, design, people management, security, SRE/DevOps, frontend, mobile and full-stack roles count as no. Result: 109 fit, 148 not fit.
- **Baseline:** `classify()` as shipped. A listing is a fit when it has at least one hit and `score >= min_fit_score`.
- **Jev:** `POST https://api.typesafe.ai/v1/systemone`, model `jev-latest` (it answered as `jev-1.13.0`). The request state is `{profile: {keywords}, posting: {title, company, location, description}}` with one question in each of two forms:
  - `noul`: a fit when p >= 0.5
  - `score`: a 4-level rubric; the expected score is rescaled to 0-100 and held to `min_fit_score`, the drop-in the issue suggested

  Every answer is cached in `jev_answers.json`. `decisions.json` holds each classifier's decision per listing.

Reproduce with `python tools/jev_eval/compare.py --offline`, which spends no tokens. `TYPESAFE_API_KEY=… python tools/jev_eval/compare.py` re-asks Jev.

## Numbers

| classifier | correct | accuracy | TP | FP | FN | TN | precision | recall |
|---|---|---|---|---|---|---|---|---|
| baseline (`classify`, floor 55) | 184/257 | 71.6% | 51 | 15 | 58 | 133 | 0.77 | 0.47 |
| Jev `noul` (p ≥ 0.5) | **226/257** | **87.9%** | 96 | 18 | 13 | 130 | 0.84 | 0.88 |
| Jev `score` (≥ 55/100) | 222/257 | 86.4% | 99 | 25 | 10 | 123 | 0.80 | 0.91 |

- **noul vs baseline:** Jev was right on 60 discordant pairs and the baseline on 18. McNemar exact p = 2e-6.
- **score vs baseline:** Jev was right on 57 and the baseline on 19. p = 1.5e-5.
- **Unbiased stratum:** the 54 no-hit listings were hand-picked, so this subset leaves them out. On the 203 listings the baseline itself selected, it scored 152 (74.9%), `noul` 180 (88.7%) and `score` 177 (87.2%). `noul` was right on 43 discordant pairs and the baseline on 15 (p = 3e-4).
- **Threshold is not what's doing the work:** no floor rescues the baseline. Its best is 55, the production value, at 184; 50 gives 167 and 60 gives 163. Jev `noul` stays at 218–226 for any threshold from 0.4 to 0.7. The poller's form (p × 100 against a floor of 55) gets 224.
- **Tokens:**
  - `noul`: 1,362 per posting (1,342 in, 20 out)
  - `score`: 1,440 per posting (1,423 in, 17 out)
  - median latency: 0.20 s per call
- **Price:** TypeSafe bills input tokens only, at $0.042 per million ([docs.typesafe.ai/models](https://docs.typesafe.ai/models)). That makes `noul` about **$0.000057 per posting**. The poller judges only listings it has not seen before. Even an unrealistically busy pass, with 40 new listings from each of 5 sources, costs about 1¢.
- **Determinism:** 40 listings were asked again in both forms. `noul` drifted by at most 0.050 (mean 0.016), `score` by at most 0.12 of a level, and **no decision flipped**. That is fine for a fit score. As the issue said, it would still not be acceptable in the browser path.

## What Jev fixes, and what it doesn't

- **Most of the gain is recall** (0.47 → 0.88): listings the keyword list never names. Examples: "Data Engineer", "Backend Software Engineer", "Staff Data Engineer", "Product Documentation Writer", "Robot Learning Engineer", and several Azumo/DRC/Pleo data roles. Remotive's short tag-list descriptions often have no keyword at all.
- **It also removes tech-adjacent false positives** the regex can't see past. The Lemon.io marketplace blurb names every stack, which scored "Senior React Native Developer" 80 and "Senior DevOps Engineer" 85. Security engineers, a cloud architect and an engineering manager also passed on keyword counts.
- **The #81 class is already fixed by the regex.** In this corpus neither classifier staged any of the 24 sales, customer-success or account-executive roles. On listings where `rag` appears as part of another word, both made 5 false positives. Jev's advantage there is in the listings the regex misses.
- **Where Jev lost (18), mostly generous yeses:**
  - Generic or full-stack software postings (Mirantis, Webflow "Build Loop", CircleCI, Podium, Edfinity, a Mercury intern, Sourcegraph, Twilio "Applied Research").
  - Data-adjacent roles that are not analysis:
    - data governance
    - a BI director
    - a data-strategy consultant
    - a fraud researcher
    - a toxicologist
  - A go-to-market engineer and a customer-facing AI architect.
  - Two plain Node and Python backend roles, which it scored 0.38 and 0.45.
- **Where neither was right:** Go and Java backend roles (Arango, Stone, QuickNode ×2, Clera, Convoso, Twikey). Jev reads the .NET-heavy keyword list narrowly, which is defensible for this owner.

A profile with a plain-language description would probably close some of these. This evaluation deliberately gave Jev only what production has: the keyword list.

## Caveats

- One adjudicator wrote both the labels and the Jev question. The labels were fixed before any Jev call. The question describes the task generically, and names no listing in the corpus.
- The labels are one reading of a keyword list, not the owner's own verdicts. Every label's reason is in `labels.json`, so they can be disputed row by row.
- The corpus is a single day's snapshot.

## Decision

Jev is implemented as an **opt-in per tenant**: `agent_settings.jev_classify` (migration 0038), shown in Settings · Agent. It only takes effect when the operator also sets `TYPESAFE_API_KEY` on the poller:

- **Scoring:** Jev's fit (p × 100) replaces the keyword score, against the same `min_fit_score`, and Jev is asked about every new listing. A staged lead's notes say "Fit NN/100 by Jev."
- **Default and fallback:** keyword `classify()` stays the default. It is also the fallback on any failure: no key, not opted in, a non-2xx response, a timeout, or a malformed or out-of-range answer. The first failure switches Jev off for the rest of that poll run, so an outage never costs a timeout per listing. A self-hoster with no key loses nothing.
- **Out of scope** (#280): the browser and form-finding path, and the LinkedIn/Handshake card pre-filters. Those still use keywords.

*Proudly Made in Nebraska. Go Big Red! 🌽 <https://xkcd.com/2347/>*
