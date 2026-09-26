// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// One entry of an application's timeline. <c>kind</c> is <c>status</c> for every row
/// today; it is on the wire so later sources (reminders, a notes log) can merge into the
/// same list without a shape change. <c>from_status</c> is null on the creating row.
/// </summary>
public sealed record TimelineEntry(string Kind, string? FromStatus, string ToStatus, DateTime At);

/// <summary>One status event as the account export carries it, keyed by the app's slug.</summary>
public sealed record StatusEventExport(string Application, string? FromStatus, string ToStatus, DateTime At);

/// <summary>One row of an analytics breakdown (a source, or a lane).</summary>
public sealed record FunnelGroup(
    string Key, int Applied, int Responded, double? ResponseRate, double? MedianDaysToResponse);

/// <summary>A stage of the funnel and how many applications reached it.</summary>
public sealed record FunnelStage(string Stage, int Count);

/// <summary>The <c>GET /api/analytics</c> body.</summary>
public sealed record Analytics(
    DateTime? Since, int Applied, int Responded, int Rejected, double? ResponseRate,
    double? MedianDaysToResponse, IReadOnlyList<FunnelStage> Funnel,
    IReadOnlyList<FunnelGroup> BySource, IReadOnlyList<FunnelGroup> ByLane);

/// <summary>
/// Tenant-scoped reads over <c>status_events</c> (#353) — the status history the
/// applications triggers write (0049). Nothing here records a change: every writer of
/// <c>applications</c> is covered by the trigger. The one write is the import's
/// history restore, which replaces the trigger's "entered just now" rows with the
/// dated history the export carried.
/// </summary>
public sealed class StatusEventRepo
{
    static StatusEventRepo() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    // Past applying: an application that reached any of these was applied for, even if
    // the history never shows `applied` itself (created straight into `screen`, say).
    private static readonly string[] Reached = ["applied", "screen", "onsite", "offer", "rejected"];
    // The employer answered.
    private static readonly string[] Responses = ["screen", "onsite", "offer", "rejected"];
    // The funnel, in order; reaching a stage implies every stage before it.
    private static readonly string[] Stages = ["applied", "screen", "onsite", "offer"];

    private readonly IDbConnection _conn;
    private readonly long _t;

    public StatusEventRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    /// <summary>The application's timeline, oldest first; null when there is no such app.</summary>
    public async Task<IReadOnlyList<TimelineEntry>?> HistoryAsync(string name)
    {
        var n = Slug.Normalize(name);
        var id = await _conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT id FROM applications WHERE tenant_id = @t AND name = @n", new { t = _t, n });
        if (id is null)
            return null;
        return (await _conn.QueryAsync<TimelineEntry>(
            """
            SELECT 'status' AS kind, from_status AS fromstatus, to_status AS tostatus, at
            FROM status_events WHERE tenant_id = @t AND application_id = @id
            ORDER BY at, id
            """,
            new { t = _t, id })).ToList();
    }

    /// <summary>Every event, for the account export: by slug, then oldest first.</summary>
    public async Task<IReadOnlyList<StatusEventExport>> ExportAllAsync() =>
        (await _conn.QueryAsync<StatusEventExport>(
            """
            SELECT a.name AS application, e.from_status AS fromstatus, e.to_status AS tostatus, e.at
            FROM status_events e JOIN applications a ON a.id = e.application_id AND a.tenant_id = e.tenant_id
            WHERE e.tenant_id = @t
            ORDER BY a.name, e.at, e.id
            """,
            new { t = _t })).ToList();

    /// <summary>
    /// Replace the history of every application in <paramref name="applications"/> with the
    /// file's events for it — the import's restore, run in its transaction after the upsert.
    /// An app the file lists with no events ends up with none, as the snapshot says; an
    /// event for an app outside the set is dropped. Statuses are clamped like any other
    /// write. Returns how many events were written.
    /// </summary>
    public async Task<int> ReplaceAsync(
        IEnumerable<string> applications, IEnumerable<StatusEventExport> events, IDbTransaction? tx = null)
    {
        var apps = applications.Select(Slug.Normalize).Distinct(StringComparer.Ordinal).ToArray();
        if (apps.Length == 0)
            return 0;
        var wanted = apps.ToHashSet(StringComparer.Ordinal);
        await _conn.ExecuteAsync(
            """
            DELETE FROM status_events e USING applications a
            WHERE e.tenant_id = @t AND a.tenant_id = @t AND a.id = e.application_id AND a.name = ANY(@apps)
            """,
            new { t = _t, apps }, tx);
        var rows = events
            .Where(e => AppFields.Statuses.Contains(e.ToStatus) && wanted.Contains(Slug.Normalize(e.Application)))
            .Select(e => (
                App: Slug.Normalize(e.Application),
                From: e.FromStatus is { } f && AppFields.Statuses.Contains(f) ? f : null,
                e.ToStatus,
                At: DateTime.SpecifyKind(e.At, e.At.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : e.At.Kind).ToUniversalTime()))
            .ToList();
        if (rows.Count == 0)
            return 0;
        return await _conn.ExecuteAsync(
            """
            INSERT INTO status_events (tenant_id, application_id, from_status, to_status, at)
            SELECT @t, a.id, r.from_status, r.to_status, r.at
            FROM unnest(@names::text[], @froms::text[], @tos::text[], @ats::timestamptz[])
                 AS r(name, from_status, to_status, at)
            JOIN applications a ON a.tenant_id = @t AND a.name = r.name
            """,
            new
            {
                t = _t,
                names = rows.Select(r => r.App).ToArray(),
                froms = rows.Select(r => r.From).ToArray(),
                tos = rows.Select(r => r.ToStatus).ToArray(),
                ats = rows.Select(r => r.At).ToArray(),
            },
            tx);
    }

