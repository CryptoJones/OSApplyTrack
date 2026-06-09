// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text;
using System.Text.Json;

namespace ApplyTrack.Api.Data;

/// <summary>One role in the candidate's history (a repeating résumé section).</summary>
public sealed record ResumeExperience(string Company, string Title, string Dates, List<string> Highlights);

/// <summary>A labelled external link (portfolio, GitHub, …).</summary>
public sealed record ResumeLink(string Label, string Url);

/// <summary>
/// Per-tenant structured résumé — the factual brief the cover-letter drafter feeds
/// the LLM as "the only facts you may assert about the candidate". The multi-tenant
/// heir to materials.py's hardcoded <c>_BACKGROUND</c>: instead of one person's
/// facts baked into the code, every tenant supplies their own. Serializes to the
/// snake_case <c>/api/resume</c> shape the SPA editor round-trips.
/// </summary>
public sealed class Resume
{
    public string FullName { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Location { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<ResumeExperience> Experience { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public List<string> Certifications { get; set; } = [];
    public List<ResumeLink> Links { get; set; } = [];

    public static Resume Empty() => new();

    /// <summary>True when nothing meaningful is filled in — drafting needs a non-empty résumé.</summary>
    public bool IsEmpty =>
        FullName.Length == 0 && Headline.Length == 0 && Summary.Length == 0
        && Experience.Count == 0 && Skills.Count == 0;

    /// <summary>Build a normalized Resume from loose JSON, ignoring junk keys (mirrors Criteria.FromJson).</summary>
    public static Resume FromJson(JsonElement data)
    {
        return new Resume
        {
            FullName = GetString(data, "full_name"),
            Headline = GetString(data, "headline"),
            Location = GetString(data, "location"),
            Summary = GetString(data, "summary"),
            Experience = CleanExperience(data),
            Skills = CleanList(data, "skills"),
            Certifications = CleanList(data, "certifications"),
            Links = CleanLinks(data),
        };
    }

    /// <summary>
    /// Render the résumé as a plain-text brief for the LLM prompt — the set of facts
    /// the model is told are the only things it may assert about the candidate.
    /// </summary>
    public string ToBrief()
    {
        var sb = new StringBuilder();
        if (FullName.Length > 0) sb.AppendLine(Headline.Length > 0 ? $"{FullName} — {Headline}" : FullName);
        else if (Headline.Length > 0) sb.AppendLine(Headline);
        if (Location.Length > 0) sb.AppendLine($"Location: {Location}");
        if (Summary.Length > 0) sb.AppendLine().AppendLine(Summary);
        if (Experience.Count > 0)
        {
            sb.AppendLine().AppendLine("Experience:");
            foreach (var e in Experience)
            {
                var head = string.Join(" · ",
                    new[] { e.Title, e.Company, e.Dates }.Where(s => s.Length > 0));
                sb.AppendLine($"- {head}");
                foreach (var h in e.Highlights)
                    sb.AppendLine($"  - {h}");
            }
        }
        if (Skills.Count > 0)
            sb.AppendLine().AppendLine($"Skills: {string.Join(", ", Skills)}");
        if (Certifications.Count > 0)
            sb.AppendLine().AppendLine($"Certifications: {string.Join(", ", Certifications)}");
        if (Links.Count > 0)
        {
            sb.AppendLine().AppendLine("Links:");
            foreach (var l in Links)
                sb.AppendLine(l.Label.Length > 0 ? $"- {l.Label}: {l.Url}" : $"- {l.Url}");
        }
        return sb.ToString().Trim();
    }

    private static string GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()).Trim()
            : "";

    // De-duped (case-insensitive), stripped, order-preserving string list.
    private static List<string> CleanList(JsonElement obj, string key)
    {
        var outList = new List<string>();
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return outList;
        var seen = new HashSet<string>();
        foreach (var el in arr.EnumerateArray())
        {
            var s = (el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString())?.Trim() ?? "";
            if (s.Length > 0 && seen.Add(s.ToLowerInvariant()))
                outList.Add(s);
        }
        return outList;
    }

    private static List<ResumeExperience> CleanExperience(JsonElement obj)
    {
        var outList = new List<ResumeExperience>();
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty("experience", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return outList;
        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var company = GetString(entry, "company");
            var title = GetString(entry, "title");
            var dates = GetString(entry, "dates");
            var highlights = CleanStrings(entry, "highlights");
            // Drop an entry that carries no information at all.
            if (company.Length == 0 && title.Length == 0 && dates.Length == 0 && highlights.Count == 0)
                continue;
            outList.Add(new ResumeExperience(company, title, dates, highlights));
        }
        return outList;
    }

    // Like CleanList but order-preserving with no dedup (highlights may legitimately repeat shapes).
    private static List<string> CleanStrings(JsonElement obj, string key)
    {
        var outList = new List<string>();
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return outList;
        foreach (var el in arr.EnumerateArray())
        {
            var s = (el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString())?.Trim() ?? "";
            if (s.Length > 0) outList.Add(s);
        }
        return outList;
    }

    private static List<ResumeLink> CleanLinks(JsonElement obj)
    {
        var outList = new List<ResumeLink>();
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty("links", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return outList;
        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var url = GetString(entry, "url");
            if (url.Length == 0) continue; // a link without a URL is noise
            outList.Add(new ResumeLink(GetString(entry, "label"), url));
        }
        return outList;
    }
}
