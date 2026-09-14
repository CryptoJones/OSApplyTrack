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
    }

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
    public async Task<IReadOnlyList<(string Name, string Kind, JsonElement Detail)>> LatestPerReadyApplicationAsync()
    {
        var rows = await _conn.QueryAsync<(string Name, string Kind, string Detail)>(
            """
            SELECT DISTINCT ON (e.application_name) e.application_name, e.kind, e.detail::text
            FROM agent_evidence e
            JOIN applications a ON a.tenant_id = e.tenant_id AND a.name = e.application_name
            WHERE e.tenant_id = @t AND a.status = 'ready'
            ORDER BY e.application_name, e.created_at DESC, e.id DESC
            """,
            new { t = _t });
        return rows.Select(r => (r.Name, r.Kind,
            JsonDocument.Parse(r.Detail.Length > 0 ? r.Detail : "{}").RootElement.Clone())).ToList();
    }

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
