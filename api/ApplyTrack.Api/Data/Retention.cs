// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using Dapper;
using Npgsql;

namespace ApplyTrack.Api.Data;

/// <summary>
/// How long the tables that only ever grow keep what they hold (#350), bound from the
/// <c>Retention</c> config section (env <c>Retention__ScreenshotDays</c> and so on). A
/// day count of 0 or less switches that step off. The defaults are deliberately
/// conservative: nothing the Errors view, the retry loop or the agent's skip ledger reads
/// is ever removed, and the <c>seen</c> ledger — the poller's "never ping a company
/// twice" guarantee — is kept forever unless the operator opts in.
/// </summary>
public sealed class RetentionOptions
{
    /// <summary>Run the daily sweep at all (in the migrating container only).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hours between sweeps.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>Seconds after boot before the first sweep, so a restart loop is not a purge loop.</summary>
    public int InitialDelaySeconds { get; set; } = 300;

    /// <summary>A screenshot older than this loses its bytes (the evidence row, its detail
    /// and confirmation text stay). Never one of a submission, never one of an application
    /// still in Ready (which is where Errors live), never the newest
    /// <see cref="ScreenshotsKeptPerApplication"/> of an application.</summary>
    public int ScreenshotDays { get; set; } = 90;

    /// <summary>The newest this-many screenshots of every application are always kept.</summary>
    public int ScreenshotsKeptPerApplication { get; set; } = 3;

    /// <summary>Sessions and magic-link tokens are deleted this long after they expire —
    /// an expired one can no longer sign anybody in.</summary>
    public int ExpiredGraceDays { get; set; } = 1;

    /// <summary>Agent audit events older than this are deleted — except verdicts (the
    /// agent's terminal skip), packet builds (what clears an Error), accounts created in
    /// the person's name, and anything about an application still a lead or in Ready.</summary>
    public int AgentEventDays { get; set; } = 180;

    /// <summary>Dedup keys older than this are forgotten. Off (0) by default: a forgotten
    /// key lets the poller stage that listing again if its application was deleted.</summary>
    public int SeenDays { get; set; }
}

/// <summary>What one sweep removed.</summary>
public sealed record RetentionResult(int Screenshots, int Sessions, int MagicTokens, int AgentEvents, int Seen)
{
    public int Total => Screenshots + Sessions + MagicTokens + AgentEvents + Seen;

    public static RetentionResult operator +(RetentionResult a, RetentionResult b) =>
        new(a.Screenshots + b.Screenshots, a.Sessions + b.Sessions, a.MagicTokens + b.MagicTokens,
            a.AgentEvents + b.AgentEvents, a.Seen + b.Seen);
}

/// <summary>The sweep itself: idempotent, one tenant at a time, every statement scoped by it.</summary>
public static class Retention
{
    /// <summary>Audit events the application logic reads back, whatever their age.</summary>
    public static readonly string[] KeptEventKinds =
    [
        AgentEventRepo.Kinds.Verdict, Agent.PacketBuilder.PacketEvent, AgentEventRepo.Kinds.AccountCreated,
    ];

    public static async Task<RetentionResult> SweepAsync(IDbConnection conn, RetentionOptions o, CancellationToken ct = default)
    {
        var total = new RetentionResult(0, 0, 0, 0, 0);
        foreach (var t in await conn.QueryAsync<long>("SELECT id FROM users ORDER BY id"))
        {
            ct.ThrowIfCancellationRequested();
            total += await SweepTenantAsync(conn, t, o);
        }
        return total;
    }

    public static async Task<RetentionResult> SweepTenantAsync(IDbConnection conn, long t, RetentionOptions o)
    {
        var screenshots = o.ScreenshotDays <= 0 ? 0 : await conn.ExecuteAsync(
            """
            UPDATE agent_evidence e SET screenshot = NULL
            WHERE e.tenant_id = @t AND e.screenshot IS NOT NULL
              AND e.kind <> 'submitted'
              AND e.created_at < now() - make_interval(days => @days)
              AND NOT EXISTS (SELECT 1 FROM applications a
                              WHERE a.tenant_id = e.tenant_id AND a.name = e.application_name
                                AND a.status = 'ready')
              AND e.id NOT IN (
                  SELECT id FROM (
                      SELECT id, row_number() OVER (PARTITION BY application_name
                                                    ORDER BY created_at DESC, id DESC) AS n
                      FROM agent_evidence
                      WHERE tenant_id = @t AND screenshot IS NOT NULL) newest
                  WHERE n <= @keep)
            """,
            new { t, days = o.ScreenshotDays, keep = Math.Max(0, o.ScreenshotsKeptPerApplication) });

        var grace = Math.Max(0, o.ExpiredGraceDays);
        var sessions = await conn.ExecuteAsync(
            "DELETE FROM sessions WHERE user_id = @t AND expires_at < now() - make_interval(days => @grace)",
            new { t, grace });
        var tokens = await conn.ExecuteAsync(
            "DELETE FROM magic_tokens WHERE user_id = @t AND expires_at < now() - make_interval(days => @grace)",
            new { t, grace });

        var events = o.AgentEventDays <= 0 ? 0 : await conn.ExecuteAsync(
            """
            DELETE FROM agent_events v
            WHERE v.tenant_id = @t AND v.created_at < now() - make_interval(days => @days)
              AND v.kind <> ALL(@kept)
              AND NOT EXISTS (SELECT 1 FROM applications a
                              WHERE a.tenant_id = v.tenant_id AND a.name = v.application_name
                                AND a.status IN ('lead', 'ready'))
            """,
            new { t, days = o.AgentEventDays, kept = KeptEventKinds });

        var seen = o.SeenDays <= 0 ? 0 : await conn.ExecuteAsync(
            "DELETE FROM seen WHERE tenant_id = @t AND created_at < now() - make_interval(days => @days)",
            new { t, days = o.SeenDays });

        return new RetentionResult(screenshots, sessions, tokens, events, seen);
    }
}

/// <summary>
/// Runs <see cref="Retention.SweepAsync"/> every <see cref="RetentionOptions.IntervalHours"/>.
/// Registered only in the migrating (owner) container: the agent's and the poller's
/// least-privilege roles hold no DELETE and no access to sessions.
/// </summary>
public sealed class RetentionWorker(NpgsqlDataSource db, RetentionOptions options, ILogger<RetentionWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.InitialDelaySeconds)), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var conn = await db.OpenConnectionAsync(ct);
                    var r = await Retention.SweepAsync(conn, options, ct);
                    if (r.Total > 0)
                        log.LogInformation(
                            "retention: cleared {Screenshots} screenshots, deleted {Sessions} sessions, {Tokens} magic tokens, {Events} agent events, {Seen} seen keys",
                            r.Screenshots, r.Sessions, r.MagicTokens, r.AgentEvents, r.Seen);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "retention sweep failed; next try in {Hours} h", options.IntervalHours);
                }
                await Task.Delay(TimeSpan.FromHours(Math.Max(1, options.IntervalHours)), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
