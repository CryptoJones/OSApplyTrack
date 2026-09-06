// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>One row of the agent's audit trail as the SPA lists it.</summary>
public sealed record AgentEvent(
    long Id, string ApplicationName, string Kind, JsonElement Detail, DateTimeOffset CreatedAt);

/// <summary>
/// Tenant-scoped append/read of <c>agent_events</c> — the agent's audit trail. Rows
/// are keyed to the tenant, not the application, so they survive the application
/// they describe. Event kinds are plain strings (<see cref="Kinds"/>) so a later
/// step can add its own without a migration.
/// </summary>
public sealed class AgentEventRepo
{
    public static class Kinds
    {
        /// <summary>The agent read the posting and decided proceed/skip.</summary>
        public const string Verdict = "verdict";
        /// <summary>The agent could not reach a decision (no model, unusable output, fetch failure).</summary>
        public const string Error = "error";
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IDbConnection _conn;
    private readonly long _t;

    public AgentEventRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    /// <summary>Append one event. <paramref name="detail"/> is serialized as snake_case JSON.
    /// A blank <paramref name="applicationName"/> records a tenant-level event (e.g. "no
    /// model configured") that is about no application in particular.</summary>
    public Task RecordAsync(string kind, string applicationName, object detail, IDbTransaction? tx = null) =>
        _conn.ExecuteAsync(
            "INSERT INTO agent_events (tenant_id, application_name, kind, detail) "
            + "VALUES (@t, @n, @kind, @detail::jsonb)",
            new
            {
                t = _t,
                n = string.IsNullOrWhiteSpace(applicationName) ? "" : Slug.Normalize(applicationName),
                kind,
                detail = JsonSerializer.Serialize(detail, Json),
            },
            tx);

    private sealed record Row(long Id, string ApplicationName, string Kind, string Detail, DateTime CreatedAt);

    /// <summary>Newest first, capped.</summary>
    public async Task<IReadOnlyList<AgentEvent>> ListRecentAsync(int limit = 50)
    {
        var rows = await _conn.QueryAsync<Row>(
            "SELECT id, application_name AS applicationname, kind, detail::text AS detail, created_at AS createdat "
            + "FROM agent_events WHERE tenant_id = @t ORDER BY created_at DESC, id DESC LIMIT @limit",
            new { t = _t, limit = Math.Clamp(limit, 1, 500) });
        return rows.Select(ToEvent).ToList();
    }

    /// <summary>The latest verdict recorded for an application, or null.</summary>
    public async Task<AgentEvent?> LatestVerdictAsync(string applicationName)
    {
        var row = await _conn.QuerySingleOrDefaultAsync<Row?>(
            "SELECT id, application_name AS applicationname, kind, detail::text AS detail, created_at AS createdat "
            + "FROM agent_events WHERE tenant_id = @t AND application_name = @n AND kind = @kind "
            + "ORDER BY created_at DESC, id DESC LIMIT 1",
            new { t = _t, n = Slug.Normalize(applicationName), kind = Kinds.Verdict });
        return row is null ? null : ToEvent(row);
    }

    /// <summary>How many verdicts the agent has reached in the trailing 24 hours — the daily cap.</summary>
    public Task<int> VerdictsSinceAsync(TimeSpan window) =>
        _conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM agent_events WHERE tenant_id = @t AND kind = @kind "
            + "AND created_at > now() - @window",
            new { t = _t, kind = Kinds.Verdict, window });

    private static AgentEvent ToEvent(Row r) =>
        new(r.Id, r.ApplicationName, r.Kind,
            JsonDocument.Parse(r.Detail.Length > 0 ? r.Detail : "{}").RootElement.Clone(),
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)));
}
