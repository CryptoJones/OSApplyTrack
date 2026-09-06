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

        return reasons;
    }
}
