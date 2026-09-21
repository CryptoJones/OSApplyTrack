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
    public async Task<List<PacketQuestion>?> DiscoverAsync(string link, CancellationToken ct = default,
        IReadOnlyList<BoardAccount>? accounts = null, Func<PacketQuestion, string?>? preScreen = null)
    {
        await using var session = await BrowserSession.OpenAsync(_options, link, ct, accounts);
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
        return questions.Count == 0 ? null : questions;
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
                (_, "checkbox") => PacketQuestion.Select,
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
          const out = [];
          const seenRadio = new Map();
          const labelFor = (el) => {
            if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
            const by = el.getAttribute('aria-labelledby');
            if (by) { const t = by.split(/\s+/).map(i => document.getElementById(i)?.innerText || '').join(' ').trim(); if (t) return t; }
            if (el.id) { const l = document.querySelector(`label[for="${CSS.escape(el.id)}"]`); if (l) return l.innerText; }
            const wrap = el.closest('label'); if (wrap) return wrap.innerText;
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
            return el.getAttribute('placeholder') || el.getAttribute('name') || '';
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
          for (const el of document.querySelectorAll('input, select, textarea')) {
            const type = (el.getAttribute('type') || (el.tagName === 'SELECT' ? (el.multiple ? 'select-multiple' : 'select-one') : 'text')).toLowerCase();
            if (['hidden', 'submit', 'button', 'reset', 'image'].includes(type)) continue;
            if (el.disabled || el.readOnly) continue;
            // Ashby draws its own radio and checkbox and keeps the real input out of sight.
            const drawn = (type === 'radio' || type === 'checkbox') && !!ashbyTitle(el);
            if (type !== 'file' && !drawn && !visible(el)) continue;
            // The careers site's own job search and job-alert boxes are not questions (#214).
            if (widget(el)) continue;
            const required = el.required || el.getAttribute('aria-required') === 'true' || ashbyRequired(el) || /^\s*\*|\*\s*$/.test(labelFor(el)) || starred(el);
            if (type === 'radio') {
              const name = el.getAttribute('name') || '';
              const grp = seenRadio.get(name);
              const opt = (document.querySelector(`label[for="${CSS.escape(el.id)}"]`)?.innerText || el.closest('label')?.innerText || el.value || '').trim();
              if (grp) { grp.options.push(opt); grp.required = grp.required || required; continue; }
              // Ashby's group name is "<form instance>_<field>" with the first half new on every
              // load; its title's `for` is the field's own id, the same every time. The question is
              // recorded by that, or the packet names a control the next load will not have.
              const stable = ashbyTitle(el)?.getAttribute('for') || '';
              const entry = { id: stable ? '' : (el.id || ''), name: stable || name, label: el.closest('fieldset')?.querySelector('legend')?.innerText || ashbyTitle(el)?.innerText?.trim() || name, tag: 'input', type: 'radio', required, options: [opt] };
              seenRadio.set(name, entry); out.push(entry); continue;
            }
            // A blank-valued option is the placeholder ("Select…"), not a choice.
            const options = el.tagName === 'SELECT' ? [...el.options].filter(o => o.value !== '').map(o => o.text.trim()) : [];
            out.push({ id: el.id || '', name: el.getAttribute('name') || '', label: labelFor(el).trim(), tag: el.tagName.toLowerCase(), type, required, options });
          }
          return out;
        }
        """.Replace("__WIDGET__", BrowserSession.WidgetJs);
}