    private sealed record Row
    {
        public string Lane { get; init; } = "";
        public string Source { get; init; } = "";
        public int Stage { get; init; }
        public bool Rejected { get; init; }
        public DateTime AppliedAt { get; init; }
        public DateTime? RespondedAt { get; init; }
    }

    /// <summary>
    /// The funnel over applications first applied for on or after <paramref name="since"/>
    /// (all time when null): how many reached each stage, how many heard back (a screen or
    /// later, or a rejection), and the median days from applying to the first answer —
    /// timed only where the history shows the application applied before it was answered.
    /// The two populations differ on purpose: <c>responded</c> counts every application that
    /// ever reached a screen or later or a rejection, whatever the order of its events (one
    /// created straight into <c>screen</c> heard back), while the median covers only those
    /// with an answer strictly after their first applied event, since only they can be timed.
    /// </summary>
    public async Task<Analytics> AnalyticsAsync(DateTime? since)
    {
        var rows = (await _conn.QueryAsync<Row>(
            """
            WITH per AS (
                SELECT a.id, a.lane, a.source,
                       min(e.at) FILTER (WHERE e.to_status = ANY(@reached)) AS applied_at,
                       max(array_position(@stages, e.to_status)) AS stage,
                       bool_or(e.to_status = 'rejected') AS rejected
                FROM applications a
                JOIN status_events e ON e.tenant_id = a.tenant_id AND e.application_id = a.id
                WHERE a.tenant_id = @t
                GROUP BY a.id, a.lane, a.source
            )
            SELECT per.lane, per.source, coalesce(per.stage, 0) AS stage, per.rejected, per.applied_at,
                   (SELECT min(r.at) FROM status_events r
                    WHERE r.tenant_id = @t AND r.application_id = per.id
                      AND r.to_status = ANY(@responses) AND r.at > per.applied_at) AS responded_at
            FROM per
            WHERE per.applied_at IS NOT NULL AND (@since::timestamptz IS NULL OR per.applied_at >= @since)
            """,
            new { t = _t, reached = Reached, responses = Responses, stages = Stages, since })).ToList();

        static bool Answered(Row r) => r.Stage >= 2 || r.Rejected;

        static FunnelGroup Group(string key, IReadOnlyCollection<Row> g)
        {
            var responded = g.Count(Answered);
            return new FunnelGroup(key, g.Count, responded, Rate(responded, g.Count), Median(g));
        }

        var answered = rows.Count(Answered);
        return new Analytics(
            Since: since,
            Applied: rows.Count,
            Responded: answered,
            Rejected: rows.Count(r => r.Rejected),
            ResponseRate: Rate(answered, rows.Count),
            MedianDaysToResponse: Median(rows),
            // Stage 1 is applied; every row counted here reached at least that.
            Funnel: Stages.Select((s, i) => new FunnelStage(s, i == 0 ? rows.Count : rows.Count(r => r.Stage >= i + 1))).ToList(),
            BySource: rows.GroupBy(r => r.Source.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => Group(g.Key, g.ToList()))
                .OrderByDescending(g => g.Applied).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList(),
            ByLane: rows.GroupBy(r => r.Lane, StringComparer.Ordinal)
                .Select(g => Group(g.Key, g.ToList()))
                .OrderByDescending(g => g.Applied).ThenBy(g => g.Key, StringComparer.Ordinal).ToList());
    }

    private static double? Rate(int part, int whole) =>
        whole == 0 ? null : Math.Round((double)part / whole, 4);

    private static double? Median(IEnumerable<Row> rows)
    {
        var days = rows.Where(r => r.RespondedAt is not null)
            .Select(r => (r.RespondedAt!.Value - r.AppliedAt).TotalDays)
            .Order().ToList();
        if (days.Count == 0)
            return null;
        var mid = days.Count / 2;
        var m = days.Count % 2 == 1 ? days[mid] : (days[mid - 1] + days[mid]) / 2;
        return Math.Round(m, 1);
    }
}
