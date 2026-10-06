// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;
using Microsoft.Playwright;

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>
/// Reads an application form the ATS does not publish — Lever and Ashby, and with
/// the tenant's opt-in the unknown long tail — by visiting it in the browser and
/// enumerating its controls: accessible name, field name, type, options, required.
/// <b>Read-only</b>: it never types and never clicks Submit. The result is the same
/// <see cref="PacketQuestion"/> list Greenhouse's API gives, so the answer drafter
/// and the submitter need no per-ATS code.
/// </summary>
public sealed partial class FormDiscoverer
{
    [GeneratedRegex(@"gender|race|ethnic|veteran|disabilit|pronoun|sexual orientation|self[- ]identif", RegexOptions.IgnoreCase)]
    private static partial Regex Eeo();

    [GeneratedRegex(@"\b(first|last|full)?\s*name\b|e-?mail|\bphone\b|resume|résumé|\bcv\b|cover letter|linkedin|github|portfolio|website|location|city", RegexOptions.IgnoreCase)]
    private static partial Regex StandardLabel();

    private readonly BrowserOptions _options;
    private readonly ILogger<FormDiscoverer> _log;

    public FormDiscoverer(BrowserOptions options, ILogger<FormDiscoverer> log)
    {
        _options = options;
        _log = log;
    }

    // What the page script hands back per control.
    private sealed record Control(
        string Id, string Name, string Label, string Tag, string Type, bool Required, List<string> Options);

