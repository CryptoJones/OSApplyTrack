// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Agent;

/// <summary>
/// Deterministic reasons a posting is out of reach for a tenant — a clearance the
/// candidate cannot get, a sponsorship the employer will not give, a location the
/// tenant has excluded. Checked in code <em>before</em> the model is consulted, so
/// a keyword-inflated score cannot buy a doomed application any tokens, and a
/// hallucinating model can never un-veto them. Conservative on purpose: every
/// pattern here is one an employer states plainly; ambiguity falls through to the
/// model's judgement.
/// </summary>
public static partial class Disqualifiers
{
    [GeneratedRegex(
        @"\b(?:active|current|existing|must (?:hold|have|possess)(?: an?)?)\s+(?:(?:ts|top[ -]secret|secret|dod|public trust)\s+)?(?:security\s+)?clearance\b"
        + @"|\b(?:ts/sci|top[ -]secret|secret clearance|security clearance (?:is )?required|clearance required|clearance(?:d)? (?:is )?required)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Clearance();

    [GeneratedRegex(
        @"\b(?:no|not|unable to|cannot|can't|will not|won't|do(?:es)? not|not able to)\s+(?:provide|offer|able to provide)?\s*(?:visa\s+)?sponsor(?:ship)?\b"
        + @"|\bwithout (?:visa )?sponsorship\b|\bsponsorship (?:is )?not (?:available|offered|provided)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoSponsorship();

    [GeneratedRegex(@"\b(?:on-?site|in-?office|in person)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnSite();

    [GeneratedRegex(@"\b(?:remote|work from home|wfh|distributed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Remote();

    // What an employer writes when the role is not remote: "a flexible/hybrid model of 3 days
    // onsite" (DTCC), "Hybrid — three days at our office", "hybrid role", "4 days a week in
    // the office", "this is an on-site position". Never "hybrid cloud" or "a hybrid model for
    // fraud detection": hybrid counts before a word about working, or before a count of days.
    [GeneratedRegex(
        @"\bhybrid\s+(?:role|position|schedule|work(?:ing|place)?|arrangement|work\s+model)\b"
        + @"|\bhybrid\b[^.\n]{0,40}?\b(?:\d|one|two|three|four|five)\s+days?\b"
        + @"|\b(?:\d|one|two|three|four|five)\s+days?\s+(?:a\s+week\s+|per\s+week\s+)?(?:on-?site|in(?:\s+the)?\s+office|in-office|in\s+person)\b"
        + @"|\b(?:this|the)\s+(?:is\s+an?\s+|role\s+is\s+(?:an?\s+)?|position\s+is\s+(?:an?\s+)?)(?:on-?site|in-office|in\s+office|hybrid)\b"
        + @"|\b(?:on-?site|in-office)\s+(?:role|position|only)\b|\bnot\s+(?:a\s+)?remote\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotRemote();

    // Remote said of the job — "fully remote", "remote role", "the position is remote",
    // "Location: Remote", "(Remote)" — never "remote access tools" or "distributed systems".
    [GeneratedRegex(
        @"\b(?:fully|100%|completely|entirely)\s+remote\b"
        + @"|\bremote(?:ly)?[\s-]+(?:role|position|job|work(?:er|ing)?|first|friendly|opportunity|employee|option|eligible|based|within|from|across|anywhere|in\s+the\b)"
        + @"|\b(?:is|be|are|work|working|role|position|job)\s+(?:\w+\s+)?remote(?:ly)?\b"
        + @"|\blocation\s*:\s*remote\b|\(remote\)"
        + @"|\bwork[- ]from[- ](?:home|anywhere)\b|\bwfh\b|\btelecommut",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SaysRemote();

    /// <summary>
    /// Does the employer's own posting contradict "remote" for a remote-only profile? A job
    /// board's listing can say "(Remote)" when the employer's page says otherwise: LinkedIn
    /// listed DTCC as "Tampa, FL (Remote)" and DTCC's page says "a flexible/hybrid model of
    /// 3 days onsite"; it listed Wingstop as "Dallas, TX (Remote)" over a posting that gives a
    /// street address and never once says remote. <paramref name="employerText"/> must be the
    /// employer's page, never the board's. The reason, or null. A posting too short to judge
    /// (a page that would not render) is never held against the role, nor is silence in
    /// text that may have been cut short (<paramref name="complete"/> false).
    /// </summary>
    public static string? EmployerContradictsRemote(string employerText, Criteria criteria, bool complete = true)
    {
        if (!criteria.RemoteOnly || string.IsNullOrWhiteSpace(employerText)) return null;
        var m = NotRemote().Match(employerText);
        if (m.Success)
            return $"the employer's own posting says \"{Around(employerText, m)}\" — the profile is remote-only";
        if (complete && employerText.Length >= 1500 && !SaysRemote().IsMatch(employerText))
            return "the employer's own posting never says the role is remote — the profile is remote-only";
        return null;
    }

    // The sentence around a match, trimmed for a reason line.
    private static string Around(string text, Match m)
    {
        var start = Math.Max(text.LastIndexOfAny(['.', '\n', '!', '?'], m.Index) + 1, m.Index - 100);
        var stops = text.IndexOfAny(['.', '\n', '!', '?'], m.Index + m.Length);
        var end = Math.Min(stops < 0 ? text.Length : stops, m.Index + m.Length + 100);
        return System.Text.RegularExpressions.Regex.Replace(text[start..end], @"\s+", " ").Trim();
    }

    private static bool UsBased(string country) =>
        country.Trim().ToLowerInvariant() is "united states" or "united states of america" or "us" or "usa" or "u.s." or "u.s.a.";

    /// <summary>The disqualifiers that apply, in plain language; empty means none.</summary>
    public static IReadOnlyList<string> Find(
        AppFields app, string postingText, Criteria criteria, AgentSettings settings)
    {
        var text = app.Role + "\n" + app.Location + "\n" + app.Notes + "\n" + postingText;
        var reasons = new List<string>();

        if (!settings.ClearanceOk && Clearance().IsMatch(text))
            reasons.Add("requires a security clearance the candidate does not hold");

        if (settings.NeedsSponsorship && NoSponsorship().IsMatch(text))
            reasons.Add("the employer states it does not sponsor visas");

        var where = app.Location + "\n" + postingText;
        foreach (var excluded in criteria.ExcludeLocations)
        {
            var needle = excluded.Trim();
            if (needle.Length < 2) continue;
            // Only the structured location field decides an exclusion outright; a
            // posting body naming the place (an office list, a "not in X" line) is
            // too ambiguous to veto on and is left to the model.
            if (app.Location.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"located in an excluded location ({needle})");
                break;
            }
        }

        if (criteria.RemoteOnly && OnSite().IsMatch(where) && !Remote().IsMatch(where))
            reasons.Add("on-site role, but the profile is remote-only");

        // Greenhouse's EU job board (job-boards.eu.greenhouse.io) hosts employers' European
        // openings: a US candidate is out of reach there, whatever the listing says (CJ,
        // 2026-09-24: "if the URL is job-boards.eu.greenhouse.io it is not a US job").
        if (UsBased(settings.Country) && Uri.TryCreate(app.Link, UriKind.Absolute, out var link)
            && link.Host.EndsWith(".eu.greenhouse.io", StringComparison.OrdinalIgnoreCase))
            reasons.Add("posted on Greenhouse's EU job board — not a US job");

        return reasons;
    }
}
