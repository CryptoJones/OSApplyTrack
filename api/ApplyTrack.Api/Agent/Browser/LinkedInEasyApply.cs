// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;
using Microsoft.Playwright;

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>
/// Drives LinkedIn's Easy Apply dialog (#278), signed in as the tenant's own account through
/// the kept session <see cref="BrowserSession.OpenAsync"/> loads. Mapped from the live dialog
/// on 2026-09-20: the entry is <c>button#jobs-apply-button-id</c>; the dialog
/// <c>.jobs-easy-apply-modal</c> walks Contact info → Resume → top choice → the employer's
/// Additional Questions → Review, each step behind <c>button[data-easy-apply-next-button]</c>
/// (the last behind <c>[data-live-test-easy-apply-review-button]</c>), ending at
/// <c>button[data-live-test-easy-apply-submit-button]</c>.
///
/// Two things make it unlike every other form here. Its questions <b>do not exist until their
/// step is reached</b>, so nothing can discover them up front: the run answers what the packet
/// already knows, and hands back the required ones it met for the first time as
/// <see cref="SubmitOutcome.Discovered"/> for the worker to draft and try again. And it is a
/// person's own LinkedIn account being driven, so it stops dead at anything that is not the
/// dialog — a sign-in, a checkpoint — rather than press on.
///
/// A dry run stops at the Submit step, screenshots, and <b>Discards</b>. A run that needs the
/// person <b>Saves</b>, which keeps LinkedIn's own draft: they can finish it by hand from
/// exactly where the agent stopped.
///
/// On the posting's own page LinkedIn renders the dialog inside a <b>shadow root</b>
/// (<c>div#interop-outlet</c>). A Playwright locator reaches into it; <c>document.querySelector</c>
/// does not, and neither does a <c>label[for]</c> looked up on the document. So every script here
/// starts from the dialog element and asks its own root — found by running 1.49.0 against the
/// real page, where the first version saw an empty dialog.
/// </summary>
internal static partial class LinkedInEasyApply
{
    // Two pages, two ways in. The search page's side panel has the button this was first mapped
    // from; the posting's own page — the one a stored link opens — has an anchor with generated
    // class names and only its aria-label to go by. Never a bare "Easy Apply": that is also the
    // search page's filter pill.
    //
    // And a third: once a run has SAVED a draft — which is what a run that needs an answer does —
    // LinkedIn swaps "Easy Apply" for "Continue" ("You last modified this application now"), and
    // the very next run, the one that now has the answer, found "no Easy Apply button" (#278). On
    // the posting's own page both are the same link into …/jobs/view/<id>/apply/, so that is what
    // is looked for; the labels are kept for the side panel, where the entry is a button.
    public const string Entry = "a[href*='/jobs/view/'][href*='/apply/'], "
        + "a[aria-label^='Easy Apply to' i], button[aria-label^='Easy Apply to' i], "
        + "a[aria-label^='Continue applying' i], button[aria-label^='Continue applying' i], "
        + "button#jobs-apply-button-id, button.jobs-apply-button[data-live-test-job-apply-button], "
        // The SDUI page (#403): a saved draft's way back in is a bare "Continue" link to the posting
        // itself, no label and no /apply/; the first run after 1.66.2 saved drafts it could not reopen.
        + "a[href*='/jobs/view/']:text-is('Continue'), a[href*='/jobs/view/']:has(:text-is('Continue'))";
    // Two dialogs, one flow. The first is the artdeco modal mapped on 2026-09-20. The second
    // shipped around 2026-09-30 with LinkedIn's SDUI front end (#403): a native <dialog> around
    // the EasyApply screen, every class name generated and none of the data-* hooks left, so its
    // buttons are known only by what they say. Every run from then on timed out waiting for a
    // .jobs-easy-apply-modal that was no longer there, with the dialog already open on screen.
    private const string Modal = ":is(.jobs-easy-apply-modal, dialog[open]:has([data-sdui-screen*='easyapply']))";
    private const string Next = Modal + " button[data-easy-apply-next-button], " + Modal + " button[data-live-test-easy-apply-review-button], "
        + Modal + " footer button:has-text('Next'), " + Modal + " footer button:has-text('Review')";
    private const string Submit = Modal + " button[data-live-test-easy-apply-submit-button], " + Modal + " button:has-text('Submit application')";
    private const string Close = Modal + " [data-test-modal-close-btn], " + Modal + " button[aria-label='Dismiss']";
    private const string Follow = Modal + " #follow-company-checkbox, " + Modal + " input[type=checkbox][aria-label^='Follow ']";
    /// <summary>More steps than any real dialog has; a loop that will not end is a bug, not a form.
    /// Twelve was not: Trility's dialog is fourteen pages, one question each, and every run of it
    /// ended on page 13 (#409). A work-history step also takes one Next per row it opens for
    /// editing (#408). A dialog that is really stuck is caught sooner, by a Next that changes nothing.</summary>
    private const int MaxSteps = 40;

    /// <summary>LinkedIn's interstitial before some postings' dialog (KRON Development, 2026-10-05):
    /// "Job search safety reminder", whose "Continue applying" link opens the real dialog (#430).</summary>
    private const string SafetyTips = "dialog[open]:has([data-sdui-screen*='SafetyTips' i])";

    /// <summary>One control group in the dialog, as the page describes it. <paramref name="Pending"/>:
    /// a blank on a work-history row the step lists but has not opened for editing — no control
    /// yet, only a question to have ready for when it opens (#408).</summary>
    public sealed record Field(string Label, string Kind, bool Required, bool Filled, List<string> Options, string ControlId, bool Invalid, bool Pending = false);

