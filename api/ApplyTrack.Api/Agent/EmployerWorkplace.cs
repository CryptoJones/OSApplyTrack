// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplyTrack.Api.Agent;

/// <summary>
/// Where the employer's ATS says the job is done, from its own structured field rather than
/// the description (#334): Ashby's <c>workplaceType</c>/<c>isRemote</c>, Lever's
/// <c>workplaceType</c>, Greenhouse's <c>location.name</c>. Bankjoy's Ashby posting is marked
/// Remote there and never says the word in its text, so silence in a description means
/// something only next to one of these. <paramref name="Says"/> is the field as a reason line
/// quotes it.
/// </summary>
public sealed partial record EmployerWorkplace(bool Remote, string Says)
{
    [GeneratedRegex(@"\b(?:remote|anywhere|distributed|work from home|wfh|telecommut)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteWord();

    // A city and its state or province, "Dallas, TX" or "Toronto, ON, Canada" — never a list of
    // countries ("United States, Canada") or a country code standing in for one ("Americas, US").
    [GeneratedRegex(@"\b[A-Z][A-Za-z.' -]*,\s*(?!(?:US|UK|EU)\b)[A-Z]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CityAndRegion();

    [GeneratedRegex(@"^/([^/?#]+)/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrgAndPosting();

    /// <summary>
    /// The public API document that carries the posting's workplace, or null when the link is
    /// not one of the ATSs read here. Ashby answers per board, not per job, so the job is found
    /// in the list by <paramref name="jobId"/>.
    /// </summary>
    public static string? ApiUrl(string link, string source, out string provider, out string jobId)
    {
        provider = AtsProvider.Detect(link, source);
        jobId = "";
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return null;
        var host = uri.Host.ToLowerInvariant();
        switch (provider)
        {
            case AtsProvider.Ashby when host == "jobs.ashbyhq.com" && OrgAndPosting().Match(uri.AbsolutePath) is { Success: true } a:
                jobId = a.Groups[2].Value;
                return $"https://api.ashbyhq.com/posting-api/job-board/{a.Groups[1].Value}";
            case AtsProvider.Lever when host is "jobs.lever.co" or "jobs.eu.lever.co" && OrgAndPosting().Match(uri.AbsolutePath) is { Success: true } l:
                jobId = l.Groups[2].Value;
                return $"https://api.{(host == "jobs.eu.lever.co" ? "eu." : "")}lever.co/v0/postings/{l.Groups[1].Value}/{jobId}";
            case AtsProvider.Greenhouse when AtsProvider.TryParseGreenhouse(link, source, out var board, out var id):
                jobId = id;
                var eu = host.EndsWith(".eu.greenhouse.io", StringComparison.Ordinal) ? "eu." : "";
                return $"https://boards-api.{eu}greenhouse.io/v1/boards/{Uri.EscapeDataString(board)}/jobs/{id}";
            default:
                return null;
        }
    }

    /// <summary>
    /// Read the workplace out of the API's answer; null when the field is missing or says
    /// nothing either way (Lever's "unspecified", Greenhouse's bare "United States"). Only
    /// a plain statement counts: a Greenhouse location is held to be a place of work only
    /// when it names a city and its state or province ("Dallas, TX"), and any remote word
    /// anywhere in it wins.
    /// </summary>
    public static EmployerWorkplace? Parse(string provider, string json, string jobId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (provider)
            {
                case AtsProvider.Ashby:
                    if (!root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
                        return null;
                    foreach (var job in jobs.EnumerateArray())
                    {
                        if (!string.Equals(Str(job, "id"), jobId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var type = Str(job, "workplaceType");
                        if (job.TryGetProperty("isRemote", out var r) && r.ValueKind == JsonValueKind.True
                            || string.Equals(type, "Remote", StringComparison.OrdinalIgnoreCase))
                            return new(true, $"Ashby workplaceType \"{type ?? "Remote"}\"");
                        return string.Equals(type, "OnSite", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(type, "Hybrid", StringComparison.OrdinalIgnoreCase)
                            ? new(false, $"Ashby workplaceType \"{type}\"") : null;
                    }
                    return null;
                case AtsProvider.Lever:
                    return Str(root, "workplaceType")?.ToLowerInvariant() switch
                    {
                        "remote" => new(true, "Lever workplaceType \"remote\""),
                        var t when t is "hybrid" or "onsite" => new(false, $"Lever workplaceType \"{t}\""),
                        _ => null,
                    };
                case AtsProvider.Greenhouse:
                    var name = root.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
                        ? Str(loc, "name")?.Trim() : null;
                    if (string.IsNullOrEmpty(name))
                        return null;
                    if (RemoteWord().IsMatch(name))
                        return new(true, $"Greenhouse location \"{name}\"");
                    return CityAndRegion().IsMatch(name) ? new(false, $"Greenhouse location \"{name}\"") : null;
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
