// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplyTrack.Api.Scrape;

/// <summary>What a scrape could recover from a posting page; every field optional.
/// Serialized snake_case to match the SPA contract.</summary>
public sealed record ScrapeResult(
    string? Company,
    string? Role,
    string? Location,
    string? Salary,
    string? Source,
    string? Description);

/// <summary>
/// Extracts lead fields from a job-posting page. Best case the page embeds a
/// schema.org <c>JobPosting</c> JSON-LD block (Greenhouse, Lever, Ashby, LinkedIn,
/// and hosted Workday pages all do) — that gives structured company/title/location/
/// salary directly. Otherwise fall back to OpenGraph/&lt;title&gt; heuristics, which
/// recover at least a role and usually a company. Dependency-free by design: a
/// regex pass for the handful of tags we need, not an HTML object model.
/// </summary>
public static partial class JobPostingParser
{
    public static ScrapeResult Parse(string html, Uri finalUrl)
    {
        var source = SourceFromHost(finalUrl.Host);

        foreach (Match m in JsonLdScript().Matches(html))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(m.Groups[1].Value); }
            catch (JsonException) { continue; } // malformed block — try the next one
            using (doc)
            {
                if (TryFindJobPosting(doc.RootElement, out var posting))
                    return FromJsonLd(posting, source);
            }
        }

        return FromMetaTags(html, source);
    }

    // ---- Tier 1: JSON-LD schema.org/JobPosting -----------------------------

    private static bool TryFindJobPosting(JsonElement el, out JsonElement posting)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (HasType(el, "JobPosting"))
                {
                    posting = el;
                    return true;
                }
                if (el.TryGetProperty("@graph", out var graph))
                    return TryFindJobPosting(graph, out posting);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    if (TryFindJobPosting(item, out posting))
                        return true;
                break;
        }
        posting = default;
        return false;
    }

    private static bool HasType(JsonElement obj, string type)
    {
        if (!obj.TryGetProperty("@type", out var t))
            return false;
        return t.ValueKind switch
        {
            JsonValueKind.String => string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Array => t.EnumerateArray().Any(x =>
                x.ValueKind == JsonValueKind.String
                && string.Equals(x.GetString(), type, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private static ScrapeResult FromJsonLd(JsonElement posting, string? source) => new(
        Company: Str(posting, "hiringOrganization") ?? Str(Obj(posting, "hiringOrganization"), "name"),
        Role: Str(posting, "title"),
        Location: LocationFrom(posting),
        Salary: SalaryFrom(posting),
        Source: source,
        Description: Clean(HtmlToText(Str(posting, "description"))));

    private static string? LocationFrom(JsonElement posting)
    {
        var parts = new List<string>();
        if (string.Equals(Str(posting, "jobLocationType"), "TELECOMMUTE", StringComparison.OrdinalIgnoreCase))
            parts.Add("Remote");

        var loc = posting.TryGetProperty("jobLocation", out var jl) ? jl : default;
        if (loc.ValueKind == JsonValueKind.Array)
            loc = loc.EnumerateArray().FirstOrDefault();
        if (loc.ValueKind == JsonValueKind.Object)
        {
            var address = Obj(loc, "address");
            var place = address?.ValueKind == JsonValueKind.Object
                ? string.Join(", ", new[]
                    {
                        Str(address.Value, "addressLocality"),
                        Str(address.Value, "addressRegion"),
                        Str(address.Value, "addressCountry") ?? Str(Obj(address.Value, "addressCountry"), "name"),
                    }.Where(s => !string.IsNullOrWhiteSpace(s)))
                : Str(loc, "address") ?? Str(loc, "name");
            if (!string.IsNullOrWhiteSpace(place))
                parts.Add(place!);
        }

        // Remote roles often carry the eligible region here instead of an address.
        if (parts.Count <= 1)
        {
            var region = Str(Obj(posting, "applicantLocationRequirements"), "name");
            if (!string.IsNullOrWhiteSpace(region))
                parts.Add(region!);
        }

        return parts.Count > 0 ? string.Join(" · ", parts.Distinct()) : null;
    }

    private static string? SalaryFrom(JsonElement posting)
    {
        var baseSalary = Obj(posting, "baseSalary");
        if (baseSalary is not { ValueKind: JsonValueKind.Object })
            return Str(posting, "baseSalary");

        var currency = Str(baseSalary.Value, "currency") ?? "";
        var value = Obj(baseSalary.Value, "value");
        string? amount = null, unit = null;
        if (value is { ValueKind: JsonValueKind.Object })
        {
            var min = Num(value.Value, "minValue");
            var max = Num(value.Value, "maxValue");
            var exact = Num(value.Value, "value");
            unit = Str(value.Value, "unitText");
            amount = (min, max) switch
            {
                (not null, not null) => $"{min:#,0}–{max:#,0}",
                _ => exact?.ToString("#,0") ?? min?.ToString("#,0") ?? max?.ToString("#,0"),
            };
        }
        else
        {
            amount = Str(baseSalary.Value, "value");
        }

        if (amount is null)
            return null;
        var text = $"{currency} {amount}".Trim();
        return unit is null ? text : $"{text}/{unit.ToLowerInvariant()}";
    }

    // ---- Tier 2: OpenGraph / <title> heuristics ----------------------------

    private static ScrapeResult FromMetaTags(string html, string? source)
    {
        var ogTitle = MetaContent(html, "og:title");
        var titleTag = TitleTag(html);
        var siteName = MetaContent(html, "og:site_name");
        var description = Clean(HtmlToText(
            MetaContent(html, "og:description") ?? MetaContent(html, "description")));

        // Run the title patterns over og:title first, then <title> — the new
        // job-boards.greenhouse.io pages put the bare role in og:title and hide
        // "Job Application for ROLE at COMPANY" in <title> only.
        string? company = null, role = null, location = null;
        foreach (var title in new[] { ogTitle, titleTag })
        {
            if (title is null)
                continue;
            // Greenhouse: "Job Application for ROLE at COMPANY"
            // LinkedIn:   "COMPANY hiring ROLE in LOCATION | LinkedIn"
            // Generic:    "ROLE at COMPANY"
            if (GreenhouseTitle().Match(title) is { Success: true } g)
                (role, company) = (role ?? g.Groups[1].Value, company ?? g.Groups[2].Value);
            else if (LinkedInTitle().Match(title) is { Success: true } li)
                (company, role, location) =
                    (company ?? li.Groups[1].Value, role ?? li.Groups[2].Value, location ?? li.Groups[3].Value);
            else if (RoleAtCompanyTitle().Match(title) is { Success: true } at)
                (role, company) = (role ?? at.Groups[1].Value, company ?? at.Groups[2].Value);
            if (company is not null)
                break;
        }
        // Last resort: if no pattern claimed the role, take the raw title text
        // (og:title, else the <title> element) rather than returning nothing.
        role ??= ogTitle ?? titleTag;

        // Greenhouse (and friends) put the *location* in og:description. If nothing
        // else claimed the location slot and the "description" reads like a place,
        // not prose, file it where the user actually wants it.
        if (location is null && description is not null && LooksLikeLocation(description))
            (location, description) = (description, null);

        return new(
            Company: Clean(company) ?? Clean(siteName),
            Role: Clean(role),
            Location: Clean(location),
            Salary: null,
            Source: source,
            Description: description);
    }

    private static string? MetaContent(string html, string name)
    {
        // Attribute order varies by generator: property-then-content and the reverse.
        // The content value is delimited by a captured quote and closed by the SAME
        // quote (\1) — a plain ["'] on both ends would terminate a double-quoted value
        // at the first apostrophe inside it (e.g. content="Bob's Burgers" → "Bob").
        var forward = Regex.Match(html,
            $"""<meta[^>]+(?:property|name)\s*=\s*["']{Regex.Escape(name)}["'][^>]*?content\s*=\s*(["'])(.*?)\1""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (forward.Success)
            return WebUtility.HtmlDecode(forward.Groups[2].Value);
        var reverse = Regex.Match(html,
            $"""<meta[^>]+content\s*=\s*(["'])(.*?)\1[^>]*?(?:property|name)\s*=\s*["']{Regex.Escape(name)}["']""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return reverse.Success ? WebUtility.HtmlDecode(reverse.Groups[2].Value) : null;
    }

    private static string? TitleTag(string html)
    {
        var m = TitleElement().Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    // Short, no sentence punctuation, and shaped like "Remote, India" / "Lincoln, NE".
    // Prose descriptions have periods and run long; a bare "remote" mention alone is
    // NOT enough — "Help us build the future of remote work" is a tagline, not a place.
    private static bool LooksLikeLocation(string s)
    {
        if (s.Length > 60 || s.Contains('.') || s.Contains('\n'))
            return false;
        // Comma-separated place tokens: "Lincoln, NE", "Remote, India".
        if (LocationShape().IsMatch(s))
            return true;
        // A short, remote-dominated marker ("Remote", "100% Remote", "Remote (US)"),
        // not a longer sentence that merely contains the word "remote".
        return s.Length <= 25 && s.Contains("remote", StringComparison.OrdinalIgnoreCase);
    }

    // ---- Shared helpers -----------------------------------------------------

    /// <summary>The SPA's <c>source</c> convention: a short board id (the poller uses
    /// <c>auto:remotive</c>-style ids; manual scrapes use the bare board name).</summary>
    public static string? SourceFromHost(string host)
    {
        host = host.ToLowerInvariant();
        foreach (var (needle, id) in KnownBoards)
            if (host.Contains(needle))
                return id;
        // "careers.acme.com" / "acme.com" → "acme". Skip a two-part public suffix
        // ("co.uk", "com.au", "co.jp") so we land on the organization label, not the
        // suffix component — "careers.acme.co.uk" → "acme", not "co".
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0];
        if (parts.Length < 2)
            return null;
        var orgIndex = parts.Length - 2;
        if (parts.Length >= 3 && parts[^1].Length == 2 && SecondLevelSuffixes.Contains(parts[^2]))
            orgIndex = parts.Length - 3;
        return parts[orgIndex];
    }

    // Second-level labels that sit under a two-letter ccTLD as a public suffix
    // (acme.co.uk, acme.com.au, acme.co.jp). Not the full Public Suffix List — just
    // the common ones, enough to keep the board id off the suffix.
    private static readonly HashSet<string> SecondLevelSuffixes = new(StringComparer.Ordinal)
    {
        "co", "com", "org", "net", "gov", "edu", "ac", "or", "ne", "go", "gob",
    };

    private static readonly (string Needle, string Id)[] KnownBoards =
    [
        ("greenhouse", "greenhouse"),
        ("lever.co", "lever"),
        ("ashbyhq", "ashby"),
        ("linkedin", "linkedin"),
        ("indeed", "indeed"),
        ("myworkday", "workday"),
        ("smartrecruiters", "smartrecruiters"),
        ("workable", "workable"),
        ("bamboohr", "bamboohr"),
        ("wellfound", "wellfound"),
        ("weworkremotely", "weworkremotely"),
        ("remotive", "remotive"),
        ("ziprecruiter", "ziprecruiter"),
        ("dice.com", "dice"),
        ("builtin", "builtin"),
        ("ycombinator", "hn"),
    ];

    private static string? HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        var text = BlockBreak().Replace(html, "\n");
        text = AnyTag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = SpaceRuns().Replace(text, " ");
        return BlankLines().Replace(text, "\n\n");
    }

    private const int MaxDescription = 2000;

    private static string? Clean(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;
        return s.Length <= MaxDescription ? s : s[..MaxDescription].TrimEnd() + "…";
    }

    private static string? Str(JsonElement? obj, string key) =>
        obj is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static JsonElement? Obj(JsonElement? obj, string key) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(key, out var v)
            ? v
            : null;

    private static decimal? Num(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString(), out var d) => d,
            _ => null,
        };
    }

    [GeneratedRegex("""<script[^>]*type\s*=\s*["']application/ld\+json["'][^>]*>(.*?)</script>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JsonLdScript();

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleElement();

    [GeneratedRegex(@"^Job Application for (.+?) at (.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GreenhouseTitle();

    [GeneratedRegex(@"^(.+?) hiring (.+?) in (.+?)(?:\s*\|.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkedInTitle();

    [GeneratedRegex(@"^(.+?) at (.+?)(?:\s*[|·–—-]\s*[^|·–—-]*)?$")]
    private static partial Regex RoleAtCompanyTitle();

    [GeneratedRegex(@"^[\w .'()/&-]+(,\s*[\w .'()/&-]+){1,3}$")]
    private static partial Regex LocationShape();

    [GeneratedRegex(@"<(?:br|/p|/li|/div|/h[1-6])[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\r\f]+")]
    private static partial Regex SpaceRuns();

    [GeneratedRegex(@"\s*\n\s*(\n\s*)*")]
    private static partial Regex BlankLines();
}
