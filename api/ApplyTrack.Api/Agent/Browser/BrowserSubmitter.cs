// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;
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

    /// <param name="resumeText">The résumé as text, for boards whose uploader will not take the
    /// file — their own "Enter manually" box accepts it. Empty disables that fallback.</param>
    public async Task<SubmitOutcome> RunAsync(
        string link, AgentPacket packet, (byte[] Bytes, string Name)? resumePdf, bool dryRun,
        CancellationToken ct = default, string resumeText = "")
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

            // Let the form finish arriving before typing into it. Greenhouse's form fetches a
            // candidate profile after it renders and writes the name fields from the (empty)
            // reply when it lands — which, mid-fill, emptied first and last name that had just
            // been typed. Bounded: a page that never goes idle is filled anyway.
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); }
            catch (TimeoutException) { /* analytics beacons and the like; carry on */ }

            foreach (var q in packet.Questions)
            {
                ct.ThrowIfCancellationRequested();
                if (q.Kind == PacketQuestion.Eeo)
                    continue;
                if (q.Type == PacketQuestion.File)
                {
                    if (q.Id.Contains("resume", StringComparison.OrdinalIgnoreCase) && resumePdf is { } pdf)
                    {
                        if (await AttachResumeAsync(page, q, pdf, resumeText)) mapped.Add(q.Id);
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

            // Anything the page emptied behind our back gets one more go before it is judged.
            foreach (var (key, label) in await RequiredEmptyAsync(page))
            {
                var q = FindQuestion(packet, key, label);
                if (q is null || !mapped.Contains(q.Id) || q.Type == PacketQuestion.File
                    || !packet.Answers.TryGetValue(q.Id, out var again) || string.IsNullOrWhiteSpace(again))
                    continue;
                await FillAsync(page, q, again);
            }

            // Now ask the FORM what is still missing, not just the packet. The packet is the ATS
            // API's idea of the form and the two drift: Greenhouse's rendered form carries a
            // required Country picker its Job Board API never mentions, its city field is an
            // autocomplete that only counts once a suggestion is chosen, and its custom selects
            // are react-select widgets that look filled after typing and are not. Every one of
            // those produced "9 mapped, 0 unmapped", a clean dry run, an automatic promotion,
            // and a Submit click into a wall of client-side validation with no POST ever sent.
            // Anything the page still marks required-and-empty is unmapped, whatever we thought.
            foreach (var (key, label) in await RequiredEmptyAsync(page))
            {
                var id = FindQuestion(packet, key, label)?.Id ?? key;
                mapped.Remove(id);
                if (!unmapped.Contains(id)) unmapped.Add(id);
            }

            screenshot = await session.ScreenshotAsync();
            // Reported on a dry run too: the dry run's whole job is to find out whether this
            // posting can be finished unattended, and with a captcha in the way the answer is no.
            if (await HasCaptchaAsync(page))
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                    "this form is guarded by a captcha — finish it with Copy answers and open",
                    Captcha: true);
            // A form with fields on it and not one of them filled is not "clean", it is a packet
            // that describes some other form. Seen in production: zero mapped, zero unmapped,
            // and a run that would have clicked Submit on an empty application.
            if (mapped.Count == 0 && await HasFillableControlsAsync(page))
                return new SubmitOutcome(false, false, page.Url, "", screenshot, unmapped, mapped,
                    "nothing on this form could be filled — the packet's questions match none of its fields");
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
            {
                // A challenge thrown up BY the click is the common case, not the rare one: the
                // form looks clean right up to Submit and only then demands "click the object
                // that does not fit the column pattern". Checking only before the click filed
                // nine of these as mystery failures to retry forever, when every one of them
                // was a form asking for a person.
                if (await HasCaptchaAsync(page))
                    return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                        "a captcha appeared on Submit — finish it with Copy answers and open",
                        Captcha: true);
                // The form's own validation messages are the diagnosis; without them every
                // refused click reads as the same mystery and gets the same wrong theory.
                var complaints = await ValidationErrorsAsync(page);
                if (complaints.Count > 0)
                    return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                        "the form rejected the submission: " + string.Join("; ", complaints));
                if (await FormResetAsync(page, packet, mapped))
                    return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                        "Submit was clicked and the form reset itself with no confirmation — check the screenshot");
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                    "Submit was clicked but no confirmation text was recognised — check the screenshot");
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

    /// <summary>The packet question a rendered field belongs to — by field id/name, else by label.</summary>
    private static PacketQuestion? FindQuestion(AgentPacket packet, string key, string label) =>
        packet.Questions.FirstOrDefault(x => Unprefixed(x.Id).Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? (label.Length == 0 ? null
            : packet.Questions.FirstOrDefault(x => x.Label.Trim().TrimEnd('*').Trim().Equals(label, StringComparison.OrdinalIgnoreCase)));

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
    private static async Task<bool> ChooseAsync(IPage page, ILocator control, PacketQuestion q, string answer)
    {
        var fixedSet = q.Options.Count > 0;
        if (fixedSet && !q.Options.Any(o => Same(o, answer)))
            return false;
        var combobox = await IsComboboxAsync(control);
        await control.ClickAsync();
        try { await control.FillAsync(answer); }
        catch (PlaywrightException) { await page.Keyboard.TypeAsync(answer); } // a div-based widget: type at it
        ILocator? pick = null;
        if (combobox)
        {
            for (var i = 0; i < 12 && pick is null; i++)
            {
                await page.WaitForTimeoutAsync(250);
                pick = await BestOptionAsync(page, control, answer, fixedSet);
            }
            // No menu ever showed: commit whatever is under the caret. Only on a combobox —
            // Enter in a plain text input submits the form it sits in.
            if (pick is null)
                await page.Keyboard.PressAsync("Enter");
        }
        if (pick is not null)
            await pick.ClickAsync();
        await page.WaitForTimeoutAsync(300);
        return await CommittedAsync(control, answer);
    }

    /// <summary>The option to click for an answer, or null while the menu has nothing usable:
    /// an exact match, else the shortest option that starts with the answer, else one that
    /// contains it, else (open autocompletes only) the first suggestion.</summary>
    private static async Task<ILocator?> BestOptionAsync(IPage page, ILocator control, string answer, bool fixedSet)
    {
        // Prefer the listbox the control says it owns; otherwise any option that is on screen.
        var owned = await control.GetAttributeAsync("aria-controls");
        var options = !string.IsNullOrWhiteSpace(owned)
            ? page.Locator($"#{CssEscape(owned)} [role=option]:visible")
            : page.Locator("[role=option]:visible");
        IReadOnlyList<string> texts;
        try { texts = await options.AllInnerTextsAsync(); }
        catch (PlaywrightException) { return null; }
        if (texts.Count == 0) return null;
        var want = answer.Trim();
        var norm = texts.Select(t => t.Replace('\n', ' ').Trim()).ToList();
        var i = norm.FindIndex(t => t.Equals(want, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            var starts = norm.Select((t, n) => (t, n))
                .Where(x => x.t.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.t.Length).ToList();
            if (starts.Count > 0) i = starts[0].n;
        }
        if (i < 0) i = norm.FindIndex(t => t.Contains(want, StringComparison.OrdinalIgnoreCase));
        if (i < 0 && !fixedSet) i = 0;
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
    private static async Task<List<(string Key, string Label)>> RequiredEmptyAsync(IPage page)
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

    private const string RequiredEmptyScript = """
        () => {
          const visible = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          const labelFor = el => {
            if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
            const by = el.getAttribute('aria-labelledby');
            if (by) { const t = by.split(/\s+/).map(i => document.getElementById(i)?.innerText || '').join(' ').trim(); if (t) return t; }
            if (el.id) { const l = document.querySelector(`label[for="${CSS.escape(el.id)}"]`); if (l) return l.innerText; }
            const wrap = el.closest('label'); if (wrap) return wrap.innerText;
            const legend = el.closest('fieldset')?.querySelector('legend'); if (legend) return legend.innerText;
            return el.getAttribute('placeholder') || '';
          };
          const out = []; const seen = new Set();
          const add = (key, label) => {
            label = (label || '').replace(/\*\s*$/, '').trim();
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
          for (const el of document.querySelectorAll('input, select, textarea')) {
            const type = (el.getAttribute('type') || (el.tagName === 'SELECT' ? 'select' : 'text')).toLowerCase();
            if (['hidden', 'submit', 'button', 'reset', 'image', 'file'].includes(type) || el.disabled) continue;
            if (!(el.required || el.getAttribute('aria-required') === 'true')) continue;
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
        """;

    /// <summary>Does the page have anything a person could type into or pick from?</summary>
    private static async Task<bool> HasFillableControlsAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => [...document.querySelectorAll('input, select, textarea')].some(el => {
                  const type = (el.getAttribute('type') || 'text').toLowerCase();
                  if (['hidden', 'submit', 'button', 'reset', 'image', 'file'].includes(type) || el.disabled) return false;
                  const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                  return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
                })
                """);
        }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>
    /// The validation messages the form is showing: alerts, the descriptions of invalid
    /// controls, and error-styled helper text. Short leaf text only — never a wrapper that
    /// contains a control — so what comes back reads like "Select a country", not the form.
    /// </summary>
    private static async Task<List<string>> ValidationErrorsAsync(IPage page)
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
    private static async Task<bool> FormResetAsync(IPage page, AgentPacket packet, List<string> mapped)
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
        candidates.Add(page.Locator($"[id$='-{id}']").First);
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
    private static async Task<bool> AttachResumeAsync(
        IPage page, PacketQuestion q, (byte[] Bytes, string Name) pdf, string resumeText)
    {
        var payload = new FilePayload { Name = pdf.Name, MimeType = "application/pdf", Buffer = pdf.Bytes };
        var id = Unprefixed(q.Id);
        var candidates = new ILocator?[]
        {
            page.Locator($"input[type=file]#{CssEscape(id)}").First,
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
    private static async Task ClearStagedResumeAsync(IPage page)
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
    private static async Task<ILocator?> OnlyFileInputAsync(IPage page)
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
    private static async Task<bool> EnterResumeManuallyAsync(IPage page, string resumeText)
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
            await trigger.ClickAsync();

            // Wait for the box rather than looking for it straight away: the widget renders it
            // after the click, and checking immediately finds nothing and gives up on a board
            // that was about to work. (On GitLab's form it is textarea#resume_text, which
            // carries no name attribute — so match on id as well.)
            var box = page.Locator("textarea[name*='resume' i], textarea[id*='resume' i]").First;
            try { await box.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8_000 }); }
            catch (TimeoutException)
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
    /// Did the attachment actually register? True when a file input holds the file or the page
    /// now names it, and no uploader error is on screen. The uploader is asynchronous, so give
    /// it a moment before deciding.
    /// </summary>
    private static async Task<bool> ResumeTookAsync(IPage page, string fileName)
    {
        // Poll rather than sleep once. The input reports its file immediately while the board's
        // uploader is still working, so a single early look says "attached" and a later one says
        // "error" — which is exactly how a dry run came back clean and the real run that followed
        // it refused. Wait for whichever lands first, and treat neither as failure.
        try
        {
            for (var i = 0; i < 10; i++)
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
                // Only trust "attached" once the uploader has had a chance to disagree.
                if (verdict == "attached" && i >= 2) return true;
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
