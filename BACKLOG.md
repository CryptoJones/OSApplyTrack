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

## Operations and scalability

- [x] [#53 — Add hardened production container defaults](https://github.com/CryptoJones/OSApplyTrack/issues/53)
- [x] [#54 — Paginate or delta-refresh the applications list](https://github.com/CryptoJones/OSApplyTrack/issues/54)
- [ ] [#88 — Poller: `_fetch_feed_set` parses up to the whole-run limit on every category feed](https://github.com/CryptoJones/OSApplyTrack/issues/88)
- [ ] [#89 — `uv.lock`: exceptiongroup's typing-extensions marker was dropped in the 1.16.1 release commit](https://github.com/CryptoJones/OSApplyTrack/issues/89)

## Discovery and workflow

- [x] [#69 — Allow sorting by fit, date posted, or company name](https://github.com/CryptoJones/OSApplyTrack/issues/69)
- [x] [#70 — Allow adding custom RSS feeds for job listings](https://github.com/CryptoJones/OSApplyTrack/issues/70)
- [x] [#77 — Add RemoteFirstJobs and WorkAnywhere.pro as built-in sources, and the remaining We Work Remotely category feeds](https://github.com/CryptoJones/OSApplyTrack/issues/77)
- [x] [#80 — Poller: a cross-listed posting spends a slot in every category feed it appears in](https://github.com/CryptoJones/OSApplyTrack/issues/80)
- [x] [#81 — Poller: keyword matching is raw substring, so short keywords fire mid-word](https://github.com/CryptoJones/OSApplyTrack/issues/81)
- [ ] [#85 — Poller: the `.net` word-boundary guard blocks ASP.NET, VB.NET and ADO.NET titles](https://github.com/CryptoJones/OSApplyTrack/issues/85)
- [ ] [#86 — Poller: the keyword suffix allowance re-opens the rag/ml/go false positives](https://github.com/CryptoJones/OSApplyTrack/issues/86)
- [ ] [#87 — Poller: listings with no link bypass the cross-feed dedupe entirely](https://github.com/CryptoJones/OSApplyTrack/issues/87)

## Agentic auto-apply

An attached model evaluates qualifying leads, drafts the materials, answers the
screening questions, and parks a finished packet in a review queue. A human always
clicks Submit. Each step below ships on its own; step 3 delivers most of the value.

- [x] Step 1 — Read the job posting before drafting the letter ([#91](https://github.com/CryptoJones/OSApplyTrack/pull/91))
- [ ] [#92 — Step 2: the agent reads the posting and forms its own fit verdict](https://github.com/CryptoJones/OSApplyTrack/issues/92)
- [ ] [#93 — Step 3: prepared packets and the Ready-to-submit queue](https://github.com/CryptoJones/OSApplyTrack/issues/93)
- [ ] [#94 — Step 4: human-triggered browser submission, dry-run by default](https://github.com/CryptoJones/OSApplyTrack/issues/94)
- [ ] [#95 — Step 5: Lever, Ashby, and the unknown long tail](https://github.com/CryptoJones/OSApplyTrack/issues/95)

Proudly Made in Nebraska. Go Big Red! 🌽 https://xkcd.com/2347/
