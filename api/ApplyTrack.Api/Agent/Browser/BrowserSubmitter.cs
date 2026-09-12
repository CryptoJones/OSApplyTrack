// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;
using Microsoft.Playwright;

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>What one browser run produced.</summary>
/// <param name="Filled">The form was reached and the mapped answers were typed in.</param>
/// <param name="Submitted">Submit was clicked AND a confirmation was recognised.</param>
/// <param name="Unmapped">Required questions with an answer that no field could be found for — any of these refuses the click.</param>
/// <param name="Closed">The posting was gone: the page says it is no longer open. The lead expired, it did not fail.</param>
/// <param name="Captcha">The form guards submit with an interactive captcha. Not a defect to retry and not
/// something to solve — the posting has to be finished by hand with Copy answers and open.</param>
public sealed record SubmitOutcome(
    bool Filled, bool Submitted, string Url, string Confirmation, byte[]? Screenshot,
    List<string> Unmapped, List<string> Mapped, string Error, bool Closed = false, bool Captcha = false);

/// <summary>
/// Drives the browser container at a posting: fill every mapped answer, attach the
/// résumé from memory, screenshot — and click Submit only when asked (<c>dry_run</c>
/// false) and only when every required question with an answer found its field.
/// Fields are found by accessible name first, then by the ATS field name — the
/// generic adapter — so Greenhouse, Lever, Ashby and (with the opt-in) the long tail
/// all go through the same code. Containment lives in <see cref="BrowserSession"/>.
/// </summary>
public sealed partial class BrowserSubmitter
{
    [GeneratedRegex(@"thank you|thanks for applying|application (?:has been |was )?(?:submitted|received|sent)|we(?:'ve| have) received your application|successfully (?:submitted|applied)|your application is in", RegexOptions.IgnoreCase)]
    private static partial Regex Confirmation();

    // A closed posting: the apply URL redirects to the board or shows a gone-notice. Matching
    // any of these means there is no form to fill — the lead expired between discovery and now,
    // which is an expected outcome, not a failure to fix. Kept specific so an open posting's
    // prose ("no longer supported", etc.) never trips it.
    [GeneratedRegex(@"job you are looking for is no longer open|no longer (?:open|accepting applications)|(?:position|posting|job|role|opening) (?:has been|was|is now) (?:filled|closed)|this (?:position|posting|job|role|opening) is (?:no longer available|closed)", RegexOptions.IgnoreCase)]
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

