// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Data;
using Microsoft.Playwright;

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>What one browser run produced.</summary>
/// <param name="Filled">The form was reached and the mapped answers were typed in.</param>
/// <param name="Submitted">Submit was clicked AND a confirmation was recognised.</param>
/// <param name="Unmapped">Required questions with an answer that no field could be found for, plus any
/// field the rendered form itself still marks required-and-empty after the fill (a country picker the
/// packet never knew about, a combobox whose value did not commit) — any of these refuses the click.</param>
/// <param name="Closed">The posting was gone: the page says it is no longer open. The lead expired, it did not fail.</param>
/// <param name="Discovered">Questions the run met that the packet had never heard of — a dialog whose steps only
/// exist once they are reached (LinkedIn Easy Apply, #278). The worker adds them to the packet and drafts answers.</param>
/// <param name="Captcha">The form guards submit with an interactive captcha. Not a defect to retry and not
/// something to solve — the posting has to be finished by hand with Copy answers and open.</param>
public sealed record SubmitOutcome(
    bool Filled, bool Submitted, string Url, string Confirmation, byte[]? Screenshot,
    List<string> Unmapped, List<string> Mapped, string Error, bool Closed = false, bool Captcha = false,
    List<PacketQuestion>? Discovered = null);

/// <summary>
/// What a parked run is waiting for the person to relay. <see cref="Recipient"/> is the
/// address the board emailed. <see cref="SignIn"/> is false for Greenhouse's security
/// code after Submit and true for a board that signs the candidate in by email before it
/// shows the form (join.com, #210) — there the reply may be the emailed code <i>or</i> the
/// emailed link, and the browser follows a link only onto the board's own site.
/// </summary>
public sealed record CodeRequest(string Recipient, bool SignIn = false);

/// <summary>The one browser-run seam: what the worker calls, and what a worker-level test
/// can stand in for without a browser (#192).</summary>
public interface IBrowserSubmitter
{
    /// <inheritdoc cref="BrowserSubmitter.RunAsync"/>
    Task<SubmitOutcome> RunAsync(
        string link, AgentPacket packet, (byte[] Bytes, string Name)? resumePdf, bool dryRun,
        CancellationToken ct = default, string resumeText = "", string coverLetter = "",
        Func<CodeRequest, CancellationToken, Task<string?>>? awaitSecurityCode = null,
        IReadOnlyList<BoardAccount>? accounts = null);
}

/// <summary>
/// Drives the browser container at a posting: fill every mapped answer, attach the
/// résumé from memory, screenshot — and click Submit only when asked (<c>dry_run</c>
/// false) and only when every required question with an answer found its field.
/// Fields are found by accessible name first, then by the ATS field name — the
/// generic adapter — so Greenhouse, Lever, Ashby and (with the opt-in) the long tail
/// all go through the same code. Containment lives in <see cref="BrowserSession"/>.
/// </summary>
public sealed partial class BrowserSubmitter : IBrowserSubmitter
{
    [GeneratedRegex(@"thank you|thanks for applying|application (?:has been |was |is )?(?:submitted|received|sent|complete)|we(?:'ve| have) received your application|successfully (?:submitted|applied)|your application is in|you have applied", RegexOptions.IgnoreCase)]
    private static partial Regex Confirmation();

    // A closed posting: the apply URL redirects to the board or shows a gone-notice. Matching
    // any of these means there is no form to fill — the lead expired between discovery and now,
    // which is an expected outcome, not a failure to fix. Kept specific so an open posting's
    // prose ("no longer supported", etc.) never trips it.
    // "Page not found" is what Greenhouse serves for a posting that has been taken down
    // outright (Cresteo and Varicent on 2026-09-13): no gone-notice, no form, a 404 page.
    [GeneratedRegex(@"job you are looking for is no longer open|no longer (?:open|accepting applications)|(?:position|posting|job|role|opening) (?:has been|was|is now) (?:filled|closed)|this (?:position|posting|job|role|opening) is (?:no longer available|closed|not available anymore)|\bpage not found\b|\bjob (?:posting )?not found\b|this job (?:posting )?(?:is )?no longer exists", RegexOptions.IgnoreCase)]
    private static partial Regex ClosedPosting();

    /// <summary>
    /// True when a page's text reads as a closed/expired posting. The single source for
    /// closed-posting detection, shared by the browser run (against the rendered body) and
    /// the agent's cheap liveness pre-check (against the fetched HTML), so both agree on
    /// what "gone" looks like without a second regex to keep in sync.
    /// </summary>
    public static bool IsClosedPosting(string text) => text.Length > 0 && ClosedPosting().IsMatch(text);

    private readonly BrowserOptions _options;
    private readonly ILogger<BrowserSubmitter> _log;

    public BrowserSubmitter(BrowserOptions options, ILogger<BrowserSubmitter> log)
    {
        _options = options;
        _log = log;
    }

