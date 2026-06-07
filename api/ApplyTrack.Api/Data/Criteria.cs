// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;

namespace ApplyTrack.Api.Data;

/// <summary>A company's public ATS board to scan (provider + company slug).</summary>
public sealed record AtsBoard(string Provider, string Slug);

/// <summary>
/// Per-tenant discovery criteria — C# heir to the Python <c>Criteria</c>. Serializes
/// to the exact <c>/api/criteria</c> JSON the SPA round-trips. The Python poller reads
/// the same fields from <c>search_profiles</c> in Steps 3-4.
/// </summary>
public sealed class Criteria
{
    public List<string> Keywords { get; set; } = [];
    public string DefaultLane { get; set; } = "ai";
    public int MinFitScore { get; set; } = 55;
    public bool RemoteOnly { get; set; }
    public List<string> ExcludeLocations { get; set; } = [];
    public Dictionary<string, bool> Sources { get; set; } = [];
    public List<AtsBoard> AtsBoards { get; set; } = [];

    // Source ids the poller knows how to fetch without per-source configuration.
    public static readonly string[] BuiltinSources =
        ["remotive", "remoteok", "arbeitnow", "jobicy", "weworkremotely", "hn_whoishiring"];

    public static readonly string[] AtsProviders = ["greenhouse", "lever"];

    private const int ScoreFloor = 0;
    private const int ScoreCeil = 100;
    private const int ScoreDefault = 55;

    // The original per-lane keyword lists, flattened — the UI's keyword box default.
    public static readonly string[] DefaultKeywords =
    [
        ".net", "dotnet", "c#", "csharp", "asp.net", "blazor", "entity framework",
        "ef core", "f#", "backend engineer", "back-end engineer", "backend developer",
        "web api", "microservices",
        "developer advocate", "developer relations", "devrel", "developer experience",
        "technical writer", "technical writing", "documentation engineer",
        "community manager", "developer evangelist", "evangelist", "dx engineer",
        "content engineer", "developer educator",
        "ai engineer", "agentic", "llm", "large language model", "machine learning",
        "ml engineer", "applied ai", "prompt engineer", "rag", "langchain",
        "generative ai", "genai", "ai/ml", "ai agent", "mlops",
    ];

    // Only Remotive + RemoteOK are on by default, matching the original poller.
    public static Dictionary<string, bool> DefaultSources() =>
        BuiltinSources.ToDictionary(s => s, s => s is "remotive" or "remoteok");

    public static Criteria Defaults() => new()
    {
        Keywords = [.. DefaultKeywords],
        Sources = DefaultSources(),
    };

    /// <summary>Build a normalized Criteria from loose JSON, ignoring junk keys (heir to from_dict).</summary>
    public static Criteria FromJson(JsonElement data)
    {
        var lane = GetString(data, "default_lane", "ai").Trim().ToLowerInvariant();

        var score = ScoreDefault;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("min_fit_score", out var sc))
        {
            if (sc.ValueKind == JsonValueKind.Number && sc.TryGetDouble(out var d))
                score = (int)d;
            else if (sc.ValueKind == JsonValueKind.String && int.TryParse(sc.GetString(), out var parsed))
                score = parsed;
        }

        var sources = DefaultSources();
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("sources", out var src) && src.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in src.EnumerateObject())
                if (BuiltinSources.Contains(prop.Name))
                    sources[prop.Name] = ToBool(prop.Value);
        }

        var boards = new List<AtsBoard>();
        var seen = new HashSet<(string, string)>();
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("ats_boards", out var rawBoards) && rawBoards.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in rawBoards.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var provider = GetString(entry, "provider", "").Trim().ToLowerInvariant();
                var slug = GetString(entry, "slug", "").Trim();
                if (!AtsProviders.Contains(provider) || slug.Length == 0) continue;
                if (seen.Add((provider, slug.ToLowerInvariant())))
                    boards.Add(new AtsBoard(provider, slug));
            }
        }

        var keywords = CleanList(data, "keywords");
        return new Criteria
        {
            Keywords = keywords.Count > 0 ? keywords : [.. DefaultKeywords],
            DefaultLane = AppFields.Lanes.Contains(lane) ? lane : "ai",
            MinFitScore = Math.Clamp(score, ScoreFloor, ScoreCeil),
            RemoteOnly = data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("remote_only", out var ro) && ToBool(ro),
            ExcludeLocations = CleanList(data, "exclude_locations"),
            Sources = sources,
            AtsBoards = boards,
        };
    }

    private static string GetString(JsonElement obj, string key, string fallback) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString()
            : fallback;

    private static bool ToBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetDouble(out var d) && d != 0,
        JsonValueKind.String => v.GetString() is { Length: > 0 } s
            && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0",
        _ => false,
    };

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
}
