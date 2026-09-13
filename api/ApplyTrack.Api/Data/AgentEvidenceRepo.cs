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

    public async Task<byte[]?> ScreenshotAsync(string appName, long id)
    {
        var bytes = await _conn.QuerySingleOrDefaultAsync<byte[]?>(
            "SELECT screenshot FROM agent_evidence WHERE tenant_id = @t AND application_name = @n AND id = @id",
            new { t = _t, n = Slug.Normalize(appName), id });
        return bytes is null ? null : Crypto.SecretProtector.IsProtected(bytes) ? _protector.UnprotectBytes(bytes) : bytes;
    }
}