    public async Task<SubmitOutcome> RunAsync(
        string link, AgentPacket packet, (byte[] Bytes, string Name)? resumePdf, bool dryRun,
        CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
            throw new AppValidationException("browser submission isn't configured on this instance");

        await using var session = await BrowserSession.OpenAsync(_options, AtsProvider.ApplyUrl(link, packet.Provider), ct);
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
            var body = await BodyTextAsync(page);
            if (IsClosedPosting(body))
            {
                screenshot = await session.ScreenshotAsync();
                return new SubmitOutcome(false, false, page.Url, "", screenshot, unmapped, mapped,
                    "posting is no longer open", Closed: true);
            }

            foreach (var q in packet.Questions)
            {
                ct.ThrowIfCancellationRequested();
                if (q.Kind == PacketQuestion.Eeo)
                    continue;
                if (q.Type == PacketQuestion.File)
                {
                    if (q.Id.Contains("resume", StringComparison.OrdinalIgnoreCase) && resumePdf is { } pdf)
                    {
                        if (await AttachResumeAsync(page, q, pdf)) mapped.Add(q.Id);
                        else if (q.Required) unmapped.Add(q.Id);
                    }
                    else if (q.Required)
                        unmapped.Add(q.Id);
                    continue;
                }
                if (!packet.Answers.TryGetValue(q.Id, out var answer) || string.IsNullOrWhiteSpace(answer))
                    continue;
                if (await FillAsync(page, q, answer)) mapped.Add(q.Id);
                else if (q.Required) unmapped.Add(q.Id);
            }

            screenshot = await session.ScreenshotAsync();
            // Reported on a dry run too: the dry run's whole job is to find out whether this
            // posting can be finished unattended, and with a captcha in the way the answer is no.
            if (await HasCaptchaAsync(page))
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                    "this form is guarded by a captcha — finish it with Copy answers and open",
                    Captcha: true);
            if (dryRun)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped, "");
            if (unmapped.Count > 0)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                    "refused to submit: required fields could not be mapped (" + string.Join(", ", unmapped) + ")");

            var submit = await FindSubmitAsync(page);
            if (submit is null)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped, "no Submit button found");
            await submit.ClickAsync();
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 }); }
            catch (TimeoutException) { /* single-page confirmations never go idle; read what's there */ }
            await page.WaitForTimeoutAsync(1500);

            var text = await page.InnerTextAsync("body");
            var m = Confirmation().Match(text);
            screenshot = await session.ScreenshotAsync();
            if (!m.Success)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                    "Submit was clicked but no confirmation text was recognised — check the screenshot");
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

    /// <summary>Find the control for a question — by accessible name first, then by the
    /// ATS field name — and set it. False when nothing visible matched.</summary>
    private static async Task<bool> FillAsync(IPage page, PacketQuestion q, string answer)
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
        if (q.Type is PacketQuestion.Select or PacketQuestion.MultiSelect)
        {
            // A custom combobox (a div/input widget, not a native <select>). This used to type
            // the answer and return true unconditionally — so an answer that matches no option
            // (a model that replied "United States, U.S., USA" to a country picker, or "Python"
            // to a fixed specialisation list) was still counted mapped. The agent then judged a
            // form with empty required dropdowns "clean" and auto-submitted an invalid
            // application. When the question offers a fixed option set, the answer MUST be one of
            // them; otherwise report it unmapped so the run refuses to submit and asks the human.
            if (q.Options.Count > 0 && !q.Options.Any(o => o.Trim().Equals(answer.Trim(), StringComparison.OrdinalIgnoreCase)))
                return false;
            await control.ClickAsync();
            await control.FillAsync(answer);
            await page.Keyboard.PressAsync("Enter");
            return true;
        }
        await control.FillAsync(answer);
        return true;
    }

    /// <summary>
    /// Set a radio group or a checkbox: check the member whose value or label matches
    /// the answer. Null when the question is not one of those — the caller carries on
    /// with the text/select paths. False when it is one and nothing matched, so a
    /// required question still lands in <c>unmapped</c> rather than passing silently.
    /// </summary>
    private static async Task<bool?> SetChoiceAsync(IPage page, PacketQuestion q, string answer)
    {
        var id = Unprefixed(q.Id);
        var radios = page.Locator($"input[type=radio][name='{id}']");
        int count;
        try { count = await radios.CountAsync(); }
        catch (PlaywrightException) { return null; }
        if (count > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var radio = radios.Nth(i);
                try
                {
                    if (!Same(await radio.GetAttributeAsync("value") ?? "", answer)
                        && !Same(await ChoiceLabelAsync(radio), answer))
                        continue;
                    await radio.CheckAsync();
                    return true;
                }
                catch (PlaywrightException) { /* try the next member */ }
            }
            return false;
        }

        var box = page.Locator($"input[type=checkbox]#{CssEscape(id)}, input[type=checkbox][name='{id}']").First;
        try
        {
            if (await box.CountAsync() == 0) return null;
            await box.SetCheckedAsync(Affirmative(answer));
            return true;
        }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>The visible text tied to one radio/checkbox — its own label, or the one wrapping it.</summary>
    private static Task<string> ChoiceLabelAsync(ILocator choice) => choice.EvaluateAsync<string>("""
        el => (el.id && document.querySelector(`label[for="${CSS.escape(el.id)}"]`)?.innerText)
              || el.closest('label')?.innerText || ''
        """);

    private static bool Same(string a, string b) =>
        a.Trim().Length > 0 && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Affirmative(string answer) =>
        answer.Trim() is not ("" or "0") && !answer.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(answer.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    private static string Unprefixed(string id) =>
        id.StartsWith("std:", StringComparison.Ordinal) ? id[4..] : id;

    private static async Task<ILocator?> LocateAsync(IPage page, PacketQuestion q)
    {
        var id = Unprefixed(q.Id);
        var candidates = new List<ILocator>();
        if (q.Label.Length > 0)
            candidates.Add(page.GetByLabel(q.Label, new() { Exact = false }).First);
        candidates.Add(page.Locator($"#{CssEscape(id)}").First);
        candidates.Add(page.Locator($"[name='{id}']").First);
        candidates.Add(page.Locator($"[name$='[{id}]']").First);
        candidates.Add(page.Locator($"[name*='{id}']").First);
        foreach (var c in candidates)
        {
            try
            {
                if (await c.CountAsync() > 0 && await c.IsVisibleAsync() && await c.IsEditableAsync())
                    return c;
            }
            catch (PlaywrightException) { /* try the next */ }
        }
        return null;
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
    private static async Task<bool> AttachResumeAsync(IPage page, PacketQuestion q, (byte[] Bytes, string Name) pdf)
    {
        var payload = new FilePayload { Name = pdf.Name, MimeType = "application/pdf", Buffer = pdf.Bytes };
        var id = Unprefixed(q.Id);
        var candidates = new[]
        {
            page.Locator($"input[type=file]#{CssEscape(id)}").First,
            page.Locator($"input[type=file][name*='{id}']").First,
            page.Locator("input[type=file][name*='resume'], input[type=file][id*='resume']").First,
            page.Locator("input[type=file]").First,
        };
        foreach (var c in candidates)
        {
            try
            {
                if (await c.CountAsync() == 0) continue;
                // Never touches disk: the bytes go straight into the input.
                await c.SetInputFilesAsync(payload);
                if (await ResumeTookAsync(page, pdf.Name)) return true;
            }
            catch (PlaywrightException) { /* try the next */ }
        }
        return false;
    }

    /// <summary>
    /// Did the attachment actually register? True when a file input holds the file or the page
    /// now names it, and no uploader error is on screen. The uploader is asynchronous, so give
    /// it a moment before deciding.
    /// </summary>
    private static async Task<bool> ResumeTookAsync(IPage page, string fileName)
    {
        try
        {
            await page.WaitForTimeoutAsync(1200);
            return await page.EvaluateAsync<bool>("""
                name => {
                  const text = document.body.innerText || '';
                  if (/cannot read propert|uploadfile|upload failed|failed to upload|error uploading/i.test(text))
                    return false;
                  const inputs = [...document.querySelectorAll('input[type=file]')];
                  if (inputs.some(i => i.files && i.files.length > 0)) return true;
                  return text.includes(name);
                }
                """, fileName);
        }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>
    /// An interactive captcha standing between the filled form and Submit. Deliberately narrow:
    /// the invisible reCAPTCHA v3 badge rides along on a great many boards and never stops a
    /// submission, so anything inside <c>.grecaptcha-badge</c> must NOT count — only a challenge
    /// a person has to touch (the v2 checkbox, hCaptcha, Turnstile).
    /// </summary>
    private static async Task<bool> HasCaptchaAsync(IPage page)
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
                  const sel = 'iframe[src*="recaptcha/api2/anchor"], iframe[src*="hcaptcha.com/captcha"],'
                            + 'iframe[src*="challenges.cloudflare.com"], .h-captcha, .cf-turnstile';
                  return [...document.querySelectorAll(sel)].some(shown);
                }
                """);
        }
        catch (PlaywrightException) { return false; }
    }

    private static async Task<ILocator?> FindSubmitAsync(IPage page)
    {
        var candidates = new[]
        {
            page.Locator("#submit_app").First,
            page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"submit (?:application|your application)|^submit$|^apply$", RegexOptions.IgnoreCase) }).First,
            page.Locator("button[type=submit], input[type=submit]").First,
        };
        foreach (var c in candidates)
        {
            try { if (await c.CountAsync() > 0 && await c.IsVisibleAsync() && await c.IsEnabledAsync()) return c; }
            catch (PlaywrightException) { /* next */ }
        }
        return null;
    }

    private static string CssEscape(string id) => Regex.Replace(id, @"([^a-zA-Z0-9_-])", "\\$1");

    /// <summary>The page's visible text, or "" if it can't be read — never throws.</summary>
    private static async Task<string> BodyTextAsync(IPage page)
    {
        try { return await page.InnerTextAsync("body"); }
        catch (PlaywrightException) { return ""; }
    }
}
