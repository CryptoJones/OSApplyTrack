# OSApplyTrack Backlog

This file mirrors the open
[GitHub Issues](https://github.com/CryptoJones/OSApplyTrack/issues) for the project.
Check an item only when its matching issue is closed. Deferred ideas in `README.md`
or `SPRINTS.md` are not committed backlog until they have a corresponding issue.

## Security and stability

- [x] [#228 — [SEC] Magic-link sign-in is broken on the live instance: POST /api/auth/request returns 500 for every valid email](https://github.com/CryptoJones/OSApplyTrack/issues/228) (fixed in #245: fail loudly at boot on a misconfigured SMTP sender instead of a per-request 500)
- [x] [#229 — [SEC] Session cookie issued without Secure flag: app layer does not see the request as HTTPS behind the TLS proxy](https://github.com/CryptoJones/OSApplyTrack/issues/229) (fixed in #247: surface a dropped X-Forwarded-Proto so Secure cookies + HSTS aren't silently off)
- [x] [#230 — [SEC] Duplicate and conflicting security headers emitted by both edge and app](https://github.com/CryptoJones/OSApplyTrack/issues/230) (the app is the single source of truth for the header set — README now tells operators not to re-add them at the reverse proxy; the live pluto edge stops re-adding the duplicates)
- [x] [#231 — [SEC] DNS-rebinding TOCTOU in the IMAP mailbox host guard](https://github.com/CryptoJones/OSApplyTrack/issues/231) (fixed in 1.39.4: the mailbox resolves the host once and dials a socket pinned to that resolution's public addresses, handed to MailKit already connected — no second resolution at connect time, the same no-TOCTOU pattern as the scrape path in #51)
- [x] [#232 — [SEC] Minor information disclosure: build version, DB status, and nginx version on unauthenticated surfaces](https://github.com/CryptoJones/OSApplyTrack/issues/232) (decision recorded: `/health` build version and `/health/ready` DB status stay — both serve a purpose and are documented as accepted disclosures in code; the nginx version banner is turned off at the edge with `server_tokens off;`, documented in the README)
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
- [x] [#201 — Mobile: the Ready list will not scroll — swiping down past the bulk bar rubber-bands and Safari refreshes the page](https://github.com/CryptoJones/OSApplyTrack/issues/201) (fixed in 1.31.0)
- [x] [#114 — A crashed browser submission leaves no evidence at all](https://github.com/CryptoJones/OSApplyTrack/issues/114)
- [x] [#233 — LinkedIn as a signed-in discovery source: search with the candidate's own account, store only the employer's posting, skip Easy Apply](https://github.com/CryptoJones/OSApplyTrack/issues/233) (shipped in 1.38.0: the `linkedin` source searches the tenant's keywords through the endpoints LinkedIn's own web app uses, with the session the agent's browser keeps on the `linkedin.com` board account — password sign-in, the app tap asked for by a moo and waited on, the emailed PIN read from the mailbox, the cookies sealed for 300 days; every offsite-apply posting is staged with the employer's link, Easy Apply ones are remembered and skipped, the ledger is asked before a posting is read; LinkedIn's guest search is the fallback with no session; `linkedin.com` is an aggregator host in both runtimes)
- [x] [#235 — Apply that opens a social sign-up wall (Alignerr: only "Continue with Google / LinkedIn") is reported as "no form appeared"](https://github.com/CryptoJones/OSApplyTrack/issues/235) (fixed in 1.38.1 and 1.38.2: a page whose only way in is identity-provider buttons, with nothing to type into, is named as such — what Apply led to, which providers, and that the posting is the person's by Copy answers and open — instead of "no form appeared" or "nothing matched" on its terms checkbox; and the new tab Apply opens is adopted whenever it arrives during the ten-second wait, not only in the moment after the click — through pluto's proxy Alignerr's arrived later, so the run kept watching the posting and never saw the wall)
- [x] [#238 — Poller: jobright.ai is an aggregator — a LinkedIn posting whose Apply leads there should resolve on to the employer, or be apply-by-hand](https://github.com/CryptoJones/OSApplyTrack/issues/238) (fixed in 1.38.2: `jobright.ai` is an aggregator host in both runtimes, so the #191 rule follows the listing on to the employer's posting when it can and the lead is apply-by-hand otherwise)
- [x] [#239 — Browser: Paycor's pre-screening step (a radio question and Continue before the form) is read as "no Apply button or link on the page"](https://github.com/CryptoJones/OSApplyTrack/issues/239) (fixed in 1.39.0: a page whose only controls are radio/checkbox questions and a Continue is a pre-screening step, not a formless dead end — discovery answers it from the tenant's standing answers and reads the form behind it, submit answers it from the packet and presses through to that form, and a step that cannot be answered is named instead of "no Apply button or link on the page")
- [x] [#243 — Poller: sundayy.com is an aggregator — its Apply is a redirect chain through more aggregators, not a form](https://github.com/CryptoJones/OSApplyTrack/issues/243) (fixed in 1.39.1: `sundayy.com`, and the two other job-board hosts its Apply chains through — `thebigjobsite.com` and `lensa.com` — are aggregator hosts in both runtimes, so the #191 rule follows the listing on to the employer when it can and the lead is apply-by-hand otherwise instead of a failed browser run)
- [x] [#246 — Browser: a form collapsed behind "Apply Now" with a hidden résumé input is read as "no Submit button found" (Freshteam)](https://github.com/CryptoJones/OSApplyTrack/issues/246) (fixed in 1.39.5: the reveal's early return no longer counts a hidden file input as "the form is on screen", so a form shipped collapsed behind an "Apply Now" link is expanded by the click and every real field is discovered — a complete Copy-answers packet — instead of only the résumé field and a "no Submit button found". A reCAPTCHA still, correctly, blocks auto-submit for this posting)

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
- [x] [#159 — Submit and packet/prepare never queue a browser run from the api container — only the agent has Browser__Endpoint](https://github.com/CryptoJones/OSApplyTrack/issues/159) (fix in [#172](https://github.com/CryptoJones/OSApplyTrack/pull/172): the worker heartbeats, the api reads it)
- [x] [#168 — Take the Greenhouse security code back from a Telegram reply to the moo](https://github.com/CryptoJones/OSApplyTrack/issues/168) (fix in [#173](https://github.com/CryptoJones/OSApplyTrack/pull/173))
- [x] [#180 — Long-tail boards whose Apply leads somewhere else: Workable, Breezy, SmartRecruiters, join.com, aggregator links](https://github.com/CryptoJones/OSApplyTrack/issues/180) (fixed in 1.30.0)
- [x] [#183 — Prepare from the api container should hand discovery to the worker, not skip it](https://github.com/CryptoJones/OSApplyTrack/issues/183) (fixed in 1.30.0)
- [x] [#184 — Worker heartbeat cadence is tied to the submit lane, so worker_running flickers on a worker without a browser](https://github.com/CryptoJones/OSApplyTrack/issues/184) (fixed in 1.30.0)
- [x] [#185 — Ready packets with a clean dry run never promote once dry-run is switched off](https://github.com/CryptoJones/OSApplyTrack/issues/185) (fixed in 1.30.0)
- [x] [#186 — Bulk actions in the Ready lane: prepare / submit / pass the selected packets without tripping the rate limit](https://github.com/CryptoJones/OSApplyTrack/issues/186) (fixed in 1.30.0)
- [x] [#187 — Field locator prefers a substring label match over an exact one, so a second similarly-named field gets filled twice](https://github.com/CryptoJones/OSApplyTrack/issues/187) (fixed in 1.30.0)
- [x] [#188 — Salary expectation should carry a period and a currency, and convert within the same currency](https://github.com/CryptoJones/OSApplyTrack/issues/188) (fixed in 1.30.0)
- [x] [#189 — Answer bank edits should be able to refill already-built packets, and packets should not freeze profile facts](https://github.com/CryptoJones/OSApplyTrack/issues/189) (fixed in 1.30.0)
- [x] [#190 — The "filled" moo and the evidence label should say what still needs the person](https://github.com/CryptoJones/OSApplyTrack/issues/190) (fixed in 1.30.0)
- [x] [#191 — Poller: resolve aggregator listing links to the employer's posting, or mark the lead apply-by-hand](https://github.com/CryptoJones/OSApplyTrack/issues/191) (fixed in 1.30.0)
- [x] [#192 — Worker-level tests for the parked-on-code path, and a live check of the Telegram reply](https://github.com/CryptoJones/OSApplyTrack/issues/192) (worker-level tests in 1.30.0; live check passed on pluto 2026-09-13 — the code came back through a Telegram reply and GitLab was submitted)
- [x] [#195 — Greenhouse résumé listed optional by the API but required on the form: a failed attach is not unmapped, so the click goes ahead into "Resume/CV is required"](https://github.com/CryptoJones/OSApplyTrack/issues/195) (fixed in 1.31.1: a failed attach always refuses, the uploader gets 2.5 s more to fail, the pre-click sweep reads the upload group's own required flag and retries Enter manually there)
- [x] [#205 — Zoho Recruit's consent banner arrives after load with a full-viewport freeze layer: Apply times out under it](https://github.com/CryptoJones/OSApplyTrack/issues/205) (fixed in 1.31.1: consent is dismissed again right before the Apply click, and once more if the click still would not land)
- [x] [#207 — Zoho Recruit long-tail form: Country, LinkedIn and Facebook get the wrong answers — the label fallback matches too loosely](https://github.com/CryptoJones/OSApplyTrack/issues/207) (fixed in 1.31.2: discovery reads a web component's own label attribute and the row's label, keeps same-id boxes by label; a question with no readable label is never put to the model; social-profile boxes take that site's link or stay empty)
- [x] [#210 — join.com: the agent clicks "Apply later" (the email-me-the-link box) instead of Apply, so the board emails the posting link and no application is sent](https://github.com/CryptoJones/OSApplyTrack/issues/210) (fixed in 1.33.0: a lone email box is not an application form; nothing that reads as "later / send me the link" is ever the Apply trigger or the Submit button; a side form's required email is ignored by the required-empty sweep. The email sign-in behind Apply is driven too: a dry run stops there and says what Submit would do; a real Submit types the candidate's email, presses Continue, and parks the way the Greenhouse security code does — the emailed code or link, pasted in the app, replied to the 🔐 moo, or read from the mailbox, takes the run on to the form in the same session; a link is followed only onto the board's own site)
- [x] [#211 — Pipeline view: click the strip's PIPELINE label to see the submit queue and what each request will do when drained](https://github.com/CryptoJones/OSApplyTrack/issues/211) (shipped in 1.33.0: `GET /api/pipeline` and the Pipeline view — queue in claim order with phase and what the worker will do, the Ready packets not queued and what holds them, Submit all clean)
- [x] [#200 — Last name drafted as "K. Clark": middle initials land in the last name, and the name fields cannot be corrected in Settings · Answers](https://github.com/CryptoJones/OSApplyTrack/issues/200) (fixed in 1.31.0: initials stay out of the split, and First name / Last name are rows in Settings · Answers)
- [x] [#202 — Browser: cookie-consent dialogs ("Accept all") sit over the Apply button, so the click times out and the run reports no form](https://github.com/CryptoJones/OSApplyTrack/issues/202) (fixed in 1.31.0)
- [x] [#203 — Submit drives the packet's stale provider, and a posting that is gone (HTTP 410/404) is reported as "no form found"](https://github.com/CryptoJones/OSApplyTrack/issues/203) (fixed in 1.31.0)
- [x] [#224 — Demographic answers (gender, race, veteran, disability) are never saved or loaded on queued jobs](https://github.com/CryptoJones/OSApplyTrack/issues/224) (fixed in 1.36.0: bankable, answered once in Settings · Answers or on any packet, applied to every packet whose form offers the same option, filled by the browser, never guessed, never flagged)
- [x] [#221 — MyGreenhouse: renew the portal session through the agent's browser when it lapses](https://github.com/CryptoJones/OSApplyTrack/issues/221) (shipped in 1.37.0: the agent's pass signs in at `my.greenhouse.io` in its browser when the kept session is missing, bounced or within two days of running out — Send security code, the code read from the tenant's mailbox, the `_session_id` cookie sealed on the board-account row for thirteen days; a tenant is tried at most every six hours; the poller only searches with the kept session and clears one the portal bounces — its own plain-client sign-in, which the portal never sent the mail for, is gone)
- [x] [#222 — SuccessFactors driver leftovers on Kiewit: pinned picker answers not selected, Acknowledgement checkbox and a years box regressed](https://github.com/CryptoJones/OSApplyTrack/issues/222) (fixed in 1.37.0, read against the real form: ids that start with a digit are looked up by attribute so the picker's menu is read and the pinned answer chosen; the required star is stripped from labels on both sides so a field the board renumbered, or two boxes it names `-1`, are found by label; a label match that is not a control no longer sinks the one before it; the Acknowledgement box the widget re-renders is read fresh after the click; "No Selection" is never picked; an account that already applied is told so)
- [x] [#218 — MyGreenhouse as a discovery source: the poller signs in to Greenhouse's candidate portal (emailed code) and pulls its job search](https://github.com/CryptoJones/OSApplyTrack/issues/218) (shipped in 1.35.0: the `mygreenhouse` source, per tenant, signed in with the greenhouse.io board account by the security code read from the tenant's mailbox, the session kept sealed on the account row; keyword searches plus the newest postings, remote-only when the profile is)
- [x] [#216 — Account-only ATSs (SAP SuccessFactors): save the candidate's own board account and let the browser sign in and apply](https://github.com/CryptoJones/OSApplyTrack/issues/216) (shipped in 1.34.0: Settings · Agent · Board accounts with `GET/PUT/DELETE /api/board-accounts`, password write-only and sealed; the browser signs in when Apply lands on a password page whose host an account covers, opens folded accordion sections, counts a résumé already on the account as attached, presses SuccessFactors' Apply and answers the confirm dialog; no account → the run names the host and where to save one. 1.34.1: a picker the account already filled — Country, State, Education Type — is left alone and counts as answered, and the option finder honours aria-owns)
- [x] [#214 — SuccessFactors career sites: the agent takes the posting page's job-search and job-alert boxes for the application form, never clicks Apply now, and Apply leads to an account-only sign-in](https://github.com/CryptoJones/OSApplyTrack/issues/214) (fixed in 1.33.1: search and job-alert widgets are never form controls — not for "is the form on screen", not for discovery, not for the required-empty sweep; Apply that leads to SuccessFactors or Workday is reported as needing a signed-in candidate account, yours by Copy answers and open; `successfactors` is a known provider the browser never drives)
- [x] [#177 — Browser fill misses plain forms: labels-as-text never match, Ashby's single Name box and late-loading form, and a zero-field dry run reports as filled](https://github.com/CryptoJones/OSApplyTrack/issues/177) (fix in [#178](https://github.com/CryptoJones/OSApplyTrack/pull/178): the form is found in any frame, after it renders)
- [x] [#174 — Answer bank: every question the agent meets, with the answer it gave, editable in one place and reused next time](https://github.com/CryptoJones/OSApplyTrack/issues/174) (fix in [#175](https://github.com/CryptoJones/OSApplyTrack/pull/175): Settings · Answers)

## Production audit — 2026-09-12, closed out 2026-09-13

Pluto ran **v1.25.5** at audit time with **102 dry runs, 26 failures, and zero
confirmed submissions**. Between 1.26.0 and **1.27.0** (all deployed the same night)
the chain was fixed end to end, each step found by reading the next production run:

1. **Required fields the packet never knew** (Greenhouse's Country picker, its
   react-select city and custom selects) and no check of the rendered form — 1.26.0.
2. Country on every Greenhouse packet, a geocodable city answer, a required cover
   letter entered as text, "Page not found" retired as closed — 1.26.1.
3. The click read the page in the quiet moment before reCAPTCHA and the POST even
   started; it now waits for the form's verdict and records the application request.
   Country pick-list spellings and combined eligibility questions drafted as the
   form accepts them — 1.26.2.
4. **The click itself**: the finder took the posting's "Apply" anchor, first in
   document order, never the form's Submit — 1.26.3.
5. **Greenhouse's captcha fallback**: a bot-scored submission is answered 428 and an
   emailed security code. The run parks in the same session, moos, and finishes once
   the human pastes the code (`POST /api/apps/{name}/security-code`) — 1.27.0.
6. Auto-apply gated per account by the operator's `agent_allowlist` — 1.27.0.

**First real submission: Aperia, 2026-09-13 04:37 CDT**, confirmed by Greenhouse and by
the employer's email. Greenhouse applications go one at a time: each parks for its
code for up to eight minutes. Open follow-ups: [#159](https://github.com/CryptoJones/OSApplyTrack/issues/159)
(the api container cannot queue a browser run, so the SPA's Submit and Prepare do
nothing on a stock deploy); Eleventh Hour needs a human answer to a required question.

Proudly Made in Nebraska. Go Big Red! 🌽 https://xkcd.com/2347/