    /// <param name="preScreen">Answers a pre-screening step's questions so discovery can go
    /// through it to the real form (Paycor asks about sponsorship on a page of radios and a
    /// Continue before it shows the form, #239). Null leaves any such gate in place — the old
    /// behaviour, which read the gate's questions as if they were the form's.</param>
    /// <summary>The form's questions, or null when the page had no fillable form.</summary>
    /// <param name="postingText">Handed the posting's rendered text, when there was any.</param>
    public async Task<List<PacketQuestion>?> DiscoverAsync(string link, CancellationToken ct = default,
        IReadOnlyList<BoardAccount>? accounts = null, Func<PacketQuestion, string?>? preScreen = null,
        Action<string>? postingText = null)
    {
        var opened = await BrowserSession.OpenAsync(_options, link, ct, accounts);
        if (opened.PostingText.Length > 0) postingText?.Invoke(opened.PostingText);
        // An employer page that names an Ashby job (?ashby_jid=) and shows no form for it:
        // Ashby's own hosted copy is the form (Hercules, #434).
        opened = await BrowserSession.AshbyHostedInsteadAsync(_options, opened, link, ct, accounts);
        await using var session = opened;
        // The form is often not there yet (Ashby fetches it after the page is idle) and often
        // not in the page at all (Comeet loads it into a cross-origin iframe): wait for a
        // control to show anywhere, then read every frame, top document first.
        await BrowserSession.WaitForFieldAsync(session.Page, 10_000);
        // The board's sign-in, not the form: nothing to discover (#216).
        if (await BrowserSession.SignInFormVisibleAsync(session.Page))
        {
            _log.LogInformation("discovery at {Link}: {Reason}", link, session.RevealNote.Length > 0 ? session.RevealNote : "the page is a sign-in");
            return null;
        }
        // A pre-screening step before the form — a page of radio/checkbox questions and a
        // Continue (#239): answer it from the tenant's standing answers and go through to the
        // real form, so the packet describes that form and not the gate. The gate's questions
        // lead the packet's list, so the person can see and correct what was answered for them.
        var gate = preScreen is not null
            ? await BrowserSubmitter.AdvancePreScreenAsync(session.Page, preScreen, _log)
            : [];
        var questions = new List<PacketQuestion>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in gate.Concat(await ReadQuestionsAsync(session.Page, _log)))
            if (seenKeys.Add(q.Id))
                questions.Add(q);
        await ReadChoicesAsync(session.Page, questions, _log);
        return questions.Count == 0 ? null : questions;
    }

    /// <summary>The most options a menu may have and still be recorded as the question's fixed
    /// set: past this it is a lookup (a country or school list), answered by typing into it.</summary>
    private const int MaxMenuOptions = 60;

    /// <summary>
    /// Read the menu of every combobox the enumeration could only call text: open it, read its
    /// options, close it. Greenhouse draws its custom questions as react-select widgets — an
    /// &lt;input role=combobox&gt; with no options in the DOM until opened — so "Are you legally
    /// authorized…?" (Yes / No) and "Which of these languages?" (Ruby on Rails / Rust / Both /
    /// Neither) reached the drafter as free text, it wrote prose, and no prose is a choice on a
    /// menu (Fieldwire, Stripe, #432). Read-only: a menu is opened and closed, nothing chosen.
    /// A menu longer than <see cref="MaxMenuOptions"/>, or empty until typed into (a location
    /// lookup), leaves the question as it was.
    /// </summary>
    public static async Task ReadChoicesAsync(IPage page, List<PacketQuestion> questions, ILogger? log = null)
    {
        for (var n = 0; n < questions.Count; n++)
        {
            var q = questions[n];
            if (q.Options.Count > 0 || q.Type != PacketQuestion.Text || q.Kind == PacketQuestion.Eeo) continue;
            foreach (var frame in page.Frames)
            {
                if (frame.IsDetached) continue;
                try
                {
                    var box = frame.Locator($"input[id=\"{q.Id.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"][role=combobox]");
                    if (await box.CountAsync() != 1 || !await box.IsVisibleAsync()) continue;
                    // Once more when the menu came up empty: the first open on a freshly
                    // rendered form can land before the widget is listening.
                    var menu = await OpenMenuAsync(frame, box);
                    if (menu.Options.Count == 0) menu = await OpenMenuAsync(frame, box);
                    if (menu.Options.Count is > 0 and <= MaxMenuOptions)
                        questions[n] = q with { Type = menu.Multi ? PacketQuestion.MultiSelect : PacketQuestion.Select, Options = menu.Options };
                    break;
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
                {
                    log?.LogInformation("choices for {Id}: {Reason}", q.Id, ex.Message.Split('\n')[0]);
                    try { await page.Keyboard.PressAsync("Escape"); } catch (PlaywrightException) { /* gone */ }
                }
            }
        }
    }

    /// <summary>Open one combobox's menu and read it: the option texts, and whether it takes
    /// several (react-select marks a multi widget's value container <c>--is-multi</c>).</summary>
    private static async Task<(List<string> Options, bool Multi)> OpenMenuAsync(IFrame frame, ILocator box)
    {
        await box.ClickAsync(new() { Timeout = 3_000 });
        var owned = await box.GetAttributeAsync("aria-controls");
        var options = string.IsNullOrWhiteSpace(owned)
            ? frame.Locator("[role=option]:visible")
            : frame.Locator($"[id=\"{owned}\"] [role=option]");
        var texts = new List<string>();
        for (var i = 0; i < 12 && texts.Count == 0; i++)
        {
            await frame.WaitForTimeoutAsync(250);
            texts = [.. (await options.AllInnerTextsAsync()).Select(t => t.Replace('\n', ' ').Trim()).Where(t => t.Length > 0)];
        }
        var multi = await box.EvaluateAsync<bool>("""
            el => { for (let a = el.parentElement, i = 0; a && i < 4; a = a.parentElement, i++)
                      if (a.matches('[class*="is-multi"]')) return true;
                    const owned = el.getAttribute('aria-controls');
                    return !!owned && document.getElementById(owned)?.getAttribute('aria-multiselectable') === 'true'; }
            """);
        await frame.Page.Keyboard.PressAsync("Escape");
        await box.EvaluateAsync("el => el.blur()");
        return ([.. texts.Distinct()], multi);
    }

    /// <summary>Every fillable control on a page already open, top document first, mapped to
    /// questions — the read <see cref="DiscoverAsync"/> does, exposed so a pre-screening step
    /// can be read off the page the submitter is already on (#239).</summary>
    public static async Task<List<PacketQuestion>> ReadQuestionsAsync(IPage page, ILogger? log = null)
    {
        var controls = new List<Control>();
        foreach (var frame in page.Frames)
        {
            JsonElement raw;
            try
            {
                raw = await frame.EvaluateAsync<JsonElement>(EnumerateScript);
            }
            catch (PlaywrightException ex)
            {
                log?.LogInformation("enumerate: {Reason}", ex.Message.Split('\n')[0]);
                continue;
            }
            controls.AddRange(JsonSerializer.Deserialize<List<Control>>(raw.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []);
        }
        return Map(controls);
    }

    /// <summary>Turn raw controls into questions. Public for tests.</summary>
    public static List<PacketQuestion> Map(IEnumerable<(string Id, string Name, string Label, string Tag, string Type, bool Required, List<string> Options)> controls) =>
        Map(controls.Select(c => new Control(c.Id, c.Name, c.Label, c.Tag, c.Type, c.Required, c.Options)).ToList());

    private static List<PacketQuestion> Map(List<Control> controls)
    {
        var questions = new List<PacketQuestion>();
        var seen = new Dictionary<string, string>();   // key → the label it was first seen with
        foreach (var c in controls)
        {
            var label = PacketQuestion.CleanLabel(c.Label);
            // A control with neither name nor id — Comeet's custom questions — is keyed by its
            // label; the submitter finds it by that label, and the required-empty sweep
            // reports it under the same key.
            var key = c.Name.Length > 0 ? c.Name : c.Id.Length > 0 ? c.Id : label;
            if (key.Length == 0) continue;
            // The same control read twice (two frames, a re-render) collapses. A repeated
            // key under a DIFFERENT label is another box that is keyed by its label rather
            // than dropped: Zoho's City, State/Province and Zip all carry id="inputId", and
            // only City survived (#207). The submitter finds the others by their label.
            if (seen.TryGetValue(key, out var firstLabel))
            {
                if (label.Length == 0 || label == firstLabel || seen.ContainsKey(label)) continue;
                key = label;
            }
            seen[key] = label;
            var type = (c.Tag, c.Type) switch
            {
                ("textarea", _) => PacketQuestion.Textarea,
                ("select", "select-multiple") => PacketQuestion.MultiSelect,
                ("select", _) => PacketQuestion.Select,
                (_, "file") => PacketQuestion.File,
                // Boxes that share a name are one "select all that apply" (#431).
                (_, "checkbox") => c.Options.Count > 1 ? PacketQuestion.MultiSelect : PacketQuestion.Select,
                (_, "radio") => PacketQuestion.Select,
                _ => PacketQuestion.Text,
            };
            var options = c.Options.Where(o => o.Trim().Length > 0).Select(o => o.Trim()).Distinct().ToList();
            if (c.Type == "checkbox" && options.Count == 0) options = ["Yes", "No"];
            var kind = Eeo().IsMatch(label) ? PacketQuestion.Eeo
                : StandardLabel().IsMatch(label) || StandardLabel().IsMatch(key) ? PacketQuestion.Standard
                : PacketQuestion.Custom;
            questions.Add(new PacketQuestion(key, label.Length > 0 ? label : key, c.Required && kind != PacketQuestion.Eeo, type, options, kind));
        }
        return questions;
    }

    // Runs in the page: every visible, enabled control with its accessible name.
    // Radio groups collapse to one control keyed by name with the labels as options;
    // hidden/submit/button inputs are skipped.
    private static readonly string EnumerateScript = """
        () => {
          const widget = __WIDGET__;
          // Every control, in the document and in any open shadow root (Manatal, #436); a
          // control's label is looked up in its own root, where its <label for> lives.
          const deep = __DEEP__;
          const rootOf = el => { const r = el.getRootNode(); return r && r !== document && r.querySelector ? r : document; };
          const byId = (el, i) => (rootOf(el).getElementById ? rootOf(el).getElementById(i) : null) || document.getElementById(i);
          const out = [];
          const seenRadio = new Map();
          const seenBox = new Map();
          // A placeholder that only says what to do ("Enter", "Select…") names nothing: Fullstack's
          // talent portal puts "Enter" in every box and titles each with the text above it, and
          // every question came back "Enter", optional, and unanswered (#439).
          const generic = p => /^\s*(?:enter|type|select|choose|pick|write|input|add)(?:\s+(?:here|a value|value|text|an? option|one|answer|your answer))?\s*(?:\.{3}|…)?\s*$/i.test(p || '');
          const labelFor = (el) => {
            if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
            const by = el.getAttribute('aria-labelledby');
            if (by) { const t = by.split(/\s+/).map(i => byId(el, i)?.innerText || '').join(' ').trim(); if (t) return t; }
            if (el.id) { const l = rootOf(el).querySelector(`label[for="${CSS.escape(el.id)}"]`); if (l) return l.innerText; }
            // A dropzone's own label is its instructions, not the field: Ethos wraps its hidden
            // résumé input in <label>Click or drag & drop PDF</label> under a sibling
            // <label>CV / Resume *</label>, and the résumé went unrecognised (#417). The row's
            // title, found below, names it.
            const wrap = el.closest('label');
            if (wrap && !(el.type === 'file' && (__DROPZONE__)(wrap.innerText || ''))) return wrap.innerText;
            const legend = el.closest('fieldset')?.querySelector('legend'); if (legend) return legend.innerText;
            // A label the page never associated with its control. Zoho Recruit's form is
            // web components: the input has no id, no <label for>, no aria — but the
            // component around it names the field in an attribute (cx-prop-label="Country")
            // and the row above it carries a plain <label>. Without this every Zoho field
            // came back labelled by its opaque name (rec-form_5042…), the model guessed
            // answers for boxes it could not see, and certifications went into LinkedIn (#207).
            // Deep: Zoho's City box sits nine wrappers below its component.
            for (let a = el.parentElement, i = 0; a && a !== document.body && i < 12; a = a.parentElement, i++) {
              for (const attr of a.attributes) {
                // label / data-label / cx-prop-label — not aria-label (a group's name, not the
                // field's), not a framework binding (lt-prop-label="value") or a prefix ("@").
                if (!/^(?:label|data-label|[\w-]*prop-label)$/i.test(attr.name)) continue;
                const v = attr.value.trim();
                if (v && /[A-Za-z]/.test(v) && !/^[a-z_]+$/.test(v)) return v;
              }
              // The row's own <label> counts only while this is the row's one control:
              // one level higher and it would be the first label of the whole form. A label
              // with no letters in it is decoration (the "@" before a Twitter handle).
              const controls = [...a.querySelectorAll('input, select, textarea')]
                .filter(c => !['hidden', 'submit', 'button', 'reset', 'image'].includes((c.getAttribute('type') || '').toLowerCase()) && (c === el || visible(c)));
              if (controls.length > 1) break;
              const l = [...a.querySelectorAll('label')].find(x => !x.contains(el) && /[A-Za-z]/.test(x.innerText || ''));
              if (l) return l.innerText.trim();
            }
            const ph = el.getAttribute('placeholder') || '';
            return (generic(ph) ? '' : ph) || precedingTitle(el) || el.getAttribute('name') || ph;
          };
          // A field titled by plain text above it and nothing else: no aria, no <label for>,
          // no wrapping <label>, no <legend>. Workable renders a screening question as a
          // <div> of text over an input whose id *and* name are the same generated hex, so
          // the question came back named by that hex — the drafter answered a question it
          // could not read, and the person saw one too (Stevens, Pinewood; #280). The title
          // is the nearest text before the control, inside a box holding no other control:
          // the same rule groupTitle uses for a radio group, for one field.
          const precedingTitle = (el) => {
            for (let a = el.parentElement, i = 0; a && a !== document.body && i < 6; a = a.parentElement, i++) {
              const controls = [...a.querySelectorAll('input, select, textarea')]
                .filter(c => !['hidden', 'submit', 'button', 'reset', 'image'].includes((c.getAttribute('type') || '').toLowerCase()) && (c === el || visible(c)));
              if (controls.length > 1) break;
              const title = [...a.querySelectorAll('*')].find(x =>
                x !== el && !x.contains(el) && !x.querySelector('input, select, textarea')
                && (x.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_FOLLOWING)
                && /[A-Za-z]{3,}/.test(x.innerText || '')
                && [...x.children].every(c => !/[A-Za-z]{3,}/.test(c.innerText || '')));
              if (!title) continue;
              // Workable puts the question in one <span> and the required star in its
              // aria-hidden sibling, so the title itself carries no star — take the box
              // around it when that is all the box adds, and the star is read as required.
              const box = title.parentElement;
              const whole = box && !box.querySelector('input, select, textarea')
                && /^\s*\*|\*\s*$/.test((box.innerText || '').trim()) ? box : title;
              return whole.innerText.replace(/\s+/g, ' ').trim();
            }
            return '';
          };
          // The required star when it sits in the row's <label> rather than on the control or
          // in the label the component names itself by ("Last Name" + <span>*</span>).
          const starred = (el) => {
            for (let a = el.parentElement, i = 0; a && a !== document.body && i < 12; a = a.parentElement, i++) {
              const l = [...a.querySelectorAll('label')].find(x => !x.contains(el) && /[A-Za-z]/.test(x.innerText || ''));
              if (l) return /^\s*\*|\*\s*$/.test((l.innerText || '').trim());
            }
            return false;
          };
          const visible = (el) => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          // Ashby names a question in a <label class="…ashby-application-form-question-title">,
          // even for a radio group in a <fieldset> (there is no <legend>), and says it is required
          // only with a generated _required_ class on that label — never `required` on the radios,
          // the yes/no or the typeahead. Read as a legend-less fieldset, a required screening
          // question came back labelled by its radios' name — a UUID — and optional, so nobody
          // answered it and the application was clicked through to a silent rejection (#280).
          const ashbyTitle = (el) => el.closest('fieldset, [class*="_fieldEntry_"], .ashby-application-form-field-entry')
            ?.querySelector('.ashby-application-form-question-title') || null;
          const ashbyRequired = (el) => /(^|\s)_required_/.test(ashbyTitle(el)?.className?.toString() || '');
          // A radio group titled by plain text above its options — ClearCompany's
          // <span class="control-label">Are you authorized…? <span>*</span></span> over a div of
          // <label><input type=radio> Yes</label> rows, no <fieldset>, no <legend> — came back
          // labelled by its radios' name, a UUID, and the drafter answered a question it could
          // not read. The title is the first text inside the group's own box (an ancestor holding
          // no other group's radios) that comes before the first option and belongs to no option.
          const groupTitle = (el) => {
            const name = el.getAttribute('name') || '';
            for (let a = el.parentElement, i = 0; a && a !== document.body && i < 8; a = a.parentElement, i++) {
              const choices = [...a.querySelectorAll('input[type=radio], input[type=checkbox]')];
              if (choices.some(c => (c.getAttribute('name') || '') !== name)) break;
              const first = choices[0];
              const title = [...a.querySelectorAll('*')].find(x =>
                // Not an option's own label — but a <label> that holds no control is the title itself
                // (evlo: <label>Will you … sponsorship …? <span>*</span></label> over a radiogroup).
                // and never a label bound to some control by `for` (an option's "Yes" set before its radio).
                x !== first && !x.contains(first) && !x.closest('label')?.querySelector('input, select, textarea') && !x.closest('label[for]') && !x.querySelector('input, select, textarea')
                && (x.compareDocumentPosition(first) & Node.DOCUMENT_POSITION_FOLLOWING)
                && /[A-Za-z]{3,}/.test(x.innerText || '')
                && [...x.children].every(c => !/[A-Za-z]{3,}/.test(c.innerText || '')));
              if (title) return title.innerText.replace(/\s+/g, ' ').trim();
            }
            return '';
          };
          for (const el of deep('input, select, textarea')) {
            const type = (el.getAttribute('type') || (el.tagName === 'SELECT' ? (el.multiple ? 'select-multiple' : 'select-one') : 'text')).toLowerCase();
            if (['hidden', 'submit', 'button', 'reset', 'image'].includes(type)) continue;
            if (el.disabled || el.readOnly) continue;
            // Ashby draws its own radio and checkbox and keeps the real input out of sight; so does
            // Oracle Recruiting, whose 0×0 radio is pressed through the <label for> drawn beside it —
            // read as invisible, every yes/no question on DTCC's form went undiscovered (#318).
            const drawn = (type === 'radio' || type === 'checkbox')
              && (!!ashbyTitle(el) || (!!el.id && [...rootOf(el).querySelectorAll(`label[for="${CSS.escape(el.id)}"]`)].some(visible)));
            if (type !== 'file' && !drawn && !visible(el)) continue;
            // The careers site's own job search and job-alert boxes are not questions (#214).
            if (widget(el)) continue;
            // Nor is a honeypot, on screen for bots and out of a person's reach: Oracle's aria-hidden,
            // tabindex -1 "honey-pot" became a question the model could not answer and blocked DTCC.
            if ((el.getAttribute('aria-hidden') === 'true' && el.tabIndex < 0) || /honey-?pot|beecatcher/i.test((el.id || '') + ' ' + (el.getAttribute('name') || ''))) continue;
            const required = el.required || el.getAttribute('aria-required') === 'true' || ashbyRequired(el) || /^\s*\*|\*\s*$/.test(labelFor(el)) || starred(el);
            if (type === 'radio') {
              const name = el.getAttribute('name') || '';
              const grp = seenRadio.get(name);
              const opt = ((el.id && rootOf(el).querySelector(`label[for="${CSS.escape(el.id)}"]`)?.innerText) || el.closest('label')?.innerText || el.value || '').trim();
              if (grp) { grp.options.push(opt); grp.required = grp.required || required; continue; }
              // Ashby's group name is "<form instance>_<field>" with the first half new on every
              // load; its title's `for` is the field's own id, the same every time. The question is
              // recorded by that, or the packet names a control the next load will not have.
              const stable = ashbyTitle(el)?.getAttribute('for') || '';
              const title = el.closest('fieldset')?.querySelector('legend')?.innerText || ashbyTitle(el)?.innerText?.trim() || groupTitle(el)
                || el.closest('[role=radiogroup]')?.getAttribute('aria-label') || name;
              // The star on the group's title is the group's: its options' own labels ("Yes") carry none.
              const entry = { id: stable ? '' : (el.id || ''), name: stable || name, label: title, tag: 'input', type: 'radio', required: required || /^\s*\*|\*\s*$/.test(title) || el.closest('[role=radiogroup]')?.getAttribute('aria-required') === 'true', options: [opt] };
              seenRadio.set(name, entry); out.push(entry); continue;
            }
            // Boxes sharing a name are one "select all that apply" — Greenhouse's
            // <fieldset><legend>Cloud tools?</legend> over question_N[] boxes. Read box by box,
            // each became its own Yes/No question, the first keyed by the group and labelled by
            // its first option ("AWS"), and none of them could be answered as the form asks (#431).
            // One question, titled by the group, its boxes' labels the options, as a radio group is.
            if (type === 'checkbox') {
              const name = el.getAttribute('name') || '';
              const members = name ? rootOf(el).querySelectorAll(`input[type=checkbox][name="${CSS.escape(name)}"]`).length : 0;
              if (members > 1) {
                const opt = ((el.id && rootOf(el).querySelector(`label[for="${CSS.escape(el.id)}"]`)?.innerText) || el.closest('label')?.innerText || el.value || '').trim();
                const grp = seenBox.get(name);
                if (grp) { grp.options.push(opt); grp.required = grp.required || required; continue; }
                const stable = ashbyTitle(el)?.getAttribute('for') || '';
                const title = (el.closest('fieldset')?.querySelector('legend')?.innerText || ashbyTitle(el)?.innerText || groupTitle(el) || name).trim();
                const entry = { id: '', name: stable || name, label: title, tag: 'input', type: 'checkbox',
                  required: required || /^\s*\*|\*\s*$/.test(title) || el.closest('fieldset')?.getAttribute('aria-required') === 'true', options: [opt] };
                seenBox.set(name, entry); out.push(entry); continue;
              }
            }
            // A blank-valued option is the placeholder ("Select…"), not a choice — and so is a first
            // option that reads as one whatever its value: HubSpot's "-- Select" is value="-- Select" (#425).
            const options = el.tagName === 'SELECT' ? [...el.options].filter((o, i) => o.value !== ''
              && !(i === 0 && /^\s*-*\s*(?:select|choose|please (?:select|choose)|pick)\b/i.test(o.text || ''))).map(o => o.text.trim()) : [];
            out.push({ id: el.id || '', name: el.getAttribute('name') || '', label: labelFor(el).trim(), tag: el.tagName.toLowerCase(), type, required, options });
          }
          // A choice drawn as buttons with no input at all: Oracle Recruiting's "pills" — a
          // <ul role=radiogroup aria-label="How did you hear about this position?"> of
          // <button role=radio> — for its sponsorship, education and referral questions (#318).
          // Keyed by the question itself: the group has neither name nor id.
          for (const g of deep('[role=radiogroup]')) {
            if (g.querySelector('input') || !visible(g) || widget(g)) continue;
            const pills = [...g.querySelectorAll('[role=radio]')];
            if (pills.length === 0) continue;
            const by = (g.getAttribute('aria-labelledby') || '').split(/\s+/).map(i => document.getElementById(i)?.innerText || '').join(' ');
            const label = (g.getAttribute('aria-label') || by).trim();
            if (!label) continue;
            const row = g.closest('.input-row');
            const required = g.getAttribute('aria-required') === 'true' || !!row?.querySelector('.input-row__label--required') || /\*\s*$/.test(label);
            out.push({ id: '', name: '', label, tag: 'input', type: 'radio', required, options: pills.map(b => (b.innerText || '').trim()) });
          }
          return out;
        }
        """.Replace("__WIDGET__", BrowserSession.WidgetJs).Replace("__DROPZONE__", BrowserSession.DropzoneJs).Replace("__DEEP__", BrowserSession.DeepJs);
}
