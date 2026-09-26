// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>One piece of evidence, without the screenshot bytes.</summary>
public sealed record EvidenceView(
    long Id, string Kind, string Url, string Confirmation, JsonElement Detail, bool HasScreenshot, DateTimeOffset CreatedAt);

/// <summary>Tenant-scoped read/write of <c>agent_evidence</c>: what the browser saw.</summary>
public sealed class AgentEvidenceRepo
{
    public static class Kinds
    {
        public const string DryRun = "dry_run";
        public const string Submitted = "submitted";
        public const string Failed = "failed";
        /// <summary>The board emailed the candidate a security code; the run is parked waiting for it.</summary>
        public const string AwaitingCode = "awaiting_code";
        /// <summary>Nothing was attempted: LinkedIn Easy Apply's daily cap was reached. Not a
        /// failure — it goes out when the day's window has moved.</summary>
        public const string Deferred = "deferred";
    }

    /// <summary>A LinkedIn cap stand-down — a <see cref="Kinds.Deferred"/> row, or one written as
    /// failed before that kind existed, with nothing attempted.</summary>
    public static bool IsCapStandDown(string kind, JsonElement detail) =>
        kind == Kinds.Deferred
        || (kind == Kinds.Failed && detail.ValueKind == JsonValueKind.Object
            && detail.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
            && (r.GetString() ?? "").StartsWith("LinkedIn Easy Apply:", StringComparison.Ordinal)
            && (r.GetString() ?? "").Contains("already sent in the last day", StringComparison.Ordinal));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IDbConnection _conn;
    private readonly long _t;

    private readonly Crypto.SecretProtector _protector;

    public AgentEvidenceRepo(IDbConnection conn, long tenantId, Crypto.SecretProtector protector)
    {
        _conn = conn;
        _t = tenantId;
        _protector = protector;
    }

    public Task<long> RecordAsync(
        string appName, string kind, string url, string confirmation, object detail, byte[]? screenshot,
        IDbTransaction? tx = null) =>
        _conn.ExecuteScalarAsync<long>(
            "INSERT INTO agent_evidence (tenant_id, application_name, kind, url, confirmation, detail, screenshot) "
            + "VALUES (@t, @n, @kind, @url, @confirmation, @detail::jsonb, @screenshot) RETURNING id",
            new
            {
                t = _t, n = Slug.Normalize(appName), kind, url, confirmation,
                detail = JsonSerializer.Serialize(detail, Json),
                // A screenshot of a filled application form is the form: sealed at rest.
                screenshot = screenshot is null ? null : _protector.ProtectBytes(screenshot),
            },
            tx);

    private sealed record Row(long Id, string Kind, string Url, string Confirmation, string Detail, bool HasScreenshot, DateTime CreatedAt);

