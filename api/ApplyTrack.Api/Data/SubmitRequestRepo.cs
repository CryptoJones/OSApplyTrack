// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>A queued submit request as the worker claims it (cross-tenant).</summary>
public sealed record SubmitRequest(long Id, long TenantId, string ApplicationName, bool DryRun);

/// <summary>A tenant's view of the request behind one application, if any.</summary>
public sealed record SubmitRequestView(bool DryRun, DateTimeOffset RequestedAt, DateTimeOffset? ClaimedAt, DateTimeOffset? DoneAt)
{
    public bool Pending => DoneAt is null;
}

/// <summary>
/// The human's Submit click as a row the worker drains. Tenant-scoped enqueue/read;
/// the cross-tenant claim lives in <see cref="SubmitQueue"/> because it is the
/// worker's privileged fan-in, like the poller's <c>poll_requests</c> drain.
/// </summary>
public sealed class SubmitRequestRepo
{
    private readonly IDbConnection _conn;
    private readonly long _t;

    public SubmitRequestRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    /// <summary>Queue a request. False when one is already in flight for this application
    /// (a double-click is a no-op); a finished request is reused.</summary>
    public async Task<bool> EnqueueAsync(string appName, bool dryRun)
    {
        var affected = await _conn.ExecuteAsync(
            """
            INSERT INTO submit_requests (tenant_id, application_name, dry_run)
            VALUES (@t, @n, @dryRun)
            ON CONFLICT (tenant_id, application_name) DO UPDATE SET
                dry_run = EXCLUDED.dry_run, requested_at = now(), claimed_at = NULL, done_at = NULL
            WHERE submit_requests.done_at IS NOT NULL
            """,
            new { t = _t, n = Slug.Normalize(appName), dryRun });
        return affected > 0;
    }

    private sealed record Row(bool DryRun, DateTime RequestedAt, DateTime? ClaimedAt, DateTime? DoneAt);

    public async Task<SubmitRequestView?> GetAsync(string appName)
    {
        var r = await _conn.QuerySingleOrDefaultAsync<Row?>(
            "SELECT dry_run AS dryrun, requested_at AS requestedat, claimed_at AS claimedat, done_at AS doneat "
            + "FROM submit_requests WHERE tenant_id = @t AND application_name = @n",
            new { t = _t, n = Slug.Normalize(appName) });
        return r is null ? null : new SubmitRequestView(r.DryRun, Utc(r.RequestedAt)!.Value, Utc(r.ClaimedAt), Utc(r.DoneAt));
    }

    private static DateTimeOffset? Utc(DateTime? d) =>
        d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc));
}

/// <summary>The worker's side of the queue: claim the oldest pending request (or one
/// whose claim went stale — a crashed pass), and mark it done. UPDATE, never DELETE.</summary>
public static class SubmitQueue
{
    public static Task<SubmitRequest?> ClaimNextAsync(IDbConnection conn) =>
        conn.QuerySingleOrDefaultAsync<SubmitRequest?>(
            """
            UPDATE submit_requests SET claimed_at = now()
            WHERE id = (
                SELECT id FROM submit_requests
                WHERE done_at IS NULL
                  AND (claimed_at IS NULL OR claimed_at < now() - interval '15 minutes')
                ORDER BY requested_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING id, tenant_id AS tenantid, application_name AS applicationname, dry_run AS dryrun
            """);

    public static Task CompleteAsync(IDbConnection conn, long id) =>
        conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE id = @id", new { id });
}
