# OSApplyTrack Backlog

This file mirrors the open
[GitHub Issues](https://github.com/CryptoJones/OSApplyTrack/issues) for the project.
Check an item only when its matching issue is closed. Deferred ideas in `README.md`
or `SPRINTS.md` are not committed backlog until they have a corresponding issue.

## Security and stability

- [x] [#51 — Close the Python poller link-check DNS rebinding gap](https://github.com/CryptoJones/OSApplyTrack/issues/51)
- [x] [#49 — Restrict forwarded-header trust to configured proxies](https://github.com/CryptoJones/OSApplyTrack/issues/49)
- [x] [#50 — Add global JSON body caps and per-field/cardinality limits](https://github.com/CryptoJones/OSApplyTrack/issues/50)
- [x] [#52 — Serialize overlapping tenant poll runs](https://github.com/CryptoJones/OSApplyTrack/issues/52)
- [x] [#117 — Encrypt sensitive columns at rest, secure by default for every deploy](https://github.com/CryptoJones/OSApplyTrack/issues/117) (phase 1 shipped in 1.26.0; `notes`, `email`, `contact_email`, `phone` and the extracted résumé fields stay deferred)
- [x] [#118 — Public beta terms, acceptance at sign-in, and a typed-confirmation data delete](https://github.com/CryptoJones/OSApplyTrack/issues/118)
- [x] [#120 — Saved answers reused across fields that ask for different units, periods or currencies](https://github.com/CryptoJones/OSApplyTrack/issues/120)
- [x] [#121 — Optional questions get filled with placeholder answers instead of left blank](https://github.com/CryptoJones/OSApplyTrack/issues/121)

## Operations and scalability

- [x] [#53 — Add hardened production container defaults](https://github.com/CryptoJones/OSApplyTrack/issues/53)
- [x] [#54 — Paginate or delta-refresh the applications list](https://github.com/CryptoJones/OSApplyTrack/issues/54)
- [x] [#88 — Poller: `_fetch_feed_set` parses up to the whole-run limit on every category feed](https://github.com/CryptoJones/OSApplyTrack/issues/88)
- [x] [#100 — CI is failing on main](https://github.com/CryptoJones/OSApplyTrack/issues/100) (auto-filed; a boot-order race in the SPA after #98)
- [x] [#89 — `uv.lock`: exceptiongroup's typing-extensions marker was dropped in the 1.16.1 release commit](https://github.com/CryptoJones/OSApplyTrack/issues/89)

## Discovery and workflow

- [x] [#69 — Allow sorting by fit, date posted, or company name](https://github.com/CryptoJones/OSApplyTrack/issues/69)
- [x] [#70 — Allow adding custom RSS feeds for job listings](https://github.com/CryptoJones/OSApplyTrack/issues/70)
- [x] [#77 — Add RemoteFirstJobs and WorkAnywhere.pro as built-in sources, and the remaining We Work Remotely category feeds](https://github.com/CryptoJones/OSApplyTrack/issues/77)
- [x] [#80 — Poller: a cross-listed posting spends a slot in every category feed it appears in](https://github.com/CryptoJones/OSApplyTrack/issues/80)
- [x] [#81 — Poller: keyword matching is raw substring, so short keywords fire mid-word](https://github.com/CryptoJones/OSApplyTrack/issues/81)
- [x] [#85 — Poller: the `.net` word-boundary guard blocks ASP.NET, VB.NET and ADO.NET titles](https://github.com/CryptoJones/OSApplyTrack/issues/85)
- [x] [#86 — Poller: the keyword suffix allowance re-opens the rag/ml/go false positives](https://github.com/CryptoJones/OSApplyTrack/issues/86)
- [x] [#87 — Poller: listings with no link bypass the cross-feed dedupe entirely](https://github.com/CryptoJones/OSApplyTrack/issues/87)
- [x] [#106 — Follow a company's Paylocity recruiting board](https://github.com/CryptoJones/OSApplyTrack/issues/106)
- [x] [#110 — Grey out "Mark applied" once a role is already applied](https://github.com/CryptoJones/OSApplyTrack/issues/110)
- [x] [#111 — Show the running build version in the header](https://github.com/CryptoJones/OSApplyTrack/issues/111)
- [x] [#112 — A dropped submit request leaves no trace anywhere](https://github.com/CryptoJones/OSApplyTrack/issues/112)
- [x] [#114 — A crashed browser submission leaves no evidence at all](https://github.com/CryptoJones/OSApplyTrack/issues/114)

## Agentic auto-apply

An attached model evaluates qualifying leads, drafts the materials, answers the
screening questions, and prepares a packet. With dry-run-only disabled, the worker
automatically promotes a clean browser dry run to a real submission. Required
review items and unsupported forms still need human attention.

- [x] Step 1 — Read the job posting before drafting the letter ([#91](https://github.com/CryptoJones/OSApplyTrack/pull/91))
- [x] [#92 — Step 2: the agent reads the posting and forms its own fit verdict](https://github.com/CryptoJones/OSApplyTrack/issues/92)
- [x] [#93 — Step 3: prepared packets and the Ready-to-submit queue](https://github.com/CryptoJones/OSApplyTrack/issues/93)
- [x] [#99 — Notify by Telegram (moo) when a packet is ready to submit](https://github.com/CryptoJones/OSApplyTrack/issues/99)
- [x] [#94 — Step 4: human-triggered browser submission, dry-run by default](https://github.com/CryptoJones/OSApplyTrack/issues/94)
- [x] [#95 — Step 5: Lever, Ashby, and the unknown long tail](https://github.com/CryptoJones/OSApplyTrack/issues/95)
- [x] [#133 — Optional review items permanently block a packet from being submitted](https://github.com/CryptoJones/OSApplyTrack/issues/133) (fix in [#132](https://github.com/CryptoJones/OSApplyTrack/pull/132))
- [x] [#135 — Résumé counted as attached when the board rejected it; captcha forms burn runs](https://github.com/CryptoJones/OSApplyTrack/issues/135) (fix in [#136](https://github.com/CryptoJones/OSApplyTrack/pull/136))
- [x] [#138 — Résumé attach check races the uploader: dry run says mapped, the real run says unmapped](https://github.com/CryptoJones/OSApplyTrack/issues/138)
- [x] [#151 — Greenhouse submit blocked by unfilled required fields (country, location, react-select questions) — not reCAPTCHA](https://github.com/CryptoJones/OSApplyTrack/issues/151) (fixed in 1.26.0)
- [x] [#124 — Reduce posting→applied latency so the agent reaches postings before they close](https://github.com/CryptoJones/OSApplyTrack/issues/124)
- [x] [#129 — Agent abandons a run when the form has a radio group or checkbox](https://github.com/CryptoJones/OSApplyTrack/issues/129) (fix in [#128](https://github.com/CryptoJones/OSApplyTrack/pull/128))

## Production audit — 2026-09-12

Pluto's API and agent ran release **v1.25.5**, revision `4a7f926`, at audit time.
Tenant 1 has the agent **enabled** and **dry_run=false**. Production evidence then
held **102 dry runs, 26 failures, and zero confirmed submissions**.

The cause, reproduced live against a Greenhouse form with every non-GET request
aborted: client-side validation blocked Submit and no application POST was ever
made. Three gaps, all closed in **1.26.0** (#151):

1. Greenhouse's rendered form requires a **Country** picker its Job Board API never
   lists. The packet now carries a `country` question (answered from the new
   Settings · Agent **Country** field, else inferred from the résumé's location).
2. Country, city and the custom questions are **react-select** comboboxes. Typing
   and pressing Enter committed nothing. The submitter now opens the widget, picks
   the matching option, and **verifies the committed value** before counting a field
   mapped.
3. The run trusted the packet's idea of the form. It now reads the **rendered form's
   own required-and-empty state** before any click (a dry run that leaves one is not
   clean and is not promoted), refuses a form where **nothing** mapped, and after a
   refused click reports the form's **own validation messages** instead of "no
   confirmation text was recognised".

Verified against the live Aperia posting from the issue with the application POST
aborted at the browser: all twelve fields mapped, zero unmapped, Submit clicked,
no validation wall. Creatio separately reports an interactive captcha and remains a
manual handoff.

Proudly Made in Nebraska. Go Big Red! 🌽 https://xkcd.com/2347/
