// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>One source that failed on the tenant's latest pass: its key (<c>remotive</c>,
/// <c>greenhouse:acme</c>, <c>rss:https://…</c>, <c>linkedin</c>, or <c>poll</c> for the
/// pass itself) and a one-line reason.</summary>
public sealed record PollSourceError(string Source, string Error);

/// <summary>The <c>GET /api/poll/status</c> body: when the poller last finished a pass for
/// this tenant (null before the first), what that pass staged, and the sources failing now.</summary>
public sealed record PollStatus(DateTime? LastPolledAt, int LastLeadsAdded, IReadOnlyList<PollSourceError> Failing);

/// <summary>
/// Tenant-scoped read over <c>poll_runs</c> (#359), the row the Python poller writes after
/// each pass. Only read here: the poller inserts, the retention sweep prunes.
/// </summary>
public sealed class PollRunRepo(IDbConnection conn, long tenantId)
{
    private sealed record Run(DateTime FinishedAt, bool AtsOnly, int LeadsAdded, string Errors);

    public async Task<PollStatus> StatusAsync()
    {
        // The newest pass of each kind. The fast lane polls ATS boards only, so a newer
        // fast-lane pass answers for the boards but says nothing about the aggregators, feeds
        // or signed-in sources — those stay as the newest full pass found them.
        var runs = (await conn.QueryAsync<Run>(
            """
            (SELECT finished_at AS finishedat, ats_only AS atsonly, leads_added AS leadsadded, errors::text AS errors
             FROM poll_runs WHERE tenant_id = @t AND NOT ats_only ORDER BY finished_at DESC LIMIT 1)
            UNION ALL
            (SELECT finished_at, ats_only, leads_added, errors::text
             FROM poll_runs WHERE tenant_id = @t AND ats_only ORDER BY finished_at DESC LIMIT 1)
            """,
            new { t = tenantId })).ToList();
        if (runs.Count == 0)
            return new PollStatus(null, 0, []);
        var latest = runs.MaxBy(r => r.FinishedAt)!;
        var full = runs.FirstOrDefault(r => !r.AtsOnly);
        var ats = runs.FirstOrDefault(r => r.AtsOnly);
        return new PollStatus(latest.FinishedAt, latest.LeadsAdded, Failing(full, ats));
    }

    private static List<PollSourceError> Failing(Run? full, Run? ats)
    {
        var failing = full is null ? [] : Parse(full.Errors);
        if (ats is not null && (full is null || ats.FinishedAt > full.FinishedAt))
        {
            failing.RemoveAll(e => IsBoard(e.Source));
            var fresh = Parse(ats.Errors);
            failing.RemoveAll(e => fresh.Any(f => f.Source == e.Source));
            failing.AddRange(fresh);
        }
        return failing;
    }

    /// <summary>An ATS board's key is <c>provider:slug</c>; a feed's is <c>rss:url</c>.</summary>
    public static bool IsBoard(string source) =>
        source.Contains(':') && !source.StartsWith("rss:", StringComparison.Ordinal);

    private static List<PollSourceError> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PollSourceError>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
