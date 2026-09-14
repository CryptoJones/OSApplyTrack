// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>A queued submit request as the worker claims it (cross-tenant).</summary>
/// <param name="Prepare">Rebuild the packet first — judge, discover the form, draft — and
/// only then run the browser. The human's Prepare click, drained where the browser is (#183).</param>
public sealed record SubmitRequest(long Id, long TenantId, string ApplicationName, bool DryRun, bool Prepare = false);

/// <summary>A tenant's view of the request behind one application, if any.</summary>
public sealed record SubmitRequestView(
    bool DryRun, DateTimeOffset RequestedAt, DateTimeOffset? ClaimedAt, DateTimeOffset? DoneAt, bool Prepare = false)
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
    public async Task<bool> EnqueueAsync(string appName, bool dryRun, bool prepare = false)
    {
        var affected = await _conn.ExecuteAsync(
            """
            INSERT INTO submit_requests (tenant_id, application_name, dry_run, prepare)
            VALUES (@t, @n, @dryRun, @prepare)
            ON CONFLICT (tenant_id, application_name) DO UPDATE SET
                dry_run = EXCLUDED.dry_run, prepare = EXCLUDED.prepare,
                requested_at = now(), claimed_at = NULL, done_at = NULL,
                security_code = ''
            WHERE submit_requests.done_at IS NOT NULL
            """,
            new { t = _t, n = Slug.Normalize(appName), dryRun, prepare });
        return affected > 0;
    }

    /// <summary>
    /// Hand the security code the board emailed to the run parked on this application.
    /// True when a run is pending to take it; false when nothing is waiting (the run
    /// timed out, or none was queued) — then the human runs Submit again.
    /// </summary>
    public async Task<bool> SetSecurityCodeAsync(string appName, string code)
    {
        var affected = await _conn.ExecuteAsync(
            "UPDATE submit_requests SET security_code = @code "
            + "WHERE tenant_id = @t AND application_name = @n AND done_at IS NULL",
            new { t = _t, n = Slug.Normalize(appName), code = code.Trim() });
        return affected > 0;
    }

    private sealed record Row(bool DryRun, DateTime RequestedAt, DateTime? ClaimedAt, DateTime? DoneAt, bool Prepare);

    public async Task<SubmitRequestView?> GetAsync(string appName)
    {
        var r = await _conn.QuerySingleOrDefaultAsync<Row?>(
            "SELECT dry_run AS dryrun, requested_at AS requestedat, claimed_at AS claimedat, done_at AS doneat, prepare "
            + "FROM submit_requests WHERE tenant_id = @t AND application_name = @n",
            new { t = _t, n = Slug.Normalize(appName) });
        return r is null ? null : new SubmitRequestView(r.DryRun, Utc(r.RequestedAt)!.Value, Utc(r.ClaimedAt), Utc(r.DoneAt), r.Prepare);
    }

    /// <summary>One pending request as the Pipeline view lists it: the row joined to its
    /// application, in the order the worker will claim it (oldest request first).</summary>
    public sealed record PendingView(
        string ApplicationName, string Company, string Role, string Status, string Link,
        bool DryRun, bool Prepare, DateTimeOffset RequestedAt, DateTimeOffset? ClaimedAt, bool CodeReceived);

    private sealed record PendingRow(
        string ApplicationName, string Company, string Role, string Status, string Link,
        bool DryRun, bool Prepare, DateTime RequestedAt, DateTime? ClaimedAt, bool CodeReceived);

    /// <summary>Every request still pending (queued or claimed, not done), oldest first —
    /// the same order <see cref="SubmitQueue.ClaimNextAsync"/> drains them in.</summary>
    public async Task<IReadOnlyList<PendingView>> PendingAsync()
    {
        var rows = await _conn.QueryAsync<PendingRow>(
            """
            SELECT r.application_name AS applicationname, a.company, a.role, a.status, a.link,
                   r.dry_run AS dryrun, r.prepare, r.requested_at AS requestedat, r.claimed_at AS claimedat,
                   r.security_code <> '' AS codereceived
            FROM submit_requests r
            JOIN applications a ON a.tenant_id = r.tenant_id AND a.name = r.application_name
            WHERE r.tenant_id = @t AND r.done_at IS NULL
            ORDER BY r.requested_at, r.id
            """,
            new { t = _t });
        return rows.Select(r => new PendingView(r.ApplicationName, r.Company, r.Role, r.Status, r.Link,
            r.DryRun, r.Prepare, Utc(r.RequestedAt)!.Value, Utc(r.ClaimedAt), r.CodeReceived)).ToList();
    }

    /// <summary>The applications with a request still pending (queued or claimed, not done).</summary>
    public async Task<HashSet<string>> PendingNamesAsync() =>
        (await _conn.QueryAsync<string>(
            "SELECT application_name FROM submit_requests WHERE tenant_id = @t AND done_at IS NULL",
            new { t = _t })).ToHashSet(StringComparer.Ordinal);

    private static DateTimeOffset? Utc(DateTime? d) =>
        d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc));
}

/// <summary>The worker's side of the queue: claim the oldest pending request (or one
/// whose claim went stale — a crashed pass), and mark it done. UPDATE, never DELETE.</summary>
public static class SubmitQueue
{
    /// <summary>The security code the human handed a parked run, or "" while none has arrived.</summary>
    public static async Task<string> SecurityCodeAsync(IDbConnection conn, long id) =>
        await conn.ExecuteScalarAsync<string?>("SELECT security_code FROM submit_requests WHERE id = @id", new { id }) ?? "";

    public static Task<SubmitRequest?> ClaimNextAsync(IDbConnection conn) =>
        conn.QuerySingleOrDefaultAsync<SubmitRequest?>(
            """
            UPDATE submit_requests SET claimed_at = now()
            WHERE id = (
                SELECT id FROM submit_requests
                WHERE done_at IS NULL
                  AND tenant_id IN (SELECT tenant_id FROM agent_allowlist)
                  AND (claimed_at IS NULL OR claimed_at < now() - interval '15 minutes')
                ORDER BY requested_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING id, tenant_id AS tenantid, application_name AS applicationname, dry_run AS dryrun, prepare
            """);

    public static Task CompleteAsync(IDbConnection conn, long id) =>
        conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE id = @id", new { id });
}