    [GeneratedRegex(@"/(login|authwall|checkpoint|uas)(/|\?|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SignedOut();

    [GeneratedRegex(@"no longer accepting applications|this job is no longer available|job has expired", RegexOptions.IgnoreCase)]
    private static partial Regex Gone();

    [GeneratedRegex(@"application (was )?sent|your application was sent|application submitted", RegexOptions.IgnoreCase)]
    private static partial Regex Sent();

    /// <summary>
    /// LinkedIn's mark on a posting it already holds an application for: "Applied" as a status
    /// pill, or "Application submitted"/"You applied" with when. Distinct from <see cref="Sent()"/>,
    /// which reads the confirmation the dialog shows in the moment it is sent.
    /// </summary>
    [GeneratedRegex(@"\bapplied on\b|\byou applied\b|\bapplication submitted\b|^\s*applied\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Applied();

    /// <summary>
    /// The employer's own application URL, when the posting's Apply leads off LinkedIn — the
    /// host alone, which is what the person needs to know. Empty when there is no Apply at all.
    /// LinkedIn renders an offsite Apply as a control saying so, and the link it opens is the
    /// employer's; an Easy Apply one never leaves linkedin.com.
    /// </summary>
    private static async Task<string> OffsiteApplyAsync(BrowserSession session)
    {
        var page = session.Page;
        try
        {
            var href = await page.EvaluateAsync<string?>("""
                () => {
                  const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
                  for (const el of document.querySelectorAll('a[href], button')) {
                    if (!shown(el)) continue;
                    const name = ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '')).trim();
                    if (!/\bapply\b/i.test(name) || /easy apply/i.test(name)) continue;
                    const href = el.getAttribute('href') || '';
                    if (!/^https?:/i.test(href)) continue;
                    const to = new URL(href);
                    if (!/(^|\.)linkedin\.com$/i.test(to.hostname)) return href;
                    // The live shape (2026-09-23): "Apply on company website" is an <a target=_blank>
                    // through LinkedIn's own interstitial, /safety/go/?url=<the employer's address>.
                    const out = to.searchParams.get('url');
                    if (out && /^https?:/i.test(out) && !/(^|\.)linkedin\.com$/i.test(new URL(out).hostname)) return out;
                  }
                  return null;
                }
                """);
            if (href is { Length: > 0 } && Uri.TryCreate(href, UriKind.Absolute, out _)) return href;

            // The live shape (AVER, Deloitte, UMB …, 2026-09-23): "Apply ↗" is a BUTTON with no
            // address — "Responses managed off LinkedIn" — that opens the employer's page in a new
            // tab. Pressing it applies to nothing; it only opens that page, and the route guard
            // refuses to follow it off LinkedIn, which is exactly how its address is learned.
            var apply = page.GetByRole(AriaRole.Button, new() { NameRegex = PlainApply() }).First;
            if (await apply.CountAsync() == 0 || !await apply.IsVisibleAsync()) return "";
            // The address is read from the page's own window.open, which is replaced for the one
            // click: nothing opens, nothing is sent, and a tab the remote browser would open
            // without ever reporting it (the Allstate adoption problem, #280) is not needed.
            await page.EvaluateAsync("""
                () => { window.__osatOpened = ''; window.open = (u) => { window.__osatOpened = String(u || ''); return null; }; }
                """);
            var before = session.RefusedUrls.Count;
            await apply.ClickAsync(new() { Timeout = 5_000 });
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                string opened;
                try { opened = await page.EvaluateAsync<string>("() => window.__osatOpened || ''"); }
                catch (PlaywrightException) { opened = ""; }   // the page itself navigated
                if (opened.Length > 0 && Uri.TryCreate(new Uri(page.Url), opened, out var to) && to.Scheme.StartsWith("http", StringComparison.Ordinal))
                    return to.AbsoluteUri;
                var refused = session.RefusedUrls;
                if (refused.Count > before) return refused[before];
                await page.WaitForTimeoutAsync(300);
            }
            return "";
        }
        catch (PlaywrightException) { return ""; }
        catch (TimeoutException) { return ""; }
    }

    [GeneratedRegex(@"^\s*apply\b(?!.*easy)", RegexOptions.IgnoreCase)]
    private static partial Regex PlainApply();

    /// <summary>What the page actually said, for a failure that can name nothing better: its first
    /// line of real text. A run that reports only what it could not find is unreadable (#280).</summary>
    private static string Seen(string body)
    {
        var line = body.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 3 && l.Any(char.IsLetter)) ?? "";
        return line.Length == 0 ? "the page had no text at all"
            : "the page reads \"" + (line.Length > 120 ? line[..120] + "…" : line) + "\"";
    }

    public static async Task<SubmitOutcome> RunAsync(BrowserSession session, AgentPacket packet, bool dryRun, ILogger log, CancellationToken ct)
    {
        var page = session.Page;
        var mapped = new List<string>();
        var unmapped = new List<string>();
        var discovered = new List<PacketQuestion>();
        byte[]? shot = null;

        SubmitOutcome Fail(string why, bool closed = false) =>
            new(mapped.Count > 0, false, page.Url, "", shot, unmapped, mapped, why, Closed: closed, Discovered: discovered);

        try
        {
            if (SignedOut().IsMatch(page.Url))
                return Fail("LinkedIn asked to sign in — the kept session has lapsed or been challenged; it is renewed on the next pass, and nothing was attempted");

            var entry = page.Locator(Entry).First;
            try { await entry.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }); }
            catch (TimeoutException)
            {
                shot = await session.ScreenshotAsync();
                var body = await page.InnerTextAsync("body", new() { Timeout = 5_000 });
                if (Gone().IsMatch(body)) return Fail("posting is no longer accepting applications", closed: true);
                // A posting's page is public: signed out, LinkedIn serves it anyway, with "Sign in" and
                // "Join now" where the navigation would be and a plain Apply that leads to a login.
                // Read as "no Easy Apply button", that sent a whole investigation the wrong way.
                var head = body.Length > 800 ? body[..800] : body;
                if (head.Contains("Join now", StringComparison.OrdinalIgnoreCase) && head.Contains("Sign in", StringComparison.OrdinalIgnoreCase))
                    return Fail("LinkedIn showed its signed-out page — the kept session is not valid in the browser; it is renewed on the next pass, and nothing was attempted");
                if (SignedOut().IsMatch(page.Url))
                    return Fail("LinkedIn asked to sign in — the kept session has lapsed or been challenged");
                // "already applied to, or the employer moved it to its own site" guessed between two
                // outcomes that want opposite things: an application already sent is finished, and an
                // offsite Apply is a lead to re-stage on the employer's own link. The page says which
                // (#278): LinkedIn marks a posting it has an application for, and an Apply that is not
                // Easy Apply carries the employer's URL. Twenty-six runs came back on this one line
                // after the 1.50.0 session lapse, every one of them unreadable.
                if (Applied().IsMatch(body))
                    return Fail("LinkedIn says this application was already sent — nothing to do", closed: true);
                if (await OffsiteApplyAsync(session) is { Length: > 0 } offsite)
                {
                    var host = Uri.TryCreate(offsite, UriKind.Absolute, out var at) ? at.Host : offsite;
                    return Fail($"the employer takes this application on its own site ({host}), not through Easy Apply — the lead moves to that link") with { OffsiteLink = offsite };
                }
                return Fail("no Easy Apply button on the posting, and no Apply of any kind: " + Seen(body));
            }
            await entry.ClickAsync(new() { Timeout = 10_000 });
            if (await OpenDialogAsync(page) is { Length: > 0 } instead)
            {
                shot = await session.ScreenshotAsync();
                return Fail("the Easy Apply dialog did not open — " + instead);
            }

            var stuck = 0;
            for (var step = 1; step <= MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                await page.WaitForTimeoutAsync(900);
                await UntickFollowAsync(page);

                var tried = new List<string>();
                foreach (var f in await ReadStepAsync(page))
                {
                    if (f.Filled && !f.Invalid) continue;
                    var answer = AnswerFor(packet, f.Label);
                    // A row the step lists but will only open for editing after Next (#408): what
                    // it is missing is handed back now, so one run learns every row's question
                    // rather than one per run, and nothing is filled until its editor opens.
                    if (f.Pending)
                    {
                        if (answer.Length == 0 && Unknown(packet, discovered, f.Label)) discovered.Add(AsQuestion(f));
                        continue;
                    }
                    if (answer.Length == 0)
                    {
                        if (!f.Required) continue;
                        unmapped.Add(f.Label);
                        if (Unknown(packet, discovered, f.Label)) discovered.Add(AsQuestion(f));
                        continue;
                    }
                    if (await FillAsync(page, f, answer)) { mapped.Add(f.Label); tried.Add(f.Label); }
                    else if (f.Required) unmapped.Add(f.Label);
                }

                if (unmapped.Count > 0)
                {
                    shot = await session.ScreenshotAsync();
                    await LeaveAsync(page, save: true);
                    return new SubmitOutcome(true, false, page.Url, "", shot, unmapped, mapped,
                        dryRun ? "" : "refused to submit: the dialog asks for answers the packet does not have", Discovered: discovered);
                }

                if (await page.Locator(Submit).First.IsVisibleAsync())
                {
                    var notFollowing = await UntickFollowAsync(page);
                    shot = await session.ScreenshotAsync();
                    // Submitting would follow the company in the person's name. Not the agent's to do.
                    if (!dryRun && !notFollowing)
                    {
                        await LeaveAsync(page, save: true);
                        return Fail("refused to submit: \"Follow the company\" could not be unticked, and applying is not following — the draft is saved on LinkedIn");
                    }
                    if (dryRun)
                    {
                        await LeaveAsync(page, save: false);
                        return new SubmitOutcome(true, false, page.Url, "", shot, unmapped, mapped, "", Discovered: discovered,
                            ReachedSubmit: true);
                    }
                    return await SubmitAsync(session, page, mapped, unmapped, shot);
                }

                var next = page.Locator(Next).First;
                if (!await next.IsVisibleAsync())
                {
                    shot = await session.ScreenshotAsync();
                    await LeaveAsync(page, save: true);
                    return Fail($"the Easy Apply dialog offered no way forward at step {step}");
                }
                var before = await DialogTextAsync(page);
                await next.ClickAsync(new() { Timeout = 10_000 });
                await page.WaitForTimeoutAsync(1_500);

                // The dialog's own verdict on what was just entered: a field it still flags is one
                // the answer did not satisfy (a number wanted, an option not offered). Only an
                // answer this step gave counts: a field Next itself brought up — the work-history
                // editor that opens on a row LinkedIn holds no city for (#408) — is a new question,
                // read and answered on the next pass round the loop like any other.
                var flagged = (await ReadStepAsync(page)).Where(f => f.Invalid).Select(f => f.Label).ToList();
                var rejected = flagged.Where(tried.Contains).ToList();
                if (rejected.Count > 0)
                {
                    foreach (var label in rejected) { mapped.Remove(label); if (!unmapped.Contains(label)) unmapped.Add(label); }
                    shot = await session.ScreenshotAsync();
                    await LeaveAsync(page, save: true);
                    return new SubmitOutcome(true, false, page.Url, "", shot, unmapped, mapped,
                        dryRun ? "" : "refused to submit: the dialog rejected an answer", Discovered: discovered);
                }
                // A Next that changed nothing, twice over, is a dialog going nowhere; say where.
                stuck = await DialogTextAsync(page) == before ? stuck + 1 : 0;
                if (stuck >= 2)
                {
                    shot = await session.ScreenshotAsync();
                    await LeaveAsync(page, save: true);
                    return Fail($"the Easy Apply dialog would not move past {await PageOfAsync(page)}" + (flagged.Count > 0 ? $" (it flags: {string.Join(", ", flagged)})" : ""));
                }
            }
            shot = await session.ScreenshotAsync();
            await LeaveAsync(page, save: true);
            return Fail($"the Easy Apply dialog did not reach its Submit step within {MaxSteps} steps (it stopped at {await PageOfAsync(page)})");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            log.LogWarning("LinkedIn Easy Apply failed at {Url}: {Reason}", page.Url, ex.Message.Split('\n')[0]);
            shot ??= await session.ScreenshotAsync();
            return Fail("LinkedIn Easy Apply: " + ex.Message.Split('\n')[0]);
        }
    }

