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

    /// <summary>The form's questions, or null when the page had no fillable form.</summary>
    public async Task<List<PacketQuestion>?> DiscoverAsync(string link, CancellationToken ct = default)
    {
        await using var session = await BrowserSession.OpenAsync(_options, link, ct);
        JsonElement raw;
        try
        {
            raw = await session.Page.EvaluateAsync<JsonElement>(EnumerateScript);
        }
        catch (PlaywrightException ex)
        {
            _log.LogInformation("discovery at {Link}: {Reason}", link, ex.Message.Split('\n')[0]);
            return null;
        }
        var controls = JsonSerializer.Deserialize<List<Control>>(raw.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var questions = Map(controls);
        return questions.Count == 0 ? null : questions;
    }

    /// <summary>Turn raw controls into questions. Public for tests.</summary>
    public static List<PacketQuestion> Map(IEnumerable<(string Id, string Name, string Label, string Tag, string Type, bool Required, List<string> Options)> controls) =>
        Map(controls.Select(c => new Control(c.Id, c.Name, c.Label, c.Tag, c.Type, c.Required, c.Options)).ToList());

    private static List<PacketQuestion> Map(List<Control> controls)
    {
        var questions = new List<PacketQuestion>();
        var seen = new HashSet<string>();
        foreach (var c in controls)
        {
            var key = c.Name.Length > 0 ? c.Name : c.Id;
            if (key.Length == 0 || !seen.Add(key)) continue;
            var label = c.Label.Trim().TrimEnd('*').Trim();
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
    private const string EnumerateScript = """
        () => {
          const out = [];
          const seenRadio = new Map();
          const labelFor = (el) => {
            if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
            const by = el.getAttribute('aria-labelledby');
            if (by) { const t = by.split(/\s+/).map(i => document.getElementById(i)?.innerText || '').join(' ').trim(); if (t) return t; }
            if (el.id) { const l = document.querySelector(`label[for="${CSS.escape(el.id)}"]`); if (l) return l.innerText; }
            const wrap = el.closest('label'); if (wrap) return wrap.innerText;
            const legend = el.closest('fieldset')?.querySelector('legend'); if (legend) return legend.innerText;
            return el.getAttribute('placeholder') || el.getAttribute('name') || '';
          };
          const visible = (el) => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          for (const el of document.querySelectorAll('input, select, textarea')) {
            const type = (el.getAttribute('type') || (el.tagName === 'SELECT' ? (el.multiple ? 'select-multiple' : 'select-one') : 'text')).toLowerCase();
            if (['hidden', 'submit', 'button', 'reset', 'image'].includes(type)) continue;
            if (el.disabled || el.readOnly) continue;
            if (type !== 'file' && !visible(el)) continue;
            const required = el.required || el.getAttribute('aria-required') === 'true' || /\*\s*$/.test(labelFor(el));
            if (type === 'radio') {
              const name = el.getAttribute('name') || '';
              const grp = seenRadio.get(name);
              const opt = (document.querySelector(`label[for="${CSS.escape(el.id)}"]`)?.innerText || el.closest('label')?.innerText || el.value || '').trim();
              if (grp) { grp.options.push(opt); grp.required = grp.required || required; continue; }
              const entry = { id: el.id || '', name, label: el.closest('fieldset')?.querySelector('legend')?.innerText || name, tag: 'input', type: 'radio', required, options: [opt] };
              seenRadio.set(name, entry); out.push(entry); continue;
            }
            // A blank-valued option is the placeholder ("Select…"), not a choice.
            const options = el.tagName === 'SELECT' ? [...el.options].filter(o => o.value !== '').map(o => o.text.trim()) : [];
            out.push({ id: el.id || '', name: el.getAttribute('name') || '', label: labelFor(el).trim(), tag: el.tagName.toLowerCase(), type, required, options });
          }
          return out;
        }
        """;
}