    /// <param name="resumeText">The résumé as text, for boards whose uploader will not take the
    /// file — their own "Enter manually" box accepts it. Empty disables that fallback.</param>
    /// <param name="coverLetter">The drafted letter, for a form whose cover letter is a file
    /// field: it goes in through that field's own "Enter manually" box. Empty means a required
    /// cover-letter file stays unmapped.</param>
    /// <param name="awaitSecurityCode">Called with the address the board emailed when the run
    /// has to park on something only the candidate's mailbox holds — Greenhouse's security
    /// code after Submit, or a board's sign-in code or link before the form (#210); returns
    /// what the human handed over, or null when nothing arrived in time. Null disables both:
    /// a dry run stops and reports instead of parking.</param>
    /// <param name="accounts">The candidate's own sign-ins on account-only ATSs (SAP SuccessFactors,
    /// #216): the session signs in with the one for the host Apply leads to.</param>
    public async Task<SubmitOutcome> RunAsync(
        string link, AgentPacket packet, (byte[] Bytes, string Name)? resumePdf, bool dryRun,
        CancellationToken ct = default, string resumeText = "", string coverLetter = "",
        Func<CodeRequest, CancellationToken, Task<string?>>? awaitSecurityCode = null,
        IReadOnlyList<BoardAccount>? accounts = null)
    {
        if (!_options.IsConfigured)
            throw new AppValidationException("browser submission isn't configured on this instance");

        // LinkedIn's Easy Apply is a dialog with its own way in and its own steps, not a page form (#278).
        if (packet.Provider == AtsProvider.LinkedInEasy)
        {
            await using var linkedIn = await BrowserSession.OpenAsync(_options, link, ct, accounts, reveal: false);
            return await LinkedInEasyApply.RunAsync(linkedIn, packet, dryRun, _log, ct);
        }

        var opened = await BrowserSession.OpenAsync(_options, AtsProvider.ApplyUrl(link, packet.Provider), ct, accounts);
        // A hosted Greenhouse posting whose board redirects to the employer's own careers
        // site: the navigation was refused for leaving the posting's site and the page is
        // blank. Greenhouse serves the same form itself — open that instead (#280).
        if (opened.Refused.Count > 0 && !opened.Page.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && AtsProvider.GreenhouseEmbedForm(link) is { } embed)
        {
            await opened.DisposeAsync();
            opened = await BrowserSession.OpenAsync(_options, embed, ct, accounts);
        }
        await using var session = opened;
        var page = session.Page;
        var mapped = new List<string>();
        var unmapped = new List<string>();
        byte[]? screenshot = null;
        try
        {
            // Before filling anything, notice a posting that closed between discovery and now:
            // Greenhouse and the rest redirect a gone job to the openings board, so every field
            // comes back "unmapped" and the run looks like a mapping failure when nothing is wrong.
            // Recognise it, screenshot it, and hand the caller a Closed outcome so the lead is
            // retired rather than logged as a broken submission.
            // A 404 or 410 from the posting itself is the same verdict, whatever the page
            // says: Workable answers a taken-down job with a 410 (#203).
            var body = await BodyTextAsync(page.MainFrame);
            // The site will not talk to this server at all — McGraw Hill's careers site answers
            // pluto with a bare "403 Forbidden". That is not a page with no Apply button on it,
            // which is what the run used to report, and no retry will change it (#280).
            if (session.Status is 401 or 403 or 429 or 451 && body.Length < 600)
            {
                screenshot = await session.ScreenshotAsync();
                return new SubmitOutcome(false, false, page.Url, "", screenshot, unmapped, mapped,
                    $"the posting's site refused this server (HTTP {session.Status}) — it has to be opened from your own browser: apply by Copy answers and open the posting");
            }
            if (session.Status is 404 or 410 || IsClosedPosting(body))
            {
                screenshot = await session.ScreenshotAsync();
                return new SubmitOutcome(false, false, page.Url, "", screenshot, unmapped, mapped,
                    session.Status is 404 or 410 ? $"posting is gone (HTTP {session.Status})" : "posting is no longer open",
                    Closed: true);
            }

            // Let the form finish arriving before typing into it. Greenhouse's form fetches a
            // candidate profile after it renders and writes the name fields from the (empty)
            // reply when it lands — which, mid-fill, emptied first and last name that had just
            // been typed. Bounded: a page that never goes idle is filled anyway.
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); }
            catch (TimeoutException) { /* analytics beacons and the like; carry on */ }
            // A consent banner that arrived with the form, or after the page went idle, would
            // sit over every field and make the first fill time out (#202).
            await BrowserSession.DismissConsentAsync(page);
            // UKG (UltiPro): the sign-in the session just did lands a first-time candidate on
            // the employer's own profile step — name, phone, a privacy consent and "Create
            // account" — before the application. Filled by the generic pass, but the button is
            // no Submit, and the run died on "no Submit button found" (#277).
            if (await AdvanceUkgProfileAsync(session, packet))
            {
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 8_000 }); }
                catch (TimeoutException) { /* judged by what renders */ }
                await BrowserSession.DismissConsentAsync(page);
            }
            // A pre-screening step before the form — Paycor asks "Will you require sponsorship?"
            // on a page of radios and a Continue, and the reveal, finding no form and no Apply,
            // dead-ended at "no Apply button or link on the page" (#239). Answer the step from the
            // packet and press Continue through to the form. What it advanced past is already
            // answered, so those questions count mapped and are not sought again on the form.
            foreach (var live in await AdvancePreScreenAsync(page, live =>
                         FindQuestion(packet, live.Id, live.Label) is { } q && packet.Answers.TryGetValue(q.Id, out var a) ? a : null,
                         _log))
                if (FindQuestion(packet, live.Id, live.Label) is { } q && !mapped.Contains(q.Id))
                    mapped.Add(q.Id);
            // Still on the step: a required question had no answer to give. Name it and stop,
            // rather than fall through to "nothing on this form could be filled" — which reads
            // as a broken packet rather than a gate the person can clear by hand (#239).
            if (await IsPreScreenAsync(page))
            {
                screenshot = await session.ScreenshotAsync();
                return new SubmitOutcome(false, false, page.Url, "", screenshot, unmapped, mapped,
                    "this page is a pre-screening step (a question and Continue) before the form, and a required "
                    + "question could not be answered from your saved answers — set it in Settings · Agent, or apply via Copy answers and open the posting");
            }
            // Then find the form — which is not always in the page. Comeet's "Apply for this job"
            // loads it into a cross-origin iframe; Ashby fetches it after the page has gone idle
            // ("Fetching application form" was the whole of one dry run's screenshot). Every
            // locator and page script below runs against this frame, not the top document.
            var form = await FormFrameAsync(page);

            // The board's sign-in is still on screen: no account covered it, or the one that did
            // was refused. The session already wrote down why (#216). Nothing here is the form.
            if (await BrowserSession.SignInFormVisibleAsync(page))
                return new SubmitOutcome(false, false, page.Url, "", await session.ScreenshotAsync(), unmapped, mapped,
                    session.RevealNote.Length > 0 ? session.RevealNote
                        : "this page is the board's sign-in, not the application form — save the account under Settings · Agent · Board accounts, or apply via Copy answers and open the posting");

            // A page whose only fillable control is an email box is not the form, it is the
            // board's sign-in: join.com's Apply leads to "Your email address / Continue" and
            // shows its form to nobody who is not signed in (#210). A dry run stops here and
            // says so. A real run signs in as the candidate and parks the way the Greenhouse
            // security code does — the person relays the code, or the link, the board emailed,
            // and the run carries on in this same session to the form the board now shows.
            if (await OnlyEmailFieldsAsync(form))
            {
                var stopped = await SignInAsync(session, form, packet, awaitSecurityCode, ct, accounts);
                if (stopped is not null)
                    return stopped with { Url = session.Page.Url, Screenshot = await session.ScreenshotAsync() };
                page = session.Page;
                form = await FormFrameAsync(page);
            }

            foreach (var q in packet.Questions)
            {
                ct.ThrowIfCancellationRequested();
                // Already answered on a pre-screening step the run advanced past (#239): its
                // field is behind us, not on this form, so do not seek it here and mark it unmapped.
                if (mapped.Contains(q.Id)) continue;
                // A demographic question is filled only with the person's own saved answer;
                // with none it stays blank, and it never counts as unmapped (#224).
                if (q.Kind == PacketQuestion.Eeo)
                {
                    if (packet.Answers.TryGetValue(q.Id, out var own) && !string.IsNullOrWhiteSpace(own)
                        && await FillAsync(form, q, own))
                        mapped.Add(q.Id);
                    continue;
                }
                if (q.Type == PacketQuestion.File)
                {
                    if (IsResume(q) && await ResumeOnFileAsync(form))
                    {
                        // The candidate's account already holds a résumé and the form shows it
                        // (SuccessFactors: "Upload Resume · resume.pdf · Edit document", #216).
                        // Nothing to attach; the field is satisfied as it stands.
                        mapped.Add(q.Id);
                    }
                    else if (IsResume(q) && resumePdf is { } pdf)
                    {
                        // With a PDF on hand the résumé is required for the click whatever the
                        // API said: Greenhouse's Job Board API lists GitLab's résumé as optional
                        // while the rendered form marks it required, so an attach that failed
                        // on an "optional" résumé landed in neither list and the run clicked
                        // Submit into "Resume/CV is required" (#195). A failed attach refuses.
                        if (await AttachResumeAsync(form, q, pdf, resumeText)) mapped.Add(q.Id);
                        else unmapped.Add(q.Id);
                    }
                    else if (IsCoverLetter(q) && coverLetter.Length > 0)
                    {
                        // Greenhouse's cover letter is a file field with a text twin behind its
                        // own "Enter manually"; a form that requires one was refused for want of
                        // a file we never had. The drafted letter is the text.
                        if (await EnterCoverLetterAsync(form, coverLetter)) mapped.Add(q.Id);
                        else if (q.Required) unmapped.Add(q.Id);
                    }
                    else if (q.Required)
                        unmapped.Add(q.Id);
                    continue;
                }
                if (!packet.Answers.TryGetValue(q.Id, out var answer) || string.IsNullOrWhiteSpace(answer))
                    continue;
                // A picker the candidate's account already filled — SuccessFactors renders
                // Country, State, Education Type and the rest from the profile — is left as it
                // stands: the account's data is the truth, and driving the packet's guess into
                // it cleared two of Kiewit's required pickers and refused the click (#216).
                if (await PrefilledPickerAsync(form, q))
                {
                    mapped.Add(q.Id);
                    continue;
                }
                if (await FillAsync(form, q, answer)) mapped.Add(q.Id);
                else if (q.Required) unmapped.Add(q.Id);
            }

            // Anything the page emptied behind our back gets one more go before it is judged.
            // Ashby, Lever and a good part of the long tail take one Name box, not first and
            // last: when both halves went unmapped and such a box exists, it gets "First Last".
            await FillFullNameAsync(form, packet, mapped, unmapped);
            foreach (var (key, label) in await RequiredEmptyAsync(form))
            {
                var q = FindQuestion(packet, key, label);
                if (q is null || !mapped.Contains(q.Id) || q.Type == PacketQuestion.File
                    || !packet.Answers.TryGetValue(q.Id, out var again) || string.IsNullOrWhiteSpace(again))
                    continue;
                await FillAsync(form, q, again);
            }

            // Now ask the FORM what is still missing, not just the packet. The packet is the ATS
            // API's idea of the form and the two drift: Greenhouse's rendered form carries a
            // required Country picker its Job Board API never mentions, its city field is an
            // autocomplete that only counts once a suggestion is chosen, and its custom selects
            // are react-select widgets that look filled after typing and are not. Every one of
            // those produced "9 mapped, 0 unmapped", a clean dry run, an automatic promotion,
            // and a Submit click into a wall of client-side validation with no POST ever sent.
            // Anything the page still marks required-and-empty is unmapped, whatever we thought.
            // That now includes the résumé: Greenhouse marks the whole upload group required
            // (never the file input), and its uploader can fail well after the attach looked
            // good — so the group is read here, and a résumé the form still wants gets one
            // more go through the board's own Enter manually box before it is judged (#195).
            var resumeRetried = false;
            foreach (var (key, label) in await RequiredEmptyAsync(form))
            {
                var q = FindQuestion(packet, key, label);
                var id = q?.Id ?? key;
                var isResume = q is not null ? IsResume(q) : ResumeWords().IsMatch(key) || ResumeWords().IsMatch(label);
                if (isResume && resumePdf is not null && !resumeRetried)
                {
                    resumeRetried = true;
                    await ClearStagedResumeAsync(form);
                    if (await EnterResumeManuallyAsync(form, resumeText))
                    {
                        if (!mapped.Contains(id)) mapped.Add(id);
                        continue;
                    }
                }
                mapped.Remove(id);
                if (!unmapped.Contains(id)) unmapped.Add(id);
            }

            screenshot = await session.ScreenshotAsync();
            // Reported on a dry run too: the dry run's whole job is to find out whether this
            // posting can be finished unattended, and with a captcha in the way the answer is no.
            if (await HasCaptchaAsync(page.MainFrame) || await HasCaptchaAsync(form))
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                    "this form is guarded by a captcha — finish it with Copy answers and open",
                    Captcha: true);
            // A form with fields on it and not one of them filled is not "clean", it is a packet
            // that describes some other form. Seen in production: zero mapped, zero unmapped,
            // and a run that would have clicked Submit on an empty application.
            // Likewise a page with no form on it at all: that used to pass as a clean dry run
            // ("filled", zero mapped) and moo the human to come and click Apply on nothing.
            // A page whose only fillable control is an email box is a sign-in, not the form
            // (#210). The sign-in phase above handles the one the page opened with; this catches
            // a page that turned into one under the fill. One mapped field there would read as
            // a clean dry run, promote itself, and click Submit on a page that has none.
            if (await OnlyEmailFieldsAsync(form))
                return new SubmitOutcome(false, false, page.Url, "", screenshot, unmapped, mapped,
                    SignInError + "; apply via Copy answers and open the posting");
            // A terms checkbox beside "Continue with Google / LinkedIn" is a control, not a form:
            // the page is a sign-up wall (Alignerr), and the verdict says so, not "nothing matched".
            if (mapped.Count == 0 && await BrowserSession.SocialSignUpWallAsync(page) is { Count: > 0 } wall)
                return new SubmitOutcome(false, false, page.Url, "", screenshot, unmapped, mapped,
                    BrowserSession.SocialWallNote(page.Url, wall));
            if (mapped.Count == 0)
                return new SubmitOutcome(false, false, page.Url, "", screenshot, unmapped, mapped,
                    AlreadyApplied().IsMatch(await BodyTextAsync(page.MainFrame))
                        ? "the board says this account already applied to this posting — nothing to send; mark it applied"
                            + (session.SignedInAs is { } acct ? $" (signed in at {acct.Host} as {acct.Username})" : "")
                    : await HasFillableControlsAsync(form)
                        ? "nothing on this form could be filled — the packet's questions match none of its fields"
                        : "no application form was found on the page — nothing to fill"
                          + (session.RevealNote.Length > 0 ? $" ({session.RevealNote})" : "")
                          + (session.SignedInAs is { } who ? $" (signed in at {who.Host} as {who.Username})" : ""));
            if (dryRun)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped, "");
            if (unmapped.Count > 0)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                    "refused to submit: required fields could not be mapped (" + string.Join(", ", unmapped) + ")");

            var submit = await FindSubmitAsync(form);
            if (submit is null)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped, "no Submit button found");
            // What the click actually sent. Greenhouse's form runs reCAPTCHA Enterprise first
            // and only then POSTs the application as JSON — to boards.greenhouse.io, a host the
            // page itself is not on — and a failed POST shows "There was an error processing
            // your application". Every non-GET response bar analytics is recorded, so a run
            // that ends without a confirmation can say whether an application was ever posted.
            var posts = new List<string>();
            var captchaRefused = false;
            var boardSaid = "";
            page.Response += (_, r) =>
            {
                var req = r.Request;
                if (req.Method is "GET" or "HEAD" || IsChatter(r.Url)) return;
                if (Uri.TryCreate(r.Url, UriKind.Absolute, out var u))
                    lock (posts) posts.Add($"{req.Method} {u.Host}{u.AbsolutePath} → {r.Status}");
                // The board's own verdict on the application. Greenhouse answers a submission
                // its invisible reCAPTCHA Enterprise scored as a bot's with
                // 428 {"code":"captcha-failed", "security_code_recipient": "<email>"} and asks
                // for the code it emailed — handled below. Without a recipient it is the plain
                // captcha refusal: the form asking for a person.
                if (r.Status >= 400)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var body = await r.TextAsync();
                            if (Regex.IsMatch(body, "captcha", RegexOptions.IgnoreCase)) captchaRefused = true;
                            var msg = Regex.Match(body, "\"message\"\\s*:\\s*\"([^\"]{1,200})\"");
                            if (msg.Success) boardSaid = msg.Groups[1].Value;
                        }
                        catch (PlaywrightException) { /* body gone with the page */ }
                    });
            };
            page.RequestFailed += (_, req) =>
            {
                if (req.Method is "GET" or "HEAD" || IsChatter(req.Url)) return;
                if (Uri.TryCreate(req.Url, UriKind.Absolute, out var u))
                    lock (posts) posts.Add($"{req.Method} {u.Host}{u.AbsolutePath} → failed ({req.Failure})");
            };
            var urlBefore = page.Url;
            await submit.ClickAsync();
            // Then WAIT for the form's verdict rather than reading the page in the quiet
            // moment before the captcha and the POST have even started. This used to wait for
            // network idle (which fires in exactly that moment), read the untouched form,
            // screenshot it, and tear the browser down — cancelling whatever was in flight.
            var text = "";
            Match m = Match.Empty;
            List<string> complaints = [];
            var confirmedDialog = false;
            async Task WaitForVerdictAsync(bool stopAtCodePrompt)
            {
                complaints = [];
                for (var i = 0; i < 30; i++)
                {
                    await page.WaitForTimeoutAsync(1000);
                    text = await BodyTextAsync(form);
                    m = Confirmation().Match(text);
                    if (m.Success) break;
                    // "Are you sure you want to apply?" — a board that asks once more before it
                    // sends. Answered once, and only inside a dialog the click opened (#216).
                    if (!confirmedDialog && i >= 1 && await ConfirmDialogAsync(page))
                    {
                        confirmedDialog = true;
                        continue;
                    }
                    if (stopAtCodePrompt && await HasSecurityCodePromptAsync(form)) break;
                    complaints = await ValidationErrorsAsync(form);
                    if (complaints.Count > 0) break;
                    if (page.Url != urlBefore && i >= 3) break; // navigated somewhere new: read it below
                }
            }
            await WaitForVerdictAsync(stopAtCodePrompt: true);

            // Phase two. The board emailed the candidate a security code and put its boxes on
            // the form: park here, ask for the code, type it, and click again. This is the
            // gate every production click ended at; the human relays one code and the run
            // finishes in the same session, which is the only session the code is good for.
            if (!m.Success && await HasSecurityCodePromptAsync(form))
            {
                var recipient = Regex.Match(text, @"sent to\s+([^\s,]+@[A-Za-z0-9.-]+[A-Za-z0-9])", RegexOptions.IgnoreCase) is { Success: true } rm
                    ? rm.Groups[1].Value : "";
                screenshot = await session.ScreenshotAsync();
                if (awaitSecurityCode is null)
                    return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                        $"the board emailed a security code to {(recipient.Length > 0 ? recipient : "the candidate")} and this run cannot take one — finish it with Copy answers and open");
                var code = await awaitSecurityCode(new CodeRequest(recipient), ct);
                if (string.IsNullOrWhiteSpace(code))
                    return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                        $"the board emailed a security code to {(recipient.Length > 0 ? recipient : "the candidate")} and none was entered in time — run Submit again and paste the code when it arrives");
                if (!await EnterSecurityCodeAsync(form, code.Trim()))
                    return new SubmitOutcome(true, false, page.Url, "", await session.ScreenshotAsync(), unmapped, mapped,
                        "the security code could not be entered — check the screenshot");
                var again = await FindSubmitAsync(form);
                if (again is null)
                    return new SubmitOutcome(true, false, page.Url, "", await session.ScreenshotAsync(), unmapped, mapped,
                        "the security code was entered but Submit did not become clickable — check the screenshot");
                captchaRefused = false; // the first verdict was the code request; judge the second afresh
                await again.ClickAsync();
                await WaitForVerdictAsync(stopAtCodePrompt: false);
            }
            screenshot = await session.ScreenshotAsync();
            string PostNote()
            {
                lock (posts) return posts.Count == 0 ? " (no application request was sent)" : $" ({string.Join("; ", posts)})";
            }
            if (!m.Success)
            {
                // A challenge thrown up BY the click is the common case, not the rare one: the
                // form looks clean right up to Submit and only then demands "click the object
                // that does not fit the column pattern". Checking only before the click filed
                // nine of these as mystery failures to retry forever, when every one of them
                // was a form asking for a person.
                if (await HasCaptchaAsync(page.MainFrame) || await HasCaptchaAsync(form))
                    return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                        "a captcha appeared on Submit — finish it with Copy answers and open",
                        Captcha: true);
                // The invisible kind, with no code offered: the board simply refused the
                // application as a bot's. Same handoff. Ashby says it in words — "Your
                // application submission was flagged as possible spam" — after a 200 from its
                // API, so the page text is the only tell.
                await page.WaitForTimeoutAsync(500); // let the response body reader finish
                var refusal = SpamRefusal().Match(text + "\n" + await BodyTextAsync(page.MainFrame));
                if (refusal.Success)
                    return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                        $"the board refused the application as a bot's (\"{refusal.Value}\")" + PostNote()
                        + " — finish it with Copy answers and open",
                        Captcha: true);
                if (captchaRefused && !await HasSecurityCodePromptAsync(form))
                    return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                        "the board refused the application as a bot's" + (boardSaid.Length > 0 ? $" (\"{boardSaid}\")" : "")
                        + PostNote() + " — finish it with Copy answers and open",
                        Captcha: true);
                // The form's own validation messages are the diagnosis; without them every
                // refused click reads as the same mystery and gets the same wrong theory.
                if (complaints.Count == 0) complaints = await ValidationErrorsAsync(form);
                if (complaints.Count > 0)
                    return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                        "the form rejected the submission: " + string.Join("; ", complaints) + PostNote());
                if (await FormResetAsync(form, packet, mapped))
                    return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                        "Submit was clicked and the form reset itself with no confirmation" + PostNote() + " — check the screenshot");
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                    "Submit was clicked but no confirmation text was recognised" + PostNote() + " — check the screenshot");
            }
            var start = Math.Max(0, m.Index - 80);
            var snippet = text.Substring(start, Math.Min(text.Length - start, 240)).Trim();
            return new SubmitOutcome(true, true, page.Url, snippet, screenshot, unmapped, mapped, "");
        }
        catch (PlaywrightException ex)
        {
            _log.LogWarning("browser run failed at {Url}: {Reason}", page.Url, ex.Message);
            screenshot ??= await session.ScreenshotAsync();
            var refused = session.Refused;
            var why = refused.Count > 0 ? $"navigation to {refused[0]} was refused" : ex.Message.Split('\n')[0];
            return new SubmitOutcome(mapped.Count > 0, false, page.Url, "", screenshot, unmapped, mapped, why);
        }
    }

    /// <summary>
    /// The frame the application form lives in: the top document when it has fillable
    /// controls, else the child frame that does — waiting up to ten seconds for either to
    /// render, because both the iframe embed and the fetched-after-idle form arrive late.
    /// Falls back to the top document, whose emptiness the caller then reports honestly.
    /// </summary>
    private static async Task<IFrame> FormFrameAsync(IPage page)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (await HasFillableControlsAsync(page.MainFrame)) return page.MainFrame;
            foreach (var child in page.Frames)
            {
                if (child == page.MainFrame) continue;
                if (await HasFillableControlsAsync(child)) return child;
            }
            if (DateTime.UtcNow >= deadline) return page.MainFrame;
            await page.WaitForTimeoutAsync(500);
        }
    }

    /// <summary>
    /// A form that asks for one name, not two: Ashby's <c>_systemfield_name</c>, Lever's
    /// <c>name</c>, any box labelled "Name" or "Full name". When the packet's first and last
    /// name both went unmapped and such a box is there, type them together and count both.
    /// </summary>
    private static async Task FillFullNameAsync(IFrame page, AgentPacket packet, List<string> mapped, List<string> unmapped)
    {
        var first = packet.Questions.FirstOrDefault(q => Unprefixed(q.Id) == "first_name");
        var last = packet.Questions.FirstOrDefault(q => Unprefixed(q.Id) == "last_name");
        if (first is null || last is null || mapped.Contains(first.Id) || mapped.Contains(last.Id)) return;
        packet.Answers.TryGetValue(first.Id, out var firstName);
        packet.Answers.TryGetValue(last.Id, out var lastName);
        var full = string.Join(' ', new[] { firstName ?? "", lastName ?? "" }.Where(n => n.Trim().Length > 0));
        if (full.Length == 0) return;
        var candidates = new[]
        {
            page.Locator("input[name='_systemfield_name'], input#_systemfield_name").First,
            page.GetByLabel(new Regex(@"^\s*(?:full |your )?name\s*\*?\s*$", RegexOptions.IgnoreCase)).First,
            page.Locator("input[name='name'], input#name, input[name='full_name'], input#full_name, input[name='fullName']").First,
            page.Locator("input[placeholder*='full name' i]").First,
        };
        foreach (var c in candidates)
        {
            try
            {
                if (await c.CountAsync() == 0 || !await c.IsVisibleAsync() || !await c.IsEditableAsync()) continue;
                await c.FillAsync(full);
                if ((await c.InputValueAsync()).Trim() != full) continue;
                mapped.Add(first.Id);
                mapped.Add(last.Id);
                unmapped.Remove(first.Id);
                unmapped.Remove(last.Id);
                return;
            }
            catch (PlaywrightException) { /* try the next */ }
            catch (TimeoutException) { /* try the next */ }
        }
    }

    /// <summary>The packet question a rendered field belongs to — by field id/name (exactly, or
    /// with the form's own prefix: <c>candidate-location</c> is the API's <c>location</c>, the
    /// same rule <see cref="LocateAsync"/> finds it by), else by label.</summary>
    private static PacketQuestion? FindQuestion(AgentPacket packet, string key, string label) =>
        packet.Questions.FirstOrDefault(x => Unprefixed(x.Id).Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? packet.Questions.FirstOrDefault(x => key.EndsWith("-" + Unprefixed(x.Id), StringComparison.OrdinalIgnoreCase))
        ?? (PacketQuestion.CleanLabel(label) is { Length: > 0 } clean
            ? packet.Questions.FirstOrDefault(x => PacketQuestion.CleanLabel(x.Label).Equals(clean, StringComparison.OrdinalIgnoreCase))
            : null);

    /// <summary>Find the control for a question — by accessible name first, then by the
    /// ATS field name — and set it. False when nothing visible matched.</summary>
    private static async Task<bool> FillAsync(IFrame page, PacketQuestion q, string answer)
    {
        // Radio groups and checkboxes are set, never typed into — Playwright refuses to
        // fill() them ("Input of type \"radio\" cannot be filled"), and discovery hands
        // both to us as Select, so this has to come before the combobox path below.
        if (await SetChoiceAsync(page, q, answer) is { } choice)
            return choice;

        var control = await LocateAsync(page, q);
        if (control is null) return false;
        var tag = (await control.EvaluateAsync<string>("el => el.tagName")).ToLowerInvariant();
        if (tag == "select")
        {
            try { await control.SelectOptionAsync(new SelectOptionValue { Label = answer }); return true; }
            catch (PlaywrightException) { return false; }
        }
        // A custom combobox — react-select and its kin — whether the packet calls the question
        // a select or (as discovery does for a combobox <input>) plain text. Judged by what the
        // control IS, not by what the packet says it is.
        if (q.Type is PacketQuestion.Select or PacketQuestion.MultiSelect || await IsComboboxAsync(control))
            return await ChooseAsync(page, control, q, answer);
        await control.FillAsync(answer);
        return true;
    }

    /// <summary>
    /// Drive a custom combobox the way a person does and <b>prove the value committed</b>:
    /// open it, type, wait for the widget's options, click the one that matches, then read the
    /// widget's own selected-value state. This used to type the answer, press Enter and
    /// return true unconditionally — react-select keeps whatever was typed as search text,
    /// commits nothing, and the form's validation then said "Select a country" to a run that
    /// had reported the field mapped. A question with a fixed option set only accepts an
    /// answer that is one of them; an open autocomplete (a city picker fed by a geocoder)
    /// takes the first suggestion for what was typed, which is what the human would click.
    /// </summary>
    private static async Task<bool> ChooseAsync(IFrame page, ILocator control, PacketQuestion q, string answer)
    {
        var fixedSet = q.Options.Count > 0;
        if (fixedSet && !q.Options.Any(o => Same(o, answer)))
            return false;
        var combobox = await IsComboboxAsync(control);
        ILocator? pick = null;
        var typed = answer;
        // An open autocomplete (a geocoded city picker) may find nothing for the full text
        // and everything for a shorter one: "Minden, Nebraska (Remote)" → "Minden, Nebraska"
        // → "Minden". Fixed option sets get one try with the answer as given.
        foreach (var query in fixedSet ? [answer] : AutocompleteQueries(answer))
        {
            typed = query;
            await control.ClickAsync();
            try { await control.FillAsync(query); }
            catch (PlaywrightException) { await page.Page.Keyboard.TypeAsync(query); } // a div-based widget: type at it
            if (!combobox) break;
            for (var i = 0; i < 12 && pick is null; i++)
            {
                await page.WaitForTimeoutAsync(250);
                pick = await BestOptionAsync(page, control, answer, fixedSet);
            }
            if (pick is not null) break;
        }
        // No menu ever showed: commit whatever is under the caret. Only on a combobox —
        // Enter in a plain text input submits the form it sits in.
        if (pick is null && combobox)
            await page.Page.Keyboard.PressAsync("Enter");
        if (pick is not null)
            await pick.ClickAsync();
        await page.WaitForTimeoutAsync(300);
        return await CommittedAsync(control, typed);
    }

    /// <summary>The answer, then the answer without its aside, then each comma-separated head of it.</summary>
    private static List<string> AutocompleteQueries(string answer)
    {
        var list = new List<string> { answer.Trim() };
        var clean = AnswerDrafter.StripLocationSuffix(answer);
        if (clean.Length > 0) list.Add(clean);
        var parts = clean.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var n = parts.Length - 1; n >= 1; n--)
            list.Add(string.Join(", ", parts[..n]));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The option to click for an answer, or null while the menu has nothing usable:
    /// an exact match, else the shortest option that starts with the answer, else one that
    /// contains it, else (open autocompletes only) the first suggestion.</summary>
    private static async Task<ILocator?> BestOptionAsync(IFrame page, ILocator control, string answer, bool fixedSet)
    {
        // Prefer the listbox the control says it owns (aria-controls, or SuccessFactors'
        // aria-owns); otherwise any option that is on screen.
        var owned = await control.GetAttributeAsync("aria-controls");
        if (string.IsNullOrWhiteSpace(owned)) owned = await control.GetAttributeAsync("aria-owns");
        // By attribute, never by "#id": SuccessFactors numbers its widgets ("298:_listSelect")
        // and an id selector cannot start with a digit, so the escaped form threw and the
        // menu was never read — the pinned answer was typed and never chosen (#222).
        var options = !string.IsNullOrWhiteSpace(owned)
            ? page.Locator($"[id={Quote(owned)}] [role=option]:visible")
            : page.Locator("[role=option]:visible");
        IReadOnlyList<string> texts;
        try { texts = await options.AllInnerTextsAsync(); }
        catch (PlaywrightException) { return null; }
        if (texts.Count == 0) return null;
        var want = answer.Trim();
        var norm = texts.Select(t => t.Replace('\n', ' ').Trim()).ToList();
        // The menu's own "No Selection" / "Select…" row is a placeholder, not a choice: never
        // the first suggestion, and never a match for an answer it merely contains.
        var real = norm.Select((t, n) => (t, n)).Where(x => !Placeholder().IsMatch(x.t)).ToList();
        var i = norm.FindIndex(t => t.Equals(want, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            var starts = real
                .Where(x => x.t.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.t.Length).ToList();
            if (starts.Count > 0) i = starts[0].n;
        }
        if (i < 0) i = real.FirstOrDefault(x => x.t.Contains(want, StringComparison.OrdinalIgnoreCase), (t: "", n: -1)).n;
        if (i < 0 && !fixedSet && real.Count > 0) i = real[0].n;
        return i < 0 ? null : options.Nth(i);
    }

    private static async Task<bool> IsComboboxAsync(ILocator control)
    {
        try
        {
            return await control.EvaluateAsync<bool>("""
                el => el.getAttribute('role') === 'combobox' || el.getAttribute('aria-autocomplete') === 'list'
                      || el.getAttribute('aria-haspopup') === 'listbox'
                """);
        }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>
    /// Did the widget take the value? Read what it renders, in order of certainty: the hidden
    /// <c>required</c> input react-select keeps beside the visible one (its value is the
    /// committed option), a selected-value element, a plain input that kept its text, or a
    /// whole line of the widget's text equal to the choice.
    /// </summary>
    private static async Task<bool> CommittedAsync(ILocator control, string chosen)
    {
        try
        {
            return await control.EvaluateAsync<bool>("""
                (el, chosen) => {
                  let root = el;
                  for (let i = 0; i < 6 && root.parentElement; i++) {
                    root = root.parentElement;
                    const hidden = [...root.querySelectorAll('input[required]')].find(h => h !== el && !h.getAttribute('role'));
                    if (hidden) return (hidden.value || '').trim().length > 0;
                    const sv = root.querySelector('[class*="single-value"], [class*="multi-value"]');
                    if (sv) return (sv.innerText || '').trim().length > 0;
                  }
                  if ((el.value || '').trim().length > 0) return true;
                  const want = chosen.trim().toLowerCase();
                  return (root.innerText || '').split('\n').map(s => s.trim().toLowerCase())
                    .some(l => l.length > 0 && (l === want || l.startsWith(want)));
                }
                """, chosen);
        }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>
    /// What the rendered form still marks required-and-empty, as (key, label) pairs — the
    /// control's id or name, and its accessible label. Native controls report their own
    /// value; a combobox reports through the hidden required input or selected-value element
    /// its widget keeps, and a widget that exposes neither is taken at its input's word.
    /// </summary>
    private static async Task<List<(string Key, string Label)>> RequiredEmptyAsync(IFrame page)
    {
        try
        {
            var raw = await page.EvaluateAsync<System.Text.Json.JsonElement>(RequiredEmptyScript);
            var list = new List<(string, string)>();
            foreach (var item in raw.EnumerateArray())
                list.Add((item.GetProperty("key").GetString() ?? "", item.GetProperty("label").GetString() ?? ""));
            return list;
        }
        catch (PlaywrightException) { return []; }
    }

    private static readonly string RequiredEmptyScript = """
        () => {
          const widget = __WIDGET__;
          const visible = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          const labelFor = el => {
            if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
            const by = el.getAttribute('aria-labelledby');
            if (by) { const t = by.split(/\s+/).map(i => document.getElementById(i)?.innerText || '').join(' ').trim(); if (t) return t; }
            if (el.id) { const l = document.querySelector(`label[for="${CSS.escape(el.id)}"]`); if (l) return l.innerText; }
            const wrap = el.closest('label'); if (wrap) return wrap.innerText;
            const legend = el.closest('fieldset')?.querySelector('legend'); if (legend) return legend.innerText;
            // The row's own label when the page never tied it to the control — the same rule
            // discovery names the field by, so the sweep and the packet agree on it. SuccessFactors'
            // Acknowledgement checkbox has a <label for=""> beside it and nothing else (#222).
            for (let a = el.parentElement, i = 0; a && a !== document.body && i < 12; a = a.parentElement, i++) {
              const controls = [...a.querySelectorAll('input, select, textarea')]
                .filter(c => !['hidden', 'submit', 'button', 'reset', 'image'].includes((c.getAttribute('type') || '').toLowerCase()) && (c === el || visible(c)));
              if (controls.length > 1) break;
              const l = [...a.querySelectorAll('label')].find(x => !x.contains(el) && /[A-Za-z]/.test(x.innerText || ''));
              if (l) return l.innerText.trim();
            }
            return el.getAttribute('placeholder') || '';
          };
          const out = []; const seen = new Set();
          const add = (key, label) => {
            // A required star at either end is decoration: "* Country" is Country (#222).
            label = (label || '').replace(/^\s*\*\s*/, '').replace(/\*\s*$/, '').replace(/\s+/g, ' ').trim();
            key = (key || label).trim();
            if (!key || seen.has(key)) return;
            seen.add(key); out.push({ key, label });
          };
          const comboFilled = cb => {
            let root = cb;
            for (let i = 0; i < 6 && root.parentElement; i++) {
              root = root.parentElement;
              const hidden = [...root.querySelectorAll('input[required]')].find(h => h !== cb && !h.getAttribute('role'));
              if (hidden) return (hidden.value || '').trim().length > 0;
              const sv = root.querySelector('[class*="single-value"], [class*="multi-value"]');
              if (sv) return (sv.innerText || '').trim().length > 0;
            }
            return (cb.value || '').trim().length > 0;
          };
          // A file field is filled when its input holds a file and the uploader has not
          // complained, when the field's own Enter manually box has text in it, or when the
          // widget is showing a file name. Judged over the whole field, not the input: an
          // uploader's error and its text twin both live beside the input, not on it.
          const uploadError = /cannot read propert|uploadfile|upload failed|failed to upload|error uploading/i;
          const fileFilled = root => {
            const text = root.innerText || '';
            if (uploadError.test(text)) return false;
            if ([...root.querySelectorAll('input[type=file]')].some(i => i.files && i.files.length > 0)) return true;
            if ([...root.querySelectorAll('textarea')].some(t => (t.value || '').trim().length > 0)) return true;
            return /\.(pdf|docx?|txt|rtf)\b/i.test(text);
          };
          const fileField = input => {
            let root = input;
            for (let i = 0; i < 6 && root.parentElement; i++) {
              root = root.parentElement;
              if (root.getAttribute('role') === 'group' || root.querySelector('textarea')) return root;
            }
            return input.parentElement || input;
          };
          const addFile = (root, input, label) => {
            if (!visible(root) || fileFilled(root)) return;
            add(input.id || input.getAttribute('name'), label);
          };
          // Greenhouse's shape: the required flag sits on the upload group, never on the
          // file input inside it — which is why the résumé's own "required" never reached
          // this check and the click went ahead into "Resume/CV is required" (#195).
          for (const g of document.querySelectorAll('[role=group][aria-required=true]')) {
            const input = g.querySelector('input[type=file]');
            if (input && !input.disabled) addFile(g, input, labelFor(g));
          }
          // A side form — join.com's "Apply later" box, a newsletter signup — is one whose
          // fillable controls are all email inputs while another form on the page has more
          // than that. Its required email is not the application's business (#210).
          const fillable = f => [...f.querySelectorAll('input, select, textarea')].filter(c => {
            const t = (c.getAttribute('type') || (c.tagName === 'SELECT' ? 'select' : 'text')).toLowerCase();
            return !['hidden', 'submit', 'button', 'reset', 'image'].includes(t) && !c.disabled;
          });
          const emailOnly = f => { const cs = fillable(f); return cs.length > 0 && cs.every(c => (c.getAttribute('type') || '').toLowerCase() === 'email'); };
          const realFormExists = [...document.querySelectorAll('form')].some(f => !emailOnly(f) && fillable(f).length > 0);
          const sideForm = el => { const f = el.closest('form'); return !!f && realFormExists && emailOnly(f); };
          // Ashby marks a question required on its TITLE LABEL (a generated _required_ class) and
          // nowhere on the control, for everything but plain text boxes. None of these reached the
          // loop below, so a form with three required radio questions blank, or its Location
          // typeahead empty, was clicked — Ashby answered 200, stayed on the form, and the run said
          // "Submit was clicked but no confirmation text was recognised" (actai, ElevenLabs, #280).
          for (const title of document.querySelectorAll('.ashby-application-form-question-title')) {
            if (!/(^|\s)_required_/.test(title.className.toString())) continue;
            const entry = title.closest('fieldset, [class*="_fieldEntry_"], .ashby-application-form-field-entry');
            if (!entry || !visible(entry) || entry.querySelector('input[required], textarea[required], select[required]')) continue;
            const choices = [...entry.querySelectorAll('input[type=radio], input[type=checkbox]')];
            const pressed = entry.querySelector('button[aria-pressed=true]');
            const typeahead = entry.querySelector('input[role=combobox]');
            const empty = entry.querySelector('button[data-option], button[aria-pressed]') ? !pressed
              : typeahead ? (typeahead.value || '').trim().length === 0
              : choices.length > 0 ? !choices.some(c => c.checked)
              : false;
            // Keyed by the title's `for` — the field's own id, the same on every load — never by
            // the radios' name, whose first half is this page load's and no other's.
            if (empty) add(title.getAttribute('for') || choices[0]?.getAttribute('name') || '', title.innerText);
          }
          for (const el of document.querySelectorAll('input, select, textarea')) {
            if (widget(el)) continue;   // a search or job-alert box is never a required field (#214)
            const type = (el.getAttribute('type') || (el.tagName === 'SELECT' ? 'select' : 'text')).toLowerCase();
            if (['hidden', 'submit', 'button', 'reset', 'image'].includes(type) || el.disabled) continue;
            if (!(el.required || el.getAttribute('aria-required') === 'true')) continue;
            if (sideForm(el)) continue;
            if (type === 'file') {
              // Not gated on the input being visible: uploaders keep theirs off screen behind
              // a styled Attach button.
              addFile(fileField(el), el, labelFor(el)); continue;
            }
            const combo = el.getAttribute('role') === 'combobox' || el.getAttribute('aria-autocomplete') === 'list';
            if (combo) {
              if (!visible(el) || comboFilled(el)) continue;
              add(el.id || el.getAttribute('name'), labelFor(el)); continue;
            }
            if (!el.id && !el.getAttribute('name')) {
              // react-select's hidden required input: the combobox beside it is the one to name.
              const owner = el.parentElement?.querySelector('[role=combobox]');
              if (!owner || (el.value || '').trim().length > 0) continue;
              add(owner.id || owner.getAttribute('name'), labelFor(owner)); continue;
            }
            if (!visible(el)) continue;
            let empty;
            if (type === 'checkbox') empty = !el.checked;
            else if (type === 'radio') {
              const name = el.getAttribute('name') || '';
              empty = ![...document.querySelectorAll(`input[type=radio][name="${CSS.escape(name)}"]`)].some(r => r.checked);
            }
            else empty = (el.value || '').trim().length === 0;
            if (empty) add(el.id || el.getAttribute('name'), labelFor(el));
          }
          return out;
        }
        """.Replace("__WIDGET__", BrowserSession.WidgetJs);

    /// <summary>Greenhouse's security-code prompt: one box per character, ids <c>security-input-N</c>,
    /// under "A verification code was sent to …".</summary>
    private static async Task<bool> HasSecurityCodePromptAsync(IFrame page)
    {
        try
        {
            var boxes = page.Locator("input[id^='security-input-']:visible");
            if (await boxes.CountAsync() > 0) return true;
            return Regex.IsMatch(await BodyTextAsync(page), @"(?:verification|security) code was sent to", RegexOptions.IgnoreCase)
                && await page.Locator("input[maxlength='1']:visible").CountAsync() >= 4;
        }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>Type the code into the first box; the widget advances a box per character. True
    /// when every box ended up holding a character of the code.</summary>
    private static async Task<bool> EnterSecurityCodeAsync(IFrame page, string code)
    {
        try
        {
            var boxes = page.Locator("input[id^='security-input-']:visible");
            if (await boxes.CountAsync() == 0) boxes = page.Locator("input[maxlength='1']:visible");
            var n = await boxes.CountAsync();
            if (n == 0) return false;
            await boxes.First.ClickAsync();
            await page.Page.Keyboard.TypeAsync(code, new() { Delay = 40 });
            await page.WaitForTimeoutAsync(400);
            // A widget that submits itself on the last character has moved on by now: the
            // boxes are gone, and that is the code taken, not a failure to type it (#221).
            if (await boxes.CountAsync() == 0) return true;
            var typed = "";
            for (var i = 0; i < n; i++) typed += await boxes.Nth(i).InputValueAsync();
            if (typed.Equals(code, StringComparison.OrdinalIgnoreCase)) return true;
            // A widget that does not advance on its own: one character per box, by hand.
            for (var i = 0; i < n && i < code.Length; i++)
                await boxes.Nth(i).FillAsync(code[i].ToString());
            await page.WaitForTimeoutAsync(400);
            typed = "";
            for (var i = 0; i < n; i++) typed += await boxes.Nth(i).InputValueAsync();
            return typed.Equals(code, StringComparison.OrdinalIgnoreCase);
        }
        catch (PlaywrightException) { return false; }
        catch (TimeoutException) { return false; }
    }

    private const string SignInError =
        "this page is an email sign-in, not the application form — the board wants the candidate signed in before it shows one";

    /// <summary>What the page did after Continue on the sign-in.</summary>
    private enum SignInStep
    {
        /// <summary>Nothing changed: the click did not land or the board ignored it.</summary>
        Nothing,
        /// <summary>The application form itself — the email was all the board wanted.</summary>
        Form,
        /// <summary>A box for the code the board emailed.</summary>
        Code,
        /// <summary>"Check your inbox": the board emailed a link and shows nothing to type into.</summary>
        Link,
    }

    // The button that moves a sign-in on: Continue, Next, Send code, Verify, Sign in — in the
    // languages the boards render in. Never an identity provider's ("Continue with Google").
    [GeneratedRegex(@"^\s*(?:continue|next|proceed|send(?: me)?(?: the| a| my)? (?:code|link|e-?mail)|get (?:the |a |my )?code|sign in|log ?in|submit|verify|confirm|weiter|fortfahren|absenden|bestätigen|anmelden|continuer|suivant|vérifier|valider|connexion|continuar|siguiente|enviar|verificar|confirmar)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContinueWords();

    [GeneratedRegex(@"google|linkedin|apple|microsoft|facebook|github|xing|indeed|passkey|\bsso\b", RegexOptions.IgnoreCase)]
    private static partial Regex IdentityProviders();

    // The board's word that the mail is on its way — with nothing on the page to type into,
    // it emailed a link.
    [GeneratedRegex(@"we(?:'ve| have)? (?:just )?(?:sent|e-?mailed)|check your (?:inbox|e-?mail|mail)|(?:link|code|e-?mail) (?:has been |was )?sent|sent (?:you )?(?:a|an|the) (?:link|e-?mail|code|message)|magic link|envoy[ée]|gesendet|geschickt|enviado|enviamos", RegexOptions.IgnoreCase)]
    private static partial Regex MailSent();

    /// <summary>A relayed reply that is the emailed link rather than a code.</summary>
    public static bool IsLink(string reply) => Regex.IsMatch(reply.Trim(), @"^https?://\S+$", RegexOptions.IgnoreCase);

    /// <summary>The address to sign in as: the packet's email answer, else any answer shaped like one.</summary>
    public static string CandidateEmail(AgentPacket packet)
    {
        static bool Shaped(string v) => Regex.IsMatch(v.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$");
        foreach (var q in packet.Questions)
        {
            if (q.Type == PacketQuestion.File) continue;
            var id = Unprefixed(q.Id);
            if (id != "email" && !id.EndsWith("_email", StringComparison.Ordinal)
                && !q.Label.Contains("email", StringComparison.OrdinalIgnoreCase) && !q.Label.Contains("e-mail", StringComparison.OrdinalIgnoreCase))
                continue;
            if (packet.Answers.TryGetValue(q.Id, out var v) && Shaped(v)) return v.Trim();
        }
        return packet.Answers.Values.FirstOrDefault(Shaped)?.Trim() ?? "";
    }

    /// <summary>
    /// Sign in by email on a board that shows its form only to a signed-in candidate (#210):
    /// type the candidate's address, press Continue, and park for what the board emailed —
    /// a code, typed into the box the page now shows, or a link, opened in this same session
    /// (only onto the board's own site). Null when the application form is on screen after
    /// it; otherwise the outcome that says why the run stopped, screenshot to be added by
    /// the caller. Without a relay (a dry run) it stops at once and says what a real Submit
    /// would do.
    /// </summary>
    private static async Task<SubmitOutcome?> SignInAsync(BrowserSession session, IFrame form, AgentPacket packet,
        Func<CodeRequest, CancellationToken, Task<string?>>? relay, CancellationToken ct, IReadOnlyList<BoardAccount>? accounts = null)
    {
        var page = session.Page;
        static SubmitOutcome Stop(string why) => new(false, false, "", "", null, [], [], why);
        // The address to sign in as: a board account saved for this host (one with no password
        // is exactly "sign me in by emailed code with this address", #216), else the packet's email.
        var host = Uri.TryCreate(page.Url, UriKind.Absolute, out var signInAt) ? signInAt.Host : "";
        var email = BoardAccount.For(accounts, host)?.Username is { Length: > 0 } saved && saved.Contains('@')
            ? saved : CandidateEmail(packet);
        if (relay is null)
            return Stop(SignInError + (email.Length > 0
                ? $" — Submit (not a dry run) signs in as {email} and asks you for the code or link the board emails"
                : "; apply via Copy answers and open the posting"));
        if (email.Length == 0)
            return Stop(SignInError + ", and the packet has no email address to sign in with");

        var box = form.Locator("input[type=email]:visible").First;
        var textBefore = await BodyTextAsync(form);
        var urlBefore = page.Url;
        try
        {
            await box.FillAsync(email, new() { Timeout = 5_000 });
            var go = await FindContinueAsync(form);
            if (go is not null) await go.ClickAsync(new() { Timeout = 5_000 });
            else await box.PressAsync("Enter");
        }
        catch (PlaywrightException ex) { return Stop(SignInError + "; signing in failed: " + ex.Message.Split('\n')[0]); }
        catch (TimeoutException) { return Stop(SignInError + "; the sign-in's Continue did not respond"); }

        var step = await AfterContinueAsync(page, textBefore, urlBefore);
        if (step == SignInStep.Form) return null;
        if (step == SignInStep.Nothing)
            return Stop(SignInError + "; Continue was pressed but the page did not change — check the screenshot");

        var reply = (await relay(new CodeRequest(email, SignIn: true), ct) ?? "").Trim();
        var what = step == SignInStep.Code ? "code" : "link";
        if (reply.Length == 0)
            return Stop($"the board emailed a sign-in {what} to {email} and none was relayed in time — run Submit again and paste the {what} when it arrives");
        if (IsLink(reply))
        {
            if (!Uri.TryCreate(reply, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
                || !Uri.TryCreate(urlBefore, UriKind.Absolute, out var here) || !BrowserSession.HostAllowed(u.Host, here.Host))
                return Stop($"the relayed sign-in link points off the board's site ({(Uri.TryCreate(reply, UriKind.Absolute, out var o) ? o.Host : reply)}) — the browser will not follow it");
            try { await page.GotoAsync(u.ToString(), new() { Timeout = 20_000, WaitUntil = WaitUntilState.DOMContentLoaded }); }
            catch (PlaywrightException ex) { return Stop("the sign-in link could not be opened: " + ex.Message.Split('\n')[0]); }
            catch (TimeoutException) { return Stop("the sign-in link did not load within 20 s"); }
            await BrowserSession.DismissConsentAsync(page);
        }
        else if (!await EnterSignInCodeAsync(page, reply))
            return Stop("the sign-in code could not be entered — if the email carries a link instead, run Submit again and paste that");

        // The form, or the posting again with the candidate now signed in and Apply to press.
        if (!await BrowserSession.WaitForApplicationFormAsync(page, 15_000))
        {
            await session.RevealFormAsync();
            page = session.Page;
            if (!await BrowserSession.WaitForApplicationFormAsync(page, 5_000))
            {
                var complaints = await ValidationErrorsAsync(page.MainFrame);
                return Stop($"the sign-in {what} was taken but no application form followed"
                    + (complaints.Count > 0 ? " (" + string.Join("; ", complaints) + ")" : "")
                    + (session.RevealNote.Length > 0 ? $" ({session.RevealNote})" : "") + " — check the screenshot");
            }
        }
        return null;
    }

    /// <summary>Watch the page for up to twelve seconds after Continue and say what it became.</summary>
    private static async Task<SignInStep> AfterContinueAsync(IPage page, string textBefore, string urlBefore)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        var changed = false;
        while (true)
        {
            foreach (var frame in page.Frames)
                if (await HasSignInCodeBoxAsync(frame)) return SignInStep.Code;
            if (await BrowserSession.ApplicationFormVisibleAsync(page)) return SignInStep.Form;
            var text = await BodyTextAsync(page.MainFrame);
            changed |= page.Url != urlBefore || text != textBefore;
            if (MailSent().IsMatch(text) && !await EmailBoxShownAsync(page))
                return SignInStep.Link;
            if (DateTime.UtcNow >= deadline)
                // Something happened and it was not a form or a code box: the board is telling
                // the candidate to look in their mailbox in words this does not know. Park.
                return changed && !await EmailBoxShownAsync(page) ? SignInStep.Link : SignInStep.Nothing;
            await page.WaitForTimeoutAsync(500);
        }
    }

    /// <summary>Is the sign-in's own email box still on screen? False mid-navigation too.</summary>
    private static async Task<bool> EmailBoxShownAsync(IPage page)
    {
        try { return await page.MainFrame.Locator("input[type=email]:visible").First.IsVisibleAsync(); }
        catch (PlaywrightException) { return false; }
    }

    private const string CodeBoxSelector =
        "input[autocomplete='one-time-code']:visible, input[inputmode='numeric']:visible, input[maxlength='1']:visible, "
        + "input[name*='code' i]:visible, input[id*='code' i]:visible, input[placeholder*='code' i]:visible, input[aria-label*='code' i]:visible, "
        + "input[name*='otp' i]:visible, input[id*='otp' i]:visible";

    internal static async Task<bool> HasSignInCodeBoxAsync(IFrame frame)
    {
        try { return await frame.Locator(CodeBoxSelector).CountAsync() > 0; }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>Type the relayed code — into one box per character, or the one box — and press
    /// on. A widget that submits itself on the last character is left to it.</summary>
    internal static async Task<bool> EnterSignInCodeAsync(IPage page, string code)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                if (!await HasSignInCodeBoxAsync(frame)) continue;
                var perCharacter = frame.Locator("input[maxlength='1']:visible");
                if (await perCharacter.CountAsync() >= 4)
                {
                    if (!await EnterSecurityCodeAsync(frame, code)) return false;
                }
                else
                {
                    var single = frame.Locator(CodeBoxSelector).First;
                    await single.FillAsync(code, new() { Timeout = 5_000 });
                }
                await frame.WaitForTimeoutAsync(600);
                var go = await FindContinueAsync(frame);
                if (go is not null)
                {
                    try { await go.ClickAsync(new() { Timeout = 5_000 }); }
                    catch (PlaywrightException) { /* the widget already moved on */ }
                    catch (TimeoutException) { /* ditto */ }
                }
                else
                {
                    try { await frame.Locator(CodeBoxSelector).Last.PressAsync("Enter", new() { Timeout = 2_000 }); }
                    catch (PlaywrightException) { /* gone: it submitted itself */ }
                    catch (TimeoutException) { /* ditto */ }
                }
                return true;
            }
            catch (PlaywrightException) { /* next frame */ }
            catch (TimeoutException) { return false; }
        }
        return false;
    }

    /// <summary>The sign-in's Continue / Verify / Send code button — never an identity
    /// provider's, never anything that reads as "later".</summary>
    private static async Task<ILocator?> FindContinueAsync(IFrame frame)
    {
        var notLater = new LocatorFilterOptions { HasNotTextRegex = BrowserSession.LaterWords() };
        var notIdp = new LocatorFilterOptions { HasNotTextRegex = IdentityProviders() };
        var candidates = new[]
        {
            frame.GetByRole(AriaRole.Button, new() { NameRegex = ContinueWords() }).Filter(notLater).Filter(notIdp).First,
            frame.Locator("form button[type=submit], form input[type=submit]").Filter(notLater).Filter(notIdp).First,
            frame.Locator("button[type=submit], input[type=submit]").Filter(notLater).Filter(notIdp).First,
        };
        foreach (var c in candidates)
        {
            try { if (await c.CountAsync() > 0 && await c.IsVisibleAsync() && await c.IsEnabledAsync()) return c; }
            catch (PlaywrightException) { /* next */ }
        }
        return null;
    }

    /// <summary>
    /// Does the form already hold the candidate's résumé — a file name under a résumé
    /// heading with the board's Download / Edit / Replace / Remove control beside it, and no
    /// empty file input waiting there? SuccessFactors keeps the document on the candidate's
    /// account and shows it on every application (#216).
    /// </summary>
    private static async Task<bool> ResumeOnFileAsync(IFrame page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                  const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
                  const heads = [...document.querySelectorAll('label, legend, h1, h2, h3, h4, h5, dt, th, span, div, p, strong, b')]
                    .filter(el => shown(el) && el.children.length <= 2 && /^\s*\*?\s*(?:upload (?:your )?|attach (?:your )?)?(?:resume|résumé|cv|curriculum vitae)\b/i.test((el.innerText || '').trim()));
                  for (const h of heads) {
                    let a = h;
                    for (let i = 0; i < 5 && a && a !== document.body; i++, a = a.parentElement) {
                      const t = (a.innerText || '');
                      if (!/\.(?:pdf|docx?|rtf|txt|odt)\b/i.test(t)) continue;
                      if (!/download|edit document|replace|remove|delete|last modified|uploaded/i.test(t)) continue;
                      const emptyFile = [...a.querySelectorAll('input[type=file]')].some(f => shown(f) && !f.files.length);
                      if (!emptyFile) return true;
                    }
                  }
                  return false;
                }
                """);
        }
        catch (PlaywrightException) { return false; }
    }

    [GeneratedRegex(@"^\s*(?:yes|ok|okay|confirm|continue|apply|submit|proceed|i agree|agree|accept|send)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ConfirmWords();

    /// <summary>A dialog the Submit click opened, asking once more: press its confirming
    /// button. True when one was pressed.</summary>
    private static async Task<bool> ConfirmDialogAsync(IPage page)
    {
        try
        {
            var dialogs = page.Locator("[role=dialog], [role=alertdialog], .modal, .ui-dialog, [class*='dialog' i]");
            var n = Math.Min(await dialogs.CountAsync(), 6);
            for (var i = 0; i < n; i++)
            {
                var d = dialogs.Nth(i);
                if (!await d.IsVisibleAsync()) continue;
                var btn = d.GetByRole(AriaRole.Button, new() { NameRegex = ConfirmWords() }).First;
                if (await btn.CountAsync() == 0 || !await btn.IsVisibleAsync()) continue;
                await btn.ClickAsync(new() { Timeout = 3_000 });
                return true;
            }
        }
        catch (PlaywrightException) { /* no dialog after all */ }
        catch (TimeoutException) { /* it closed itself */ }
        return false;
    }

    /// <summary>Does the page have anything a person could type into or pick from? The careers
    /// site's search and job-alert widgets do not count (#214).</summary>
    private static async Task<bool> HasFillableControlsAsync(IFrame page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(HasFillableScript);
        }
        catch (PlaywrightException) { return false; }
    }

    private static readonly string HasFillableScript = """
                () => {
                  const widget = __WIDGET__;
                  return [...document.querySelectorAll('input, select, textarea')].some(el => {
                    const type = (el.getAttribute('type') || 'text').toLowerCase();
                    if (['hidden', 'submit', 'button', 'reset', 'image', 'file'].includes(type) || el.disabled || widget(el)) return false;
                    const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
                  });
                }
                """.Replace("__WIDGET__", BrowserSession.WidgetJs);

    /// <summary>Every visible, enabled, fillable control on the page is an email box (and there is one).</summary>
    private static async Task<bool> OnlyEmailFieldsAsync(IFrame page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(OnlyEmailScript);
        }
        catch (PlaywrightException) { return false; }
    }

    private static readonly string OnlyEmailScript = """
                () => {
                  const widget = __WIDGET__;
                  const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
                  const live = [...document.querySelectorAll('input, select, textarea')].filter(el => {
                    const type = (el.getAttribute('type') || (el.tagName === 'SELECT' ? 'select' : 'text')).toLowerCase();
                    return !['hidden', 'submit', 'button', 'reset', 'image'].includes(type) && !el.disabled && !widget(el) && shown(el);
                  });
                  return live.length > 0 && live.every(el => (el.getAttribute('type') || '').toLowerCase() === 'email');
                }
                """.Replace("__WIDGET__", BrowserSession.WidgetJs);

    /// <summary>Every visible, enabled, fillable control on the page is a radio or a checkbox
    /// (and there is one): the shape of a pre-screening step, where the only thing to do is
    /// answer a question or two and press Continue. A text, email, select or file box means it
    /// is (part of) the form, not a gate, so it is left to the normal fill (#239).</summary>
    private static async Task<bool> OnlyChoiceFieldsAsync(IFrame page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(OnlyChoiceScript);
        }
        catch (PlaywrightException) { return false; }
    }

    private static readonly string OnlyChoiceScript = """
                () => {
                  const widget = __WIDGET__;
                  const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
                  const live = [...document.querySelectorAll('input, select, textarea')].filter(el => {
                    const type = (el.getAttribute('type') || (el.tagName === 'SELECT' ? 'select' : 'text')).toLowerCase();
                    return !['hidden', 'submit', 'button', 'reset', 'image'].includes(type) && !el.disabled && !widget(el) && shown(el);
                  });
                  return live.length > 0 && live.every(el => { const t = (el.getAttribute('type') || '').toLowerCase(); return t === 'radio' || t === 'checkbox'; });
                }
                """.Replace("__WIDGET__", BrowserSession.WidgetJs);

    /// <summary>The frame that is a pre-screening step — its visible fillable controls are all
    /// radio/checkbox and it carries a Continue/Next button — or null when no frame is (#239).</summary>
    private static async Task<IFrame?> PreScreenFrameAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            if (!await OnlyChoiceFieldsAsync(frame)) continue;
            if (await FindContinueAsync(frame) is not null) return frame;
        }
        return null;
    }

    /// <summary>Is the page a pre-screening step (a question and Continue before the form)?
    /// Read by the reveal so a page it finds no form and no Apply on is named for what it is
    /// rather than "no Apply button or link on the page" (#239).</summary>
    internal static async Task<bool> IsPreScreenAsync(IPage page) => await PreScreenFrameAsync(page) is not null;

    /// <summary>
    /// Some boards gate the application behind a pre-screening step: a page whose only controls
    /// are radio/checkbox questions and a Continue button — Paycor's SubmitResume asks "Will you
    /// now or in the future require sponsorship?" before it shows the form. The reveal found no
    /// form and no Apply and the run dead-ended at "no Apply button or link on the page" (#239).
    /// Read the step's questions, answer each from <paramref name="answer"/> (the packet's own
    /// answers on a submit; the deterministic set during discovery), press Continue, and go round
    /// again until the form is on screen — bounded, since a step whose required question has no
    /// answer stops rather than pressing Continue into validation or past the form. Returns the
    /// questions it answered and advanced past, so a submit counts them mapped and a discovered
    /// packet carries them.
    /// </summary>
    public static async Task<List<PacketQuestion>> AdvancePreScreenAsync(
        IPage page, Func<PacketQuestion, string?> answer, ILogger log, int maxSteps = 5)
    {
        var advanced = new List<PacketQuestion>();
        for (var step = 0; step < maxSteps; step++)
        {
            if (await BrowserSession.ApplicationFormVisibleAsync(page)) break;
            var frame = await PreScreenFrameAsync(page);
            if (frame is null) break;                    // not (or no longer) a pre-screening step
            var questions = await FormDiscoverer.ReadQuestionsAsync(page, log);
            var toSet = new List<(PacketQuestion Q, string A)>();
            foreach (var q in questions)
            {
                if (q.Kind == PacketQuestion.Eeo || q.Type == PacketQuestion.File) continue;
                var a = answer(q);
                if (!string.IsNullOrWhiteSpace(a)) { toSet.Add((q, a!)); continue; }
                if (q.Required)
                {
                    // A required question this step cannot answer: pressing Continue would fail
                    // the step's own validation, and a step that let it through would carry us
                    // past a form we never filled. Stop and let the caller report it — the
                    // screenshot shows the step, and the reveal has named it.
                    log.LogInformation("pre-screen: no answer for required \"{Label}\" — not advancing", q.Label);
                    return advanced;
                }
            }
            foreach (var (q, a) in toSet)
                if (await FillAsync(frame, q, a)) advanced.Add(q);
            var go = await FindContinueAsync(frame);
            if (go is null) break;
            var textBefore = await BodyTextAsync(page.MainFrame);
            var urlBefore = page.Url;
            try { await go.ClickAsync(new() { Timeout = 5_000 }); }
            catch (PlaywrightException) { break; }
            catch (TimeoutException) { break; }
            // Wait for the step to give way to the form or the next step, then dismiss any
            // consent banner the new page brought with it before it is read again.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (await BrowserSession.ApplicationFormVisibleAsync(page)) break;
                if (page.Url != urlBefore) break;
                if (await BodyTextAsync(page.MainFrame) != textBefore) break;
                await page.WaitForTimeoutAsync(400);
            }
            await BrowserSession.DismissConsentAsync(page);
        }
        return advanced;
    }

    /// <summary>
    /// The validation messages the form is showing: alerts, the descriptions of invalid
    /// controls, and error-styled helper text. Short leaf text only — never a wrapper that
    /// contains a control — so what comes back reads like "Select a country", not the form.
    /// </summary>
    private static async Task<List<string>> ValidationErrorsAsync(IFrame page)
    {
        try
        {
            var raw = await page.EvaluateAsync<string[]>("""
                () => {
                  const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
                  const texts = new Set();
                  const sel = '[role=alert], [aria-invalid=true], [class*="error" i], [class*="invalid" i], [id$="-error"], [id$="_error"]';
                  for (const el of document.querySelectorAll(sel)) {
                    if (!shown(el) || el.tagName === 'LABEL') continue;
                    let t = (el.innerText || '').trim();
                    if (el.matches('input, select, textarea'))
                      t = (el.getAttribute('aria-describedby') || '').split(/\s+/)
                            .map(i => document.getElementById(i)?.innerText || '').join(' ').trim();
                    else if (el.querySelector('input, select, textarea, button')) continue;
                    t = t.replace(/\s+/g, ' ');
                    if (t.length > 0 && t.length <= 160) texts.add(t);
                  }
                  return [...texts].slice(0, 12);
                }
                """);
            return raw.ToList();
        }
        catch (PlaywrightException) { return []; }
    }

    /// <summary>After the click, is a text field we filled empty again? A form that re-mounts
    /// itself instead of submitting looks exactly like this.</summary>
    private static async Task<bool> FormResetAsync(IFrame page, AgentPacket packet, List<string> mapped)
    {
        foreach (var q in packet.Questions.Where(q => q.Type == PacketQuestion.Text && mapped.Contains(q.Id)))
        {
            try
            {
                var control = await LocateAsync(page, q);
                if (control is null) continue;
                return (await control.InputValueAsync()).Trim().Length == 0;
            }
            catch (PlaywrightException) { return false; }
        }
        return false;
    }

    /// <summary>
    /// Set a radio group or a checkbox: check the member whose value or label matches
    /// the answer. Null when the question is not one of those — the caller carries on
    /// with the text/select paths. False when it is one and nothing matched, so a
    /// required question still lands in <c>unmapped</c> rather than passing silently.
    /// </summary>
    private static async Task<bool?> SetChoiceAsync(IFrame page, PacketQuestion q, string answer)
    {
        var id = Unprefixed(q.Id);
        // Ashby names a radio group "<form instance>_<field>", and the first half is minted afresh
        // on every page load: a packet built on one load looked for a name the next load no longer
        // had, and the question stayed blank however good its answer (actai, #280). Only the field
        // half is the question's. Matched by it — whether the packet holds the whole stale name or,
        // as discovery now records, the field half alone.
        var field = AshbyField().Match(id) is { Success: true } m ? m.Groups[1].Value : id;
        var radios = page.Locator($"input[type=radio][name='{id}'], input[type=radio][name$='_{field}']");
        int count;
        try { count = await radios.CountAsync(); }
        catch (PlaywrightException) { return null; }
        if (count > 0)
        {
            // Exactly first; then loosely. An option can run to a sentence ("Professional/Fluent:
            // Able to lead technical discussions, write documentation…") and the answer drafted for
            // it is often its opening words — which matched nothing, and left actai's last
            // required question blank (#280). Loose never wins over an exact match elsewhere.
            for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < count; i++)
            {
                var radio = radios.Nth(i);
                try
                {
                    var value = await radio.GetAttributeAsync("value") ?? "";
                    var text = await ChoiceLabelAsync(radio);
                    if (pass == 0 ? !Same(value, answer) && !Same(text, answer) : !Loosely(text, answer))
                        continue;
                    // Bounded, and a timeout caught: it is a System.TimeoutException, not a
                    // PlaywrightException, and a drawn radio (Ashby keeps the real input out of
                    // sight behind a span) would otherwise take its full 20 s and the run with it.
                    try { await radio.CheckAsync(new() { Timeout = 3_000 }); return true; }
                    catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
                    {
                        // Pressed the way a person presses it: by its label.
                        if (await radio.EvaluateAsync<bool>(
                                "el => { const l = (el.id && el.getRootNode().querySelector(`label[for=\"${CSS.escape(el.id)}\"]`)) || el.closest('label'); (l || el).click(); return el.checked; }"))
                            return true;
                    }
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { /* try the next member */ }
            }
            return false;
        }

        var boxSelector = $"input[type=checkbox][id={Quote(id)}], input[type=checkbox][name={Quote(id)}]";
        var box = page.Locator(boxSelector).First;
        var want = Affirmative(answer);
        try
        {
            if (await box.CountAsync() == 0) return null;
            // Ashby's yes/no is two buttons (data-option, aria-pressed) and a hidden checkbox that
            // only mirrors them. Unchecked already "matched" an answer of No, so the question was
            // reported answered with nothing pressed — and gravie's real submit came back "the form
            // rejected the submission: Will you now, or in the future, require … sponsorship?" (#280).
            // Press the button a person presses, and believe only its own aria-pressed.
            var option = want ? "yes" : "no";
            var group = await box.EvaluateAsync<bool>(
                "el => !!el.parentElement && el.parentElement.querySelectorAll('button[data-option], button[aria-pressed]').length >= 2");
            if (group)
            {
                const string Find = "(el, o) => [...el.parentElement.querySelectorAll('button[data-option], button[aria-pressed]')]"
                    + ".find(b => ((b.dataset.option || b.innerText || '').trim().toLowerCase()) === o)";
                if (!await box.EvaluateAsync<bool>($"(el, o) => {{ const b = ({Find})(el, o); if (!b) return false; if (b.getAttribute('aria-pressed') !== 'true') b.click(); return true; }}", option))
                    return false;
                await page.WaitForTimeoutAsync(200);
                return await page.Locator(boxSelector).First.EvaluateAsync<bool>(
                    $"(el, o) => {{ const b = ({Find})(el, o); return !!b && b.getAttribute('aria-pressed') === 'true'; }}", option);
            }
            await box.SetCheckedAsync(want, new() { Timeout = 3_000 });
            return true;
        }
        // A timeout is a System.TimeoutException, not a PlaywrightException: caught by neither,
        // it ended the whole run. Ashby draws its own box and hides the real input
        // (tabindex="-1", no size), so Playwright waited its full 20 s for it to be "visible"
        // and gravie died four times on one optional checkbox (#280).
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // A widget that re-renders its box on click (SuccessFactors' Acknowledgement) leaves
            // Playwright reading the detached one and reporting the click "did not change its
            // state" — the box on the page is what counts. Read it fresh, and press once more
            // by hand if it still disagrees (#222).
            try
            {
                var fresh = page.Locator(boxSelector).First;
                if (await fresh.CountAsync() == 0) return false;
                if (await fresh.IsCheckedAsync() == want) return true;
                try { await fresh.ClickAsync(new() { Force = true, Timeout = 3_000 }); }
                catch (Exception inner) when (inner is PlaywrightException or TimeoutException) { /* a box with no size cannot be clicked at all */ }
                await page.WaitForTimeoutAsync(300);
                fresh = page.Locator(boxSelector).First;
                if (await fresh.IsCheckedAsync() == want) return true;
                // A hidden input behind a drawn box: press what a person presses — its label —
                // and failing that the input itself, as a click event rather than a pointer.
                await fresh.EvaluateAsync("""
                    el => {
                      const label = el.closest('label') || (el.id && document.querySelector(`label[for="${CSS.escape(el.id)}"]`));
                      (label || el).click();
                    }
                    """);
                await page.WaitForTimeoutAsync(300);
                return await page.Locator(boxSelector).First.IsCheckedAsync() == want;
            }
            catch (Exception inner) when (inner is PlaywrightException or TimeoutException) { return false; }
        }
    }

    /// <summary>The visible text tied to one radio/checkbox — its own label, or the one wrapping it.</summary>
    private static Task<string> ChoiceLabelAsync(ILocator choice) => choice.EvaluateAsync<string>("""
        el => (el.id && document.querySelector(`label[for="${CSS.escape(el.id)}"]`)?.innerText)
              || el.closest('label')?.innerText || ''
        """);

    /// <summary>An option and an answer that are the same choice said at different lengths: one
    /// opens with the other. Four characters at least, so "No" never opens "None of the above".</summary>
    private static bool Loosely(string option, string answer)
    {
        var o = option.Trim(); var a = answer.Trim();
        if (o.Length < 4 || a.Length < 4) return false;
        return o.StartsWith(a, StringComparison.OrdinalIgnoreCase) || a.StartsWith(o, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f-]{27}_([0-9a-f]{8}-[0-9a-f-]{27})$", RegexOptions.IgnoreCase)]
    private static partial Regex AshbyField();

    private static bool Same(string a, string b) =>
        a.Trim().Length > 0 && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Affirmative(string answer) =>
        answer.Trim() is not ("" or "0") && !answer.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(answer.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    private static string Unprefixed(string id) =>
        id.StartsWith("std:", StringComparison.Ordinal) ? id[4..] : id;

    /// <summary>
    /// The control for a question. The <b>exact</b> label first (the label text, allowing
    /// the required asterisk and case), then the field id/name, then a label that merely
    /// contains the question's text, then bare text above the box. Substring-first was
    /// how "LinkedIn profile" (required) matched "LinkedIn Profile URL" (optional) on a
    /// Comeet form: the URL box was filled twice and the required one stayed empty (#187).
    /// When one locator matches several controls, the one not yet filled is preferred.
    /// </summary>
    private static async Task<ILocator?> LocateAsync(IFrame page, PacketQuestion q)
    {
        var id = Unprefixed(q.Id);
        // The label as a person reads it: SuccessFactors writes "*\u00a0Country" and the packet
        // kept the star, so no label ever matched and every field lived or died by its id —
        // which the board renumbers per render (#222).
        var label = PacketQuestion.CleanLabel(q.Label);
        var candidates = new List<ILocator>();
        if (label.Length > 0)
            candidates.Add(page.GetByLabel(new Regex(@"^\s*\*?\s*" + Regex.Escape(label).Replace(@"\ ", @"\s+") + @"\s*\*?\s*$", RegexOptions.IgnoreCase)));
        candidates.Add(page.Locator($"[id={Quote(id)}]"));
        candidates.Add(page.Locator($"[id$={Quote("-" + id)}]"));
        candidates.Add(page.Locator($"[name={Quote(id)}]"));
        candidates.Add(page.Locator($"[name$={Quote("[" + id + "]")}]"));
        candidates.Add(page.Locator($"[name*={Quote(id)}]"));
        if (label.Length > 0)
            candidates.Add(page.GetByLabel(label, new() { Exact = false }));
        // Ashby ties a title to its control with a `for` that matches nothing when the control is a
        // typeahead: no id, no name on the input, and a description paragraph sitting between the
        // two so the nearest preceding text is the hint, not the question. ElevenLabs' required
        // Location stayed empty for it. Found through the entry its title sits in (#280).
        if (label.Length > 0)
            candidates.Add(page.Locator("fieldset, [class*='_fieldEntry_'], .ashby-application-form-field-entry")
                .Filter(new()
                {
                    Has = page.Locator(".ashby-application-form-question-title", new()
                    {
                        HasTextRegex = new Regex(@"^\s*" + Regex.Escape(label).Replace(@"\ ", @"\s+") + @"\s*\*?\s*$", RegexOptions.IgnoreCase),
                    }),
                })
                .Locator("input:not([type=hidden]):not([type=file]):not([type=radio]):not([type=checkbox]), textarea, select"));
        // Last: a label that is nothing but text above the box — no <label for>, no aria —
        // which is how a good part of the long tail writes its forms. The control whose
        // nearest preceding text starts with the question's label.
        if (label.Length > 0)
            candidates.Add(page.Locator(
                "xpath=//*[self::input or self::textarea or self::select][not(@type='hidden')]"
                + "[preceding::*[normalize-space(text())!=''][1][starts-with(translate(normalize-space(.), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), "
                + $"{XPathLiteral(label.ToLowerInvariant())})]]"));
        foreach (var c in candidates)
        {
            int count;
            try { count = Math.Min(await c.CountAsync(), 6); }
            catch (PlaywrightException) { continue; }
            ILocator? usable = null;
            for (var i = 0; i < count; i++)
            {
                var nth = c.Nth(i);
                // Judged one at a time: a match that is not a form control (the picker's own
                // button carries the same aria-label as its input on SuccessFactors) throws on
                // "is it editable", and used to take the input found before it down with it.
                try
                {
                    if (!await nth.IsVisibleAsync() || !await nth.IsEditableAsync()) continue;
                    usable ??= nth;
                    if (await IsEmptyAsync(nth)) return nth;
                }
                catch (PlaywrightException) { /* not a control; the next match */ }
            }
            if (usable is not null) return usable;
        }
        return null;
    }

    /// <summary>Is the question's control a select or combobox that already holds a value?</summary>
    private static async Task<bool> PrefilledPickerAsync(IFrame page, PacketQuestion q)
    {
        try
        {
            var control = await LocateAsync(page, q);
            if (control is null || await IsEmptyAsync(control)) return false;
            var tag = (await control.EvaluateAsync<string>("el => el.tagName")).ToLowerInvariant();
            if (tag == "select")
                return await control.EvaluateAsync<bool>("el => el.selectedIndex > 0 || (el.value || '').trim().length > 0");
            return await IsComboboxAsync(control);
        }
        catch (PlaywrightException) { return false; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>Nothing typed or chosen in the control yet. A widget with no readable value
    /// (a div-based combobox) counts as empty — it is at least not something we filled.</summary>
    private static async Task<bool> IsEmptyAsync(ILocator control)
    {
        try { return (await control.InputValueAsync(new() { Timeout = 1_000 })).Trim().Length == 0; }
        catch (PlaywrightException) { return true; }
        catch (TimeoutException) { return true; }
    }

    /// <summary>
    /// Attach the résumé and <b>prove it took</b>. Handing the bytes to a file input is not
    /// the same as the board accepting them: Greenhouse's current form takes the files, its
    /// own uploader then throws
    /// <c>Cannot read properties of undefined (reading 'uploadFile')</c>, and nothing is
    /// uploaded — while Playwright reported success, so the run went on to click Submit into
    /// a validation wall it could not see, and the packet looked clean the whole way. Every
    /// attach is now confirmed against the page before it counts as mapped; an unconfirmed
    /// one returns false, which puts a required résumé in <c>unmapped</c> and refuses the
    /// click. Better a refused submission than an application sent with no résumé on it.
    /// </summary>
    private static async Task<bool> AttachResumeAsync(
        IFrame page, PacketQuestion q, (byte[] Bytes, string Name) pdf, string resumeText)
    {
        var payload = new FilePayload { Name = pdf.Name, MimeType = "application/pdf", Buffer = pdf.Bytes };
        var id = Unprefixed(q.Id);
        var candidates = new ILocator?[]
        {
            page.Locator($"input[type=file][id={Quote(id)}]").First,
            page.Locator($"input[type=file][name*='{id}']").First,
            page.Locator("input[type=file][name*='resume' i], input[type=file][id*='resume' i]").First,
            // Only when the form has exactly one file input can "the file input" mean the
            // résumé's. A form with two means the second is the cover letter, and blindly
            // taking the first-or-any is how a résumé ended up attached as GitLab's cover
            // letter — visible in the evidence screenshot as "resume.pdf" under Cover Letter.
            await OnlyFileInputAsync(page),
        };
        foreach (var c in candidates)
        {
            if (c is null) continue;
            try
            {
                if (await c.CountAsync() == 0) continue;
                // Never touches disk: the bytes go straight into the input.
                await c.SetInputFilesAsync(payload);
                if (await ResumeTookAsync(page, pdf.Name)) return true;
            }
            catch (PlaywrightException) { /* try the next */ }
        }
        // The file would not go. Boards that refuse it almost always offer to take the résumé
        // as text instead, right beside the Attach control — so use their own escape hatch
        // rather than giving up on the posting.
        await ClearStagedResumeAsync(page);
        return await EnterResumeManuallyAsync(page, resumeText);
    }

    /// <summary>
    /// Take the rejected file back out of the uploader. It matters: an uploader that is holding
    /// a file shows that file and a remove control <b>instead of</b> its Attach and Enter
    /// manually buttons, so leaving the failed upload staged hides the very escape hatch we
    /// need next. Best effort — a board with nothing to clear is left exactly as it was.
    /// </summary>
    private static async Task ClearStagedResumeAsync(IFrame page)
    {
        try
        {
            // Not a bare "clear": a form's country selector carries a "Clear search" button, and
            // on GitLab's Greenhouse form that one sorts first and swallowed the click while the
            // rejected résumé stayed exactly where it was. The real control is "Remove file".
            var remove = page.GetByRole(AriaRole.Button,
                new() { NameRegex = new Regex(@"remove|discard|delete", RegexOptions.IgnoreCase) }).First;
            if (await remove.CountAsync() > 0 && await remove.IsVisibleAsync())
            {
                await remove.ClickAsync(new() { Timeout = 5_000 });
                await page.WaitForTimeoutAsync(500);
            }
            // Whatever the widget did with its own list, the input itself must let go too.
            // Short timeout and a catch for both failure shapes: an input the form keeps hidden
            // never becomes actionable, and Playwright reports that as System.TimeoutException,
            // which is NOT a PlaywrightException — so catching only the latter let a 20s timeout
            // escape and take the whole submission down with it.
            foreach (var input in await page.Locator("input[type=file]").AllAsync())
            {
                try { await input.SetInputFilesAsync(Array.Empty<FilePayload>(), new() { Timeout = 2_000 }); }
                catch (PlaywrightException) { /* not all inputs accept being emptied */ }
                catch (TimeoutException) { /* nor is every one of them actionable */ }
            }
            await page.WaitForTimeoutAsync(300);
        }
        catch (PlaywrightException) { /* nothing to clear */ }
        catch (TimeoutException) { /* nothing to clear */ }
    }

    /// <summary>The page's single file input, or null when there is none or more than one.</summary>
    private static async Task<ILocator?> OnlyFileInputAsync(IFrame page)
    {
        try
        {
            var all = page.Locator("input[type=file]");
            return await all.CountAsync() == 1 ? all.First : null;
        }
        catch (PlaywrightException) { return null; }
    }

    /// <summary>
    /// The board's own "Enter manually" box: click it and paste the résumé text. This is the
    /// path that works where the uploader will not take a file, and it needs no file chooser —
    /// which matters, because a chooser cannot be driven over the remote browser connection.
    /// </summary>
    private static async Task<bool> EnterResumeManuallyAsync(IFrame page, string resumeText)
    {
        if (string.IsNullOrWhiteSpace(resumeText)) return false;
        var manually = new Regex(@"enter manually|paste|type it|enter text|manual", RegexOptions.IgnoreCase);
        try
        {
            var trigger = page.GetByRole(AriaRole.Button, new() { NameRegex = manually }).First;
            if (await trigger.CountAsync() == 0 || !await trigger.IsVisibleAsync())
            {
                trigger = page.GetByRole(AriaRole.Link, new() { NameRegex = manually }).First;
                if (await trigger.CountAsync() == 0 || !await trigger.IsVisibleAsync()) return false;
            }
            // Wait for the box rather than looking for it straight away: the widget renders it
            // after the click, and checking immediately finds nothing and gives up on a board
            // that was about to work. (On GitLab's form it is textarea#resume_text, which
            // carries no name attribute — so match on id as well.)
            var box = page.Locator("textarea[name*='resume' i], textarea[id*='resume' i]").First;
            if (!await RevealBoxAsync(trigger, box))
            {
                box = page.Locator("textarea:visible").First;
                if (await box.CountAsync() == 0) return false;
            }

            await box.FillAsync(resumeText, new() { Timeout = 10_000 });
            return (await box.InputValueAsync()).Length > 0;
        }
        catch (PlaywrightException) { return false; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>
    /// The cover letter section's own "Enter manually" box — scoped to that section, never the
    /// résumé's (the first such button on the page is the résumé's, and taking it is how a
    /// letter would land in the wrong box). Found from the section's label forward.
    /// </summary>
    private static async Task<bool> EnterCoverLetterAsync(IFrame page, string letter)
    {
        try
        {
            var trigger = page.Locator(
                "xpath=(//*[self::label or self::span or self::div or self::legend or self::h3 or self::h4]"
                + "[starts-with(normalize-space(translate(text(), 'COVER LETTER', 'cover letter')), 'cover letter')])[1]"
                + "/following::*[(self::button or self::a) and contains(translate(normalize-space(.), 'ENTER MANUALY', 'enter manualy'), 'enter manually')][1]").First;
            if (await trigger.CountAsync() == 0 || !await trigger.IsVisibleAsync()) return false;
            var box = page.Locator("textarea[name*='cover' i], textarea[id*='cover' i]").First;
            if (!await RevealBoxAsync(trigger, box)) return false;
            await box.FillAsync(letter, new() { Timeout = 10_000 });
            return (await box.InputValueAsync()).Length > 0;
        }
        catch (PlaywrightException) { return false; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>
    /// Click an "Enter manually" trigger and wait for its box; click once more if the box did
    /// not come. On GitLab's form the cover letter's click landed while the résumé uploader
    /// beside it was mid-failure and re-rendering, and the box never opened (#195 — the
    /// evidence screenshot shows Attach and Enter manually still standing, no textarea). A
    /// second click after the widget has settled is cheap, and it is what a person would do.
    /// </summary>
    private static async Task<bool> RevealBoxAsync(ILocator trigger, ILocator box)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await trigger.ClickAsync(new() { Timeout = 5_000 });
                await box.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 4_000 });
                return true;
            }
            catch (TimeoutException) { /* the box did not come — once more */ }
            catch (PlaywrightException) { /* the trigger went away under the click */ }
        }
        return false;
    }

    /// <summary>
    /// Did the attachment actually register? True when a file input holds the file or the page
    /// now names it, and no uploader error is on screen. The uploader is asynchronous, so give
    /// it a moment before deciding.
    /// </summary>
    private static async Task<bool> ResumeTookAsync(IFrame page, string fileName)
    {
        // Poll rather than sleep once. The input reports its file immediately while the board's
        // uploader is still working, so a single early look says "attached" and a later one says
        // "error" — which is exactly how a dry run came back clean and the real run that followed
        // it refused. Wait for whichever lands first, and treat neither as failure.
        // And once it says "attached", keep watching: on GitLab's form the input held the file
        // and the page was quiet for well over a second before the uploader threw, so a look
        // that stopped at 1.5 s counted a résumé that never went up (#195). "Attached" now
        // has to hold for a further 2.5 s with no error before it is believed.
        try
        {
            var attachedAt = -1;
            for (var i = 0; i < 16; i++)
            {
                await page.WaitForTimeoutAsync(500);
                var verdict = await page.EvaluateAsync<string>("""
                    name => {
                      const text = document.body.innerText || '';
                      if (/cannot read propert|uploadfile|upload failed|failed to upload|error uploading/i.test(text))
                        return 'error';
                      const inputs = [...document.querySelectorAll('input[type=file]')];
                      if (inputs.some(i => i.files && i.files.length > 0)) return 'attached';
                      return text.includes(name) ? 'attached' : 'pending';
                    }
                    """, fileName);
                if (verdict == "error") return false;
                if (verdict != "attached") continue;
                if (attachedAt < 0) attachedAt = i;
                // Only trust "attached" once the uploader has had a chance to disagree.
                if (i >= Math.Max(attachedAt, 2) + 5) return true;
            }
            return false;
        }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>
    /// An interactive captcha standing between the filled form and Submit. Deliberately narrow:
    /// the invisible reCAPTCHA v3 badge rides along on a great many boards and never stops a
    /// submission, so anything inside <c>.grecaptcha-badge</c> must NOT count — only a challenge
    /// a person has to touch (the v2 checkbox, hCaptcha, Turnstile).
    /// </summary>
    private static async Task<bool> HasCaptchaAsync(IFrame page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                  const shown = el => {
                    if (!el || el.closest('.grecaptcha-badge')) return false;
                    const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                    return r.width > 40 && r.height > 40 && s.visibility !== 'hidden' && s.display !== 'none';
                  };
                  // Named vendors, plus any element that says "captcha" in its src, class or id —
                  // the challenge that actually blocked us was an image grid from none of the
                  // three. Attributes only, never page text, so a form that merely mentions the
                  // word does not trip it.
                  const sel = 'iframe[src*="recaptcha/api2/anchor"], iframe[src*="hcaptcha.com/captcha"],'
                            + 'iframe[src*="challenges.cloudflare.com"], .h-captcha, .cf-turnstile,'
                            + '[id*="captcha" i], [class*="captcha" i], iframe[src*="captcha" i]';
                  return [...document.querySelectorAll(sel)].some(shown);
                }
                """);
        }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>
    /// The button that sends the application. Most specific first, and "Apply" dead last:
    /// Greenhouse's posting page carries an "Apply" button above the description that only
    /// scrolls to the form, and one regex matching both names took it — in document order it
    /// comes first — so every real run clicked the anchor, waited, and reported "Submit was
    /// clicked but no confirmation text was recognised" while no request ever left the page.
    /// </summary>
    /// <summary>
    /// UKG's employer profile step (<c>…/AuthCode/Register</c>): first name, last name, phone,
    /// a privacy checkbox, "Create account". The candidate account itself is the shared
    /// <c>signin-us.ultipro.com</c> login, saved as the <c>ultipro.com</c> board account; this
    /// registers that account with the one employer. Filled from the packet's standard answers,
    /// which is what the person's profile says. True when the step was there and pressed.
    /// </summary>
    private static async Task<bool> AdvanceUkgProfileAsync(BrowserSession session, AgentPacket packet)
    {
        var page = session.Page;
        // Known by UKG's own automation ids, which are the same on every board it hosts.
        try { if (await page.Locator("ukg-button[data-automation='registrationDetails-submit-button']").CountAsync() == 0) return false; }
        catch (PlaywrightException) { return false; }
        try
        {
            static string Ans(AgentPacket p, string id) => p.Answers.TryGetValue(id, out var v) ? v.Trim() : "";
            var (first, last) = SplitName(Ans(packet, "std:full_name"), Ans(packet, "std:first_name"), Ans(packet, "std:last_name"));
            foreach (var (name, value) in new[] { ("firstName", first), ("lastName", last), ("phoneNumber", Ans(packet, "std:phone")) })
            {
                var box = page.Locator($"input[name='{name}']").First;
                if (value.Length > 0 && await box.CountAsync() > 0 && (await box.InputValueAsync()).Trim().Length == 0)
                    await box.FillAsync(value, new() { Timeout = 5_000 });
            }
            // The privacy consent is a web component; its box is in a shadow root Playwright pierces.
            var consent = page.Locator("ukg-checkbox[data-automation='registrationDetails-privacy-checkbox'] input[type=checkbox], ukg-checkbox[data-automation='registrationDetails-privacy-checkbox']").First;
            if (await consent.CountAsync() > 0)
            {
                var ticked = await consent.EvaluateAsync<bool>("el => (el.matches('input') ? el : el.shadowRoot?.querySelector('input[type=checkbox]') || el.querySelector('input[type=checkbox]'))?.checked === true");
                if (!ticked) await consent.ClickAsync(new() { Timeout = 5_000 });
            }
            var create = page.Locator("ukg-button[data-automation='registrationDetails-submit-button'], button").Filter(new() { HasTextRegex = new Regex(@"^\s*create account\s*$", RegexOptions.IgnoreCase) }).First;
            if (await create.CountAsync() == 0) return false;
            await create.ClickAsync(new() { Timeout = 10_000 });
            // Gone or hidden means the step was accepted; still there means it was not (a field
            // the employer's validation refused), and the generic pass will say which.
            try
            {
                await page.Locator("ukg-button[data-automation='registrationDetails-submit-button']").First
                    .WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
                return true;
            }
            catch (TimeoutException) { return false; }
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return false; }
    }

    private static (string First, string Last) SplitName(string full, string first, string last)
    {
        if (first.Length > 0 || last.Length > 0) return (first, last);
        var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? ("", "") : (parts[0], parts.Length > 1 ? parts[^1] : "");
    }

    private static async Task<ILocator?> FindSubmitAsync(IFrame page)
    {
        // Nothing that reads as "later" is ever the button (#210): join.com's "Apply later"
        // is a type=submit on the posting page, and as the last one in the document it was
        // exactly what the generic fallbacks picked.
        var notLater = new LocatorFilterOptions { HasNotTextRegex = BrowserSession.LaterWords() };
        var candidates = new[]
        {
            page.Locator("#submit_app").First,
            // SuccessFactors' Apply is a span, not a button: …:_submitBtn (#216).
            page.Locator("[id$='_submitBtn']").Filter(notLater).First,
            page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"submit (?:my |your |the )?application", RegexOptions.IgnoreCase) }).Filter(notLater).Last,
            page.Locator("form").GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^submit\b", RegexOptions.IgnoreCase) }).Filter(notLater).Last,
            page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^submit\b", RegexOptions.IgnoreCase) }).Filter(notLater).Last,
            page.Locator("form button[type=submit], form input[type=submit]").Filter(notLater).Last,
            page.Locator("button[type=submit], input[type=submit]").Filter(notLater).Last,
            page.Locator("form").GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^apply(?: now)?$", RegexOptions.IgnoreCase) }).Filter(notLater).Last,
            // Last of all, a lone "Apply" anywhere on a page that has a form: the accordion
            // boards put theirs in a footer bar outside any <form>.
            page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^apply$", RegexOptions.IgnoreCase) }).Filter(notLater).Last,
        };
        foreach (var c in candidates)
        {
            try { if (await c.CountAsync() > 0 && await c.IsVisibleAsync() && await c.IsEnabledAsync()) return c; }
            catch (PlaywrightException) { /* next */ }
        }
        return null;
    }

    /// <summary>A string as an XPath literal — quotes of either kind survive.</summary>
    private static string XPathLiteral(string s) =>
        !s.Contains('\'') ? $"'{s}'" : !s.Contains('"') ? $"\"{s}\""
        : "concat(" + string.Join(", \"'\", ", s.Split('\'').Select(part => $"'{part}'")) + ")";

    [GeneratedRegex(@"flagged as (?:possible |potential )?spam|couldn't submit your application|unable to submit your application|suspicious activity|automated (?:submission|traffic)", RegexOptions.IgnoreCase)]
    private static partial Regex SpamRefusal();

    [GeneratedRegex(@"resume|résumé|\bcv\b|curriculum", RegexOptions.IgnoreCase)]
    private static partial Regex ResumeWords();

    /// <summary>The résumé field, by id or by label — "resume", "Attach Resume", "CV", "curriculum vitae".</summary>
    private static bool IsResume(PacketQuestion q) =>
        ResumeWords().IsMatch(q.Id) || ResumeWords().IsMatch(q.Label);

    private static bool IsCoverLetter(PacketQuestion q) =>
        q.Id.Contains("cover", StringComparison.OrdinalIgnoreCase) || q.Label.Contains("cover letter", StringComparison.OrdinalIgnoreCase);

    /// <summary>A value quoted for a CSS attribute selector: <c>[id="89:_input"]</c>. Attribute
    /// selectors take any id, where <c>#id</c> cannot start with a digit however it is escaped.</summary>
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [GeneratedRegex(@"^\s*(?:no selection|none selected|select(?:\s+one|\s+an option|\.{3}|…)?|-+\s*select\s*-+|please (?:select|choose)(?:\s+one)?|choose(?:\s+one)?)\s*[.…:]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"you (?:have )?already applied|already applied for this (?:position|job|role)|you(?:'ve| have) already (?:submitted|sent) (?:an|your) application|application already (?:exists|submitted)", RegexOptions.IgnoreCase)]
    private static partial Regex AlreadyApplied();

    /// <summary>Requests that are never the application: analytics beacons, the captcha's own
    /// traffic, and the résumé uploader's trip to object storage.</summary>
    private static bool IsChatter(string url) =>
        Regex.IsMatch(url, @"snowplow|analytics|recaptcha|hcaptcha|gstatic|googleapis|amazonaws\.com|sentry|segment\.io|mixpanel|datadog", RegexOptions.IgnoreCase);

    /// <summary>The page's visible text, or "" if it can't be read — never throws.</summary>
    private static async Task<string> BodyTextAsync(IFrame page)
    {
        try { return await page.InnerTextAsync("body"); }
        catch (PlaywrightException) { return ""; }
    }
}