    /// <summary>Newest first, no bytes.</summary>
    public async Task<IReadOnlyList<EvidenceView>> ListAsync(string appName, int limit = 10)
    {
        var rows = await _conn.QueryAsync<Row>(
            "SELECT id, kind, url, confirmation, detail::text AS detail, screenshot IS NOT NULL AS hasscreenshot, "
            + "created_at AS createdat FROM agent_evidence WHERE tenant_id = @t AND application_name = @n "
            + "ORDER BY created_at DESC, id DESC LIMIT @limit",
            new { t = _t, n = Slug.Normalize(appName), limit = Math.Clamp(limit, 1, 50) });
        return rows.Select(r => new EvidenceView(r.Id, r.Kind, r.Url, r.Confirmation,
            JsonDocument.Parse(r.Detail.Length > 0 ? r.Detail : "{}").RootElement.Clone(), r.HasScreenshot,
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)))).ToList();
    }

    /// <summary>The newest piece of evidence per application still parked in <c>ready</c>,
    /// without bytes — what the promotion pass and "Submit all clean" judge a packet by.</summary>
    public async Task<IReadOnlyList<(string Name, string Kind, JsonElement Detail, DateTimeOffset CreatedAt)>> LatestPerReadyApplicationAsync()
    {
        var rows = await _conn.QueryAsync<(string Name, string Kind, string Detail, DateTimeOffset CreatedAt)>(
            """
            SELECT DISTINCT ON (e.application_name) e.application_name, e.kind, e.detail::text, e.created_at
            FROM agent_evidence e
            JOIN applications a ON a.tenant_id = e.tenant_id AND a.name = e.application_name
            WHERE e.tenant_id = @t AND a.status = 'ready'
            ORDER BY e.application_name, e.created_at DESC, e.id DESC
            """,
            new { t = _t });
        return rows.Select(r => (r.Name, r.Kind,
            JsonDocument.Parse(r.Detail.Length > 0 ? r.Detail : "{}").RootElement.Clone(), r.CreatedAt)).ToList();
    }

    /// <summary>One application in the Errors view: parked in Ready with a failed run as its newest evidence.</summary>
    public sealed record Errored(
        string ApplicationName, string Company, string Role, string Link, string Source,
        JsonElement Detail, DateTimeOffset At, int Runs, int RecentFailures);

    private sealed record ErroredRow(
        string ApplicationName, string Company, string Role, string Link, string Source,
        string Detail, DateTime At, int Runs, int RecentFailures);

    private const string ErroredFrom =
        """
        FROM applications a
        JOIN LATERAL (
            SELECT e.kind, e.detail, e.created_at FROM agent_evidence e
            WHERE e.tenant_id = a.tenant_id AND e.application_name = a.name
            ORDER BY e.created_at DESC, e.id DESC LIMIT 1) last ON true
        WHERE a.tenant_id = @t AND a.status = 'ready' AND last.kind = 'failed'
          AND NOT EXISTS (SELECT 1 FROM submit_requests r
                          WHERE r.tenant_id = a.tenant_id AND r.application_name = a.name AND r.done_at IS NULL)
          -- A LinkedIn cap stand-down is a wait, not a failure (the old ones were written as failed).
          AND COALESCE(last.detail->>'reason', '') NOT LIKE 'LinkedIn Easy Apply:%already sent in the last day%'
          -- A packet built after the failure: the application was prepared afresh — LinkedIn's
          -- Apply led to the employer's own site and the lead moved there — and is ready again.
          AND NOT EXISTS (SELECT 1 FROM agent_events v
                          WHERE v.tenant_id = a.tenant_id AND v.application_name = a.name
                            AND v.kind = 'packet' AND v.created_at > last.created_at)
        """;

    /// <summary>
    /// The applications that are stuck: still <c>ready</c>, nothing queued for them, and the
    /// last thing the browser did with them was fail (#284). A failed run used to drop its
    /// application back into Ready, where it looked exactly like a packet nobody had tried
    /// yet. Oldest failure first — the one that has waited longest.
    /// </summary>
    public async Task<IReadOnlyList<Errored>> ErroredAsync(TimeSpan failureWindow)
    {
        var rows = await _conn.QueryAsync<ErroredRow>(
            """
            SELECT a.name AS applicationname, a.company, a.role, a.link, a.source,
                   last.detail::text AS detail, last.created_at AS at,
                   (SELECT count(*)::int FROM agent_evidence e
                    WHERE e.tenant_id = a.tenant_id AND e.application_name = a.name) AS runs,
                   (SELECT count(*)::int FROM agent_evidence e
                    WHERE e.tenant_id = a.tenant_id AND e.application_name = a.name
                      AND e.kind = 'failed' AND e.created_at > now() - @window
                      AND NOT (e.detail ? 'awaiting_session'
                               OR COALESCE(e.detail->>'reason', '') || ' ' || COALESCE(e.detail->>'error', '') ~ 'LinkedIn showed its signed-out page|LinkedIn asked to sign in')) AS recentfailures
            """ + "\n" + ErroredFrom + " ORDER BY last.created_at, a.name",
            new { t = _t, window = failureWindow });
        return rows.Select(r => new Errored(r.ApplicationName, r.Company, r.Role, r.Link, r.Source,
            JsonDocument.Parse(r.Detail.Length > 0 ? r.Detail : "{}").RootElement.Clone(),
            new DateTimeOffset(DateTime.SpecifyKind(r.At, DateTimeKind.Utc)), r.Runs, r.RecentFailures)).ToList();
    }

    /// <summary>Real submissions to a site inside <paramref name="window"/> — what paces LinkedIn Easy Apply (#278).</summary>
    public Task<int> SubmittedSinceAsync(string hostLike, TimeSpan window) =>
        _conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM agent_evidence WHERE tenant_id = @t AND kind = 'submitted' AND url ILIKE @like AND created_at > now() - @window",
            new { t = _t, like = "%" + hostLike + "%", window });

    /// <summary>How many applications <see cref="ErroredAsync"/> would list — the strip's number.</summary>
    /// <summary>
    /// A cheap fingerprint of everything <see cref="ErroredAsync"/> reads (#349), so the SPA's
    /// 5-second poll can get a 304 instead of re-running it: the applications revision
    /// (status, company, link), the evidence written, the queue's pending set (a finished
    /// row is reused, so its times, not its id, say it moved), the newest packet, and a
    /// 10-minute clock — a failure ageing out of the retry window changes a row with
    /// nothing written at all. The tenant is in it so two accounts never share a tag.
    /// </summary>
    public async Task<string> ErroredFingerprintAsync() =>
        await _conn.ExecuteScalarAsync<string>(
            """
            SELECT md5(concat_ws('|', @t,
                (SELECT applications_revision FROM users WHERE id = @t),
                (SELECT count(*) FROM agent_evidence WHERE tenant_id = @t),
                (SELECT max(id) FROM agent_evidence WHERE tenant_id = @t),
                (SELECT count(*) FROM submit_requests WHERE tenant_id = @t AND done_at IS NULL),
                (SELECT max(requested_at) FROM submit_requests WHERE tenant_id = @t),
                (SELECT max(done_at) FROM submit_requests WHERE tenant_id = @t),
                (SELECT max(created_at) FROM agent_events WHERE tenant_id = @t AND kind = 'packet'),
                floor(extract(epoch FROM now()) / 600)))
            """,
            new { t = _t }) ?? "";

    public Task<int> ErroredCountAsync() =>
        _conn.ExecuteScalarAsync<int>("SELECT count(*)::int " + ErroredFrom, new { t = _t });

    /// <summary>The newest piece of evidence for each of the named applications, without
    /// bytes — what the Pipeline view labels a queued request by (parked on a security
    /// code, last dry run clean or not). Applications with no evidence are absent.</summary>
    public async Task<IReadOnlyDictionary<string, (string Kind, JsonElement Detail, DateTimeOffset CreatedAt)>> LatestPerApplicationAsync(
        IEnumerable<string> names)
    {
        var list = names.Select(Slug.Normalize).Distinct(StringComparer.Ordinal).ToArray();
        if (list.Length == 0) return new Dictionary<string, (string, JsonElement, DateTimeOffset)>(StringComparer.Ordinal);
        var rows = await _conn.QueryAsync<(string Name, string Kind, string Detail, DateTime CreatedAt)>(
            """
            SELECT DISTINCT ON (application_name) application_name, kind, detail::text, created_at
            FROM agent_evidence
            WHERE tenant_id = @t AND application_name = ANY(@names)
            ORDER BY application_name, created_at DESC, id DESC
            """,
            new { t = _t, names = list });
        return rows.ToDictionary(r => r.Name, r => (r.Kind,
            JsonDocument.Parse(r.Detail.Length > 0 ? r.Detail : "{}").RootElement.Clone(),
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc))), StringComparer.Ordinal);
    }

    /// <summary>
    /// A dry run that proved the form can be finished unattended: the kind is
    /// <see cref="Kinds.DryRun"/>, no required field went unmapped, nothing errored. This
    /// is the same bar the worker applies before promoting a run it has just finished
    /// (#185); it is what lets a packet filled while dry-run was on be promoted later,
    /// once the switch flips, instead of sitting in Ready forever.
    /// </summary>
    public static bool IsCleanDryRun(string kind, JsonElement detail)
    {
        if (kind != Kinds.DryRun || detail.ValueKind != JsonValueKind.Object) return false;
        if (detail.TryGetProperty("unmapped", out var u) && u.ValueKind == JsonValueKind.Array && u.GetArrayLength() > 0)
            return false;
        if (detail.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && (e.GetString() ?? "").Length > 0)
            return false;
        // A run that mapped nothing proves nothing about the form, however clean it reads:
        // the two looping LinkedIn packets of #302 each reported "0 mapped, 0 unmapped" and
        // were promoted every pass on the strength of it. An EMPTY mapped list is that
        // signal. An ABSENT one is a row from before the field was recorded, and is judged
        // by the rest of the bar rather than parked on a question it was never asked.
        // A run that reached the board's own Submit is the exception: LinkedIn pre-fills a
        // candidate's contact details, so a dialog with no questions maps nothing and is ready.
        if (detail.TryGetProperty("mapped", out var m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() == 0
            && !(detail.TryGetProperty("reached_submit", out var r) && r.ValueKind == JsonValueKind.True))
            return false;
        return true;
    }

    public async Task<byte[]?> ScreenshotAsync(string appName, long id)
    {
        var bytes = await _conn.QuerySingleOrDefaultAsync<byte[]?>(
            "SELECT screenshot FROM agent_evidence WHERE tenant_id = @t AND application_name = @n AND id = @id",
            new { t = _t, n = Slug.Normalize(appName), id });
        return bytes is null ? null : Crypto.SecretProtector.IsProtected(bytes) ? _protector.UnprotectBytes(bytes) : bytes;
    }
}