    /// <summary>
    /// The real click. Confirmed two ways, either of which will do: LinkedIn's own apply call
    /// answering 2xx, or the dialog saying the application was sent. A click nothing confirms is
    /// reported in the words <see cref="ReadyReconciler"/> refuses to retry — it may have landed.
    /// </summary>
    private static async Task<SubmitOutcome> SubmitAsync(BrowserSession session, IPage page, List<string> mapped, List<string> unmapped, byte[]? shot)
    {
        var accepted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResponse(object? _, IResponse r)
        {
            if (r.Request.Method == "POST" && r.Url.Contains("OnsiteApplyApplication", StringComparison.OrdinalIgnoreCase) && r.Status is >= 200 and < 300)
                accepted.TrySetResult(r.Status);
        }
        page.Response += OnResponse;
        try
        {
            await page.Locator(Submit).First.ClickAsync(new() { Timeout = 10_000 });
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                await page.WaitForTimeoutAsync(1_000);
                var said = await page.Locator("[role=dialog], .artdeco-modal, dialog[open]").AllInnerTextsAsync();
                var sent = said.Select(t => Sent().Match(t)).FirstOrDefault(m => m.Success);
                if (sent is not null || accepted.Task.IsCompletedSuccessfully)
                {
                    shot = await session.ScreenshotAsync() ?? shot;
                    var snippet = sent is not null ? string.Join(" ", said).Trim() : "LinkedIn accepted the application (apply call answered " + accepted.Task.Result + ")";
                    return new SubmitOutcome(true, true, page.Url, snippet.Length > 400 ? snippet[..400] : snippet, shot, unmapped, mapped, "");
                }
            }
            shot = await session.ScreenshotAsync() ?? shot;
            return new SubmitOutcome(true, false, page.Url, "", shot, unmapped, mapped,
                "Submit was clicked but no confirmation text was recognised — check the screenshot and LinkedIn's Applied jobs");
        }
        finally { page.Response -= OnResponse; }
    }

    /// <summary>
    /// Wait for the dialog Easy Apply opens, through LinkedIn's safety reminder when it puts one
    /// first. Empty once the dialog is up; otherwise what showed instead. Both KRON postings
    /// failed on a bare "Timeout 15000ms exceeded." while the reminder sat on screen waiting for
    /// "Continue applying" — it is LinkedIn's own, says nothing about the employer, and pressing
    /// it is what the person would do (#430).
    /// </summary>
    private static async Task<string> OpenDialogAsync(IPage page)
    {
        var visible = new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 };
        try { await page.Locator($"{Modal}, {SafetyTips}").First.WaitForAsync(visible); }
        catch (TimeoutException) { return await WhatShowsAsync(page); }
        if (await page.Locator(Modal).First.IsVisibleAsync()) return "";
        await page.Locator($"{SafetyTips} :is(a, button):has-text('Continue applying')").First.ClickAsync(new() { Timeout = 10_000 });
        try { await page.Locator(Modal).First.WaitForAsync(visible); return ""; }
        catch (TimeoutException) { return "past LinkedIn's safety reminder, " + await WhatShowsAsync(page); }
    }

    private static async Task<string> WhatShowsAsync(IPage page)
    {
        try
        {
            var said = (await page.Locator("dialog[open], [role=dialog], [role=alertdialog]").AllInnerTextsAsync())
                .Select(t => t.Trim()).FirstOrDefault(t => t.Length > 0);
            return said is null ? "nothing opened when Easy Apply was pressed" : Seen(said).Replace("the page reads", "a dialog reads");
        }
        catch (PlaywrightException) { return "nothing opened when Easy Apply was pressed"; }
    }

    private static async Task<string> DialogTextAsync(IPage page)
    {
        try { return await page.Locator(Modal).First.InnerTextAsync(new() { Timeout = 3_000 }); }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return ""; }
    }

    /// <summary>"page 13 of 14", from the dialog's own counter, for a failure to say where it was.</summary>
    private static async Task<string> PageOfAsync(IPage page)
    {
        var text = await DialogTextAsync(page);
        return PagesRe().Match(text) is { Success: true } m ? $"page {m.Groups[1].Value} of {m.Groups[2].Value}"
            : Seen(text).Replace("the page reads", "the step that reads");
    }

    [GeneratedRegex(@"(\d+)\s*/\s*(\d+)\s+pages", RegexOptions.IgnoreCase)]
    private static partial Regex PagesRe();

    /// <summary>A label neither the packet nor this run has met yet.</summary>
    private static bool Unknown(AgentPacket packet, List<PacketQuestion> discovered, string label) =>
        packet.Questions.All(q => !SameLabel(q.Id, label) && !SameLabel(q.Label, label))
        && discovered.All(q => !SameLabel(q.Id, label));

    /// <summary>"Follow &lt;company&gt;" arrives ticked. Applying is not following. True when the
    /// box is absent or verifiably unticked; false when it could not be shown to be.</summary>
    private static async Task<bool> UntickFollowAsync(IPage page)
    {
        try
        {
            var follow = page.Locator(Follow).First;
            if (await follow.CountAsync() == 0) return true;
            for (var attempt = 0; attempt < 2 && await follow.IsCheckedAsync(); attempt++)
            {
                await follow.EvaluateAsync("el => (el.getRootNode().querySelector(`label[for=\"${CSS.escape(el.id)}\"]`) || el).click()");
                await page.WaitForTimeoutAsync(200);
            }
            return !await page.Locator(Follow).First.IsCheckedAsync();
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return false; }
    }

    /// <summary>Close the dialog: Save keeps LinkedIn's draft for the person, Discard leaves nothing.</summary>
    private static async Task LeaveAsync(IPage page, bool save)
    {
        try
        {
            await page.Locator(Close).First.ClickAsync(new() { Timeout = 5_000 });
            // The SDUI dialog asks "Save this application?" in a second <dialog> of its own.
            var choices = page.Locator("[data-test-dialog-primary-btn], [data-test-dialog-secondary-btn], [role=alertdialog] button, .artdeco-modal button, dialog[open] button")
                .Filter(new() { HasTextRegex = save ? new Regex(@"^\s*Save\s*$", RegexOptions.IgnoreCase) : new Regex(@"^\s*Discard\s*$", RegexOptions.IgnoreCase), Visible = true });
            // A work-history row open for editing has a "Save" of its own (#408), earlier in the
            // page than the question's; it is not the answer to "Save this application?".
            var choice = choices.First;
            for (var i = 0; i < await choices.CountAsync(); i++)
                if (!await choices.Nth(i).EvaluateAsync<bool>("b => /^\\s*Edit\\b/i.test(b.parentElement?.querySelector(':scope > p')?.innerText || '')"))
                { choice = choices.Nth(i); break; }
            await choice.ClickAsync(new() { Timeout = 5_000 });
            // Let it land. The run ends here and its context closes with it, which would cut off
            // the very request that discards or saves the draft.
            await page.Locator(Modal).First.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
            await page.WaitForTimeoutAsync(800);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { /* the context closes with the run either way */ }
    }

    /// <summary>The packet's answer for a label the dialog shows — by question id or label, since a
    /// question this driver discovered is keyed by its label.</summary>
    public static string AnswerFor(AgentPacket packet, string label)
    {
        foreach (var q in packet.Questions)
            if ((SameLabel(q.Id, label) || SameLabel(q.Label, label)) && packet.Answers.TryGetValue(q.Id, out var a) && a.Trim().Length > 0)
                return a.Trim();
        // The contact step's City / Location typeahead is not in the standard set, and a question
        // only reaches the drafter once a run hands it back. The typeahead takes the first match
        // for what the résumé already says. (SMX's "City", which this was first written for, was
        // not this one but a work-history row's — #408, below.)
        // Only a location that names a city ("Minden, Nebraska"): a bare "United States" would
        // let the typeahead's first suggestion stand in for a city the résumé never gave.
        // The SDUI dialog (#403) leaves the mobile number blank where the old one filled it from
        // the profile; the packet's standard phone is the same number.
        if (PhoneLabel().IsMatch(label) && packet.Answers.TryGetValue("std:phone", out var phone) && phone.Trim().Length > 0)
            return phone.Trim();
        // A work-history row's Description is that role's own lines on the résumé (#408). Its
        // City is not: the résumé keeps one location, today's, and a past job's city is a
        // question for the drafter or the person, never today's address passed off as then's.
        if (HistoryLabel().Match(label) is { Success: true } row && row.Groups["field"].Value.Equals("Description", StringComparison.OrdinalIgnoreCase)
            && RoleOf(packet.Resume, row.Groups["company"].Value, row.Groups["title"].Value) is { Highlights.Count: > 0 } role)
        {
            var lines = string.Join(" ", role.Highlights.Select(h => h.Trim()).Where(h => h.Length > 0));
            return lines.Length > 2_000 ? lines[..2_000].TrimEnd() : lines;
        }
        if (HistoryLabel().IsMatch(label)) return "";
        if (CityLabel().IsMatch(label) && packet.Resume is { Location.Length: > 0 } resume
            && AnswerDrafter.StripLocationSuffix(resume.Location) is { } place && place.Contains(','))
            return place;
        return "";
    }

    [GeneratedRegex(@"^\s*(?:city|location|current location|location \(city\))\s*\*?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CityLabel();

    /// <summary>The label <see cref="ReadStepAsync"/> gives a work-history row's field:
    /// "Work experience — &lt;title&gt; — &lt;company&gt; — &lt;field&gt;" (#408).</summary>
    [GeneratedRegex(@"^\s*(?<section>Work experience|Education) — (?<title>.+) — (?<company>.+?) — (?<field>[^—]+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HistoryLabel();

    /// <summary>The résumé's role for a row LinkedIn lists: by company, either name holding the
    /// other ("Black Hills Information Security" is "… / Active Countermeasures" on the résumé),
    /// and by title too when the company had more than one.</summary>
    private static ResumeExperience? RoleOf(Resume? resume, string company, string title)
    {
        if (resume is null || Squash(company).Length < 2) return null;
        bool Holds(string a, string b) => Squash(a).Contains(Squash(b), StringComparison.OrdinalIgnoreCase);
        var at = resume.Experience.Where(e => e.Company.Length > 1 && (Holds(e.Company, company) || Holds(company, e.Company))).ToList();
        return at.Count <= 1 ? at.FirstOrDefault()
            : at.FirstOrDefault(e => Holds(e.Title, title) || Holds(title, e.Title)) ?? at[0];
    }

    [GeneratedRegex(@"^\s*(?:mobile\s+)?phone(?:\s+number)?\s*\*?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneLabel();

    private static bool SameLabel(string a, string b) =>
        string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);

    private static string Squash(string s) => Regex.Replace(s ?? "", @"[\s*]+", " ").Trim();

    /// <summary>A control the packet has never heard of, as a question the drafter can answer.</summary>
    public static PacketQuestion AsQuestion(Field f) => new(
        f.Label, f.Label, f.Required,
        f.Kind switch { "select" or "radio" => PacketQuestion.Select, "textarea" => PacketQuestion.Textarea, "checkbox" => PacketQuestion.Select, _ => PacketQuestion.Text },
        f.Kind == "checkbox" ? ["Yes", "No"] : f.Options, PacketQuestion.Custom);

    private static async Task<bool> FillAsync(IPage page, Field f, string answer)
    {
        if (f.ControlId.Length == 0) return false;
        try
        {
            switch (f.Kind)
            {
                case "select":
                {
                    var pick = Choose(f.Options, answer);
                    if (pick is null) return false;
                    await page.Locator($"{Modal} [id=\"{Css(f.ControlId)}\"]").First.SelectOptionAsync(new SelectOptionValue { Label = pick });
                    return true;
                }
                case "radio":
                {
                    var pick = Choose(f.Options, answer);
                    if (pick is null) return false;
                    // Pressed through its label: LinkedIn draws its own radio over a hidden input.
                    return await page.Locator($"{Modal} [id=\"{Css(f.ControlId)}\"]").First.EvaluateAsync<bool>(
                        """
                        (first, want) => {
                          const group = first.closest('fieldset') || first.closest('[data-test-form-element]') || first.parentElement;
                          // The same scope ReadStepAsync read the options from: every same-name radio.
                          const scope = first.closest('fieldset') || first.closest('[data-test-form-element]') || first.getRootNode();
                          const radios = first.name ? [...scope.querySelectorAll(`input[type=radio][name="${CSS.escape(first.name)}"]`)] : [...group.querySelectorAll('input[type=radio]')];
                          const line = t => (t || '').split('\n').map(x => x.trim()).filter(Boolean)[0] || '';
                          for (const r of radios) {
                            const label = first.getRootNode().querySelector(`label[for="${CSS.escape(r.id)}"]`);
                            // The SDUI dialog's label is empty; the option's words sit beside it (#403).
                            const text = line(label?.innerText) || line(r.parentElement?.parentElement?.innerText) || r.value || '';
                            if (text.toLowerCase() === want.toLowerCase()) { (label || r).click(); return r.checked; }
                          }
                          return false;
                        }
                        """, pick);
                }
                case "checkbox":
                {
                    if (!Affirmative(answer)) return false;
                    return await page.Locator($"{Modal} [id=\"{Css(f.ControlId)}\"]").First.EvaluateAsync<bool>(
                        "el => { if (!el.checked) (el.getRootNode().querySelector(`label[for=\"${CSS.escape(el.id)}\"]`) || el).click(); return el.checked; }");
                }
                default:
                {
                    var box = page.Locator($"{Modal} [id=\"{Css(f.ControlId)}\"]").First;
                    // A number box takes a whole number: Insight Global's "How many years … with SQL,
                    // T-SQL, or PL/SQL?" refused the banked "5+" (#332). "5+" is 5, "10+ years" 10.
                    // The SDUI dialog draws that question as a plain 20-character text box, so the
                    // label is the only sign it wants a number (#412).
                    if (f.ControlId.Contains("numeric", StringComparison.OrdinalIgnoreCase)
                        || AnswerDrafter.WantsWholeNumber(f.Label)
                        || await box.GetAttributeAsync("type") == "number")
                        answer = AnswerDrafter.WholeNumber(answer) ?? answer;
                    await box.FillAsync(answer, new() { Timeout = 5_000 });
                    // A typeahead (city, school) only takes a value picked from its list: the
                    // suggestion is clicked once the list shows one, and a place the list does not
                    // know in full ("Minden, Nebraska") is tried by its head ("Minden"). Pressing
                    // ArrowDown and Enter blind left SMX's City typed and uncommitted.
                    if (await box.GetAttributeAsync("role") == "combobox" || await box.GetAttributeAsync("aria-autocomplete") is { Length: > 0 })
                    {
                        var all = page.Locator($"{Modal} [role=listbox] [role=option]:visible, {Modal} .basic-typeahead__selectable:visible");
                        var parts = answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var query in new[] { answer, parts.Length > 1 ? parts[0] : answer }.Distinct())
                        {
                            // The head alone ("Minden") also finds Minden, Germany: take only a
                            // suggestion that names the rest of the place ("Nebraska") too.
                            var options = query == answer ? all : all.Filter(new() { HasText = parts[1] });
                            if (query != answer) await box.FillAsync(query, new() { Timeout = 5_000 });
                            for (var i = 0; i < 12 && await options.CountAsync() == 0; i++)
                                await page.WaitForTimeoutAsync(250);
                            if (await options.CountAsync() == 0) continue;
                            await options.First.ClickAsync(new() { Timeout = 3_000 });
                            return true;
                        }
                        // No list this reader knows: the old way, and the step's own verdict after Next says if it took.
                        await box.FillAsync(answer, new() { Timeout = 5_000 });
                        await page.WaitForTimeoutAsync(1_200);
                        await box.PressAsync("ArrowDown");
                        await box.PressAsync("Enter");
                    }
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return false; }
    }

    /// <summary>The offered option an answer means: itself, its yes/no polarity, or the one it names.</summary>
    public static string? Choose(IReadOnlyList<string> options, string answer)
    {
        var a = Squash(answer);
        var exact = options.FirstOrDefault(o => string.Equals(Squash(o), a, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        if (options.Count == 2 && options.Any(o => Squash(o).Equals("Yes", StringComparison.OrdinalIgnoreCase))
            && options.Any(o => Squash(o).Equals("No", StringComparison.OrdinalIgnoreCase)))
            return options.First(o => Squash(o).Equals(Affirmative(a) ? "Yes" : "No", StringComparison.OrdinalIgnoreCase));
        return options.FirstOrDefault(o => Squash(o).Contains(a, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(o => a.Contains(Squash(o), StringComparison.OrdinalIgnoreCase) && Squash(o).Length > 2);
    }

    private static bool Affirmative(string answer) =>
        answer.Trim() is not ("" or "0") && !answer.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(answer.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    private static string Css(string id) => id.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Every control group on the step that is showing. Résumé cards, the upload box and the
    /// two courtesy checkboxes are LinkedIn's own and never a question.</summary>
    public static async Task<List<Field>> ReadStepAsync(IPage page)
    {
        var dialog = page.Locator(Modal).First;
        if (await dialog.CountAsync() == 0) return [];
        var json = await dialog.EvaluateAsync<string>(
            """
            modal => {
              // The dialog's own root: a shadow root on the posting's page, the document elsewhere.
              const root = modal.getRootNode();
              const shown = el => { const r = el.getBoundingClientRect(); return r.width > 0 || r.height > 0 || el.type === 'radio' || el.type === 'checkbox'; };
              const clean = t => (t || '').split('\n').map(x => x.trim()).filter(Boolean)[0] || '';
              // A label says itself twice — once to be seen, once for a screen reader — sometimes on
              // one line: "Email addressEmail address". The seen copy is the question.
              const said = el => {
                if (!el) return '';
                let t = clean(el.querySelector('[aria-hidden="true"]')?.innerText) || clean(el.innerText);
                const half = t.length / 2;
                if (t.length % 2 === 0 && t.slice(0, half) === t.slice(half)) t = t.slice(0, half);
                return t.trim();
              };
              const out = [], seen = new Set();
              const groups = modal.querySelectorAll('[data-test-form-element], .fb-dash-form-element, fieldset[data-test-form-builder-radio-button-form-component]');
              for (const g of groups) {
                const c = g.querySelector('input:not([type=hidden]):not([type=file]), select, textarea');
                if (!c || seen.has(c) || !shown(c)) continue;
                if ((c.id || '').startsWith('jobsDocumentCardToggle') || c.id === 'follow-company-checkbox') continue;
                const legend = g.querySelector('legend'), label = g.querySelector('label');
                const kind = c.tagName === 'SELECT' ? 'select' : c.tagName === 'TEXTAREA' ? 'textarea' : c.type === 'radio' ? 'radio' : c.type === 'checkbox' ? 'checkbox' : 'text';
                const text = (kind === 'radio' ? (said(legend) || said(label)) : (said(label) || said(legend))) || clean(c.getAttribute('aria-label'));
                if (!text || /top choice/i.test(text)) continue;
                for (const x of g.querySelectorAll('input, select, textarea')) seen.add(x);
                const radios = kind === 'radio' ? [...g.querySelectorAll('input[type=radio]')] : [];
                const options = kind === 'select' ? [...c.options].map(o => o.text.trim()).filter(t => t && !/^select an option$/i.test(t))
                  : radios.map(r => said(root.querySelector(`label[for="${CSS.escape(r.id)}"]`)) || r.value);
                const filled = kind === 'select' ? !!c.value && !/^select an option$/i.test(c.options[c.selectedIndex]?.text.trim() || '')
                  : kind === 'radio' ? radios.some(r => r.checked) : kind === 'checkbox' ? c.checked : (c.value || '').trim().length > 0;
                const required = c.required || c.getAttribute('aria-required') === 'true' || radios.some(r => r.required || r.getAttribute('aria-required') === 'true')
                  || !!g.querySelector('[data-test-form-builder-radio-button-form-component__required], .fb-dash-form-element__label--is-required, [class*=is-required]');
                out.push({ Label: text, Kind: kind, Required: required, Filled: filled, Options: options, ControlId: c.id || '', Invalid: !!g.querySelector('.artdeco-inline-feedback--error') });
              }
              if (groups.length) return JSON.stringify(out);

              // The SDUI dialog (#403): no group wrappers, just controls. A label[for] names a
              // contact field; a question's control carries the question as its aria-label, and a
              // radio group's options say themselves in a <p> beside each empty label. "*" marks
              // required. Errors arrive only after a refused Next: the helper text's own first <p>
              // ("Invalid input", "Please enter a valid phone number"), or a <p> after the fieldset.
              const column = modal.querySelector('[data-component-type=LazyColumn]') || modal;
              const optionText = r => clean(said(root.querySelector(`label[for="${CSS.escape(r.id)}"]`)) || r.parentElement?.parentElement?.innerText) || r.value;
              const errorish = /invalid|required|please (enter|select|make|provide|choose)|must be|enter a (valid|whole|decimal|number)|larger than|less than|between/i;
              for (const c of column.querySelectorAll('input:not([type=hidden]):not([type=file]), select, textarea')) {
                if (seen.has(c) || !shown(c)) continue;
                const kind = c.tagName === 'SELECT' ? 'select' : c.tagName === 'TEXTAREA' ? 'textarea' : c.type === 'radio' ? 'radio' : c.type === 'checkbox' ? 'checkbox' : 'text';
                const aria = clean(c.getAttribute('aria-label'));
                if (kind === 'checkbox' && (/top choice/i.test(aria) || /^follow /i.test(aria))) continue;
                const fieldset = c.closest('fieldset');
                const radios = kind === 'radio' ? [...(fieldset || column).querySelectorAll(`input[type=radio][name="${CSS.escape(c.name)}"]`)] : [];
                radios.forEach(r => seen.add(r)); seen.add(c);
                // The résumé cards are a radio group too, each named for its file.
                if (kind === 'radio' && (fieldset?.querySelector('[componentkey=easyApplyUploadedResumeRef]') || /\.(pdf|docx?|rtf|txt)$/i.test(aria))) continue;
                const box = c.closest('[componentkey^=easyApplyFieldFocus]') || fieldset?.parentElement || c.parentElement?.parentElement || c;
                // The question's own <p>: inside a radio group's box, or just before a text box's.
                const lead = box.querySelector(':scope > p') || box.parentElement?.querySelector(':scope > p');
                let text = (kind === 'radio' ? '' : said(root.querySelector(`label[for="${CSS.escape(c.id)}"]`))) || aria || clean(lead?.innerText);
                const starred = /\*\s*$/.test(text) || /\*\s*$/.test(clean(lead?.innerText));
                text = text.replace(/\s*\*\s*$/, '').trim();
                if (!text) continue;
                // A work-history row open for editing (#408): its "City" is that job's, not the
                // person's, so the label says whose — "Work experience — <title> — <company> — City".
                const editor = editorOf(c);
                if (editor) text = `${editor.section} — ${editor.title} — ${editor.company} — ${text}`;
                const options = kind === 'select' ? [...c.options].map(o => o.text.trim()).filter(t => t && !/^select an option$/i.test(t))
                  : radios.map(optionText);
                const filled = kind === 'select' ? !!c.value && !/^select an option$/i.test(c.options[c.selectedIndex]?.text.trim() || '')
                  : kind === 'radio' ? radios.some(r => r.checked) : kind === 'checkbox' ? c.checked : (c.value || '').trim().length > 0;
                const required = starred || c.required || c.getAttribute('aria-required') === 'true' || radios.some(r => r.required || r.getAttribute('aria-required') === 'true');
                const helper = box.querySelector('[data-testid=text-input-helper-text] > p');
                // A textarea carries the focus key itself, so its helper is not inside "box"; the
                // control names it in aria-describedby either way (#408's Description went unflagged).
                const described = c.getAttribute('aria-describedby') ? root.getElementById?.(c.getAttribute('aria-describedby'))?.querySelector(':scope > p') : null;
                const after = fieldset?.nextElementSibling?.tagName === 'P' ? fieldset.nextElementSibling : null;
                const invalid = c.getAttribute('aria-invalid') === 'true' || [helper, described, after].some(p => p && errorish.test(p.innerText || ''));
                out.push({ Label: text, Kind: kind, Required: required, Filled: filled, Options: options, ControlId: c.id || '', Invalid: invalid });
              }

              // Work history the employer's form asks for (SMX via JobDiva, #408): LinkedIn lists
              // the profile's roles as cards, "N/A" where the profile holds nothing, and opens one
              // for editing — City*, Description* — each time Next finds it short. Every blank
              // card is a question now, so one run hands all of them back.
              for (const edit of column.querySelectorAll("button[aria-label^='Edit, ']")) {
                const card = edit.parentElement;
                const pairs = [...card.querySelectorAll('p')].filter(p => p.nextElementSibling?.tagName === 'P' && !p.previousElementSibling)
                  .map(p => [clean(p.innerText), clean(p.nextElementSibling.innerText)]);
                if (pairs.length < 2) continue;
                const section = sectionOf(edit.getAttribute('aria-label'));
                for (const [name, value] of pairs.slice(2))
                  if (!value || /^n\/a$/i.test(value))
                    out.push({ Label: `${section} — ${pairs[0][1]} — ${pairs[1][1]} — ${name}`, Kind: /description/i.test(name) ? 'textarea' : 'text',
                      Required: true, Filled: false, Options: [], ControlId: '', Invalid: false, Pending: true });
              }
              return JSON.stringify(out);

              function sectionOf(s) { return /experience/i.test(s || '') ? 'Work experience' : /education/i.test(s || '') ? 'Education' : clean(s).replace(/^edit,?\s*/i, ''); }
              // The row editor: a block whose header line reads "Edit experience" / "Edit education",
              // its first two text boxes the row's title and company (school and degree).
              function editorOf(c) {
                for (let a = c.parentElement, depth = 0; a && a !== modal && depth < 12; a = a.parentElement, depth++) {
                  const head = a.firstElementChild?.querySelector(':scope > p');
                  if (!head || !/^edit (experience|education)\b/i.test(clean(head.innerText))) continue;
                  const boxes = [...a.querySelectorAll('input[type=text]')];
                  if (boxes.length < 2) return null;
                  return { section: sectionOf(head.innerText), title: (boxes[0].value || '').trim(), company: (boxes[1].value || '').trim() };
                }
                return null;
              }
            }
            """);
        return JsonSerializer.Deserialize<List<Field>>(json) ?? [];
    }
}
