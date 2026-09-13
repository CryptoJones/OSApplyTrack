// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using ApplyTrack.Api.Agent.Browser;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// The worker's heartbeat row (<c>agent_workers</c>): one per worker container, stamped
/// on every tick with whether that worker drives a browser. Instance-wide, not tenant
/// data — it answers "is there anything for a Submit click to land on?", which the api
/// process cannot tell from its own configuration because the browser is wired to the
/// agent container only (#159).
/// </summary>
public static class AgentWorkerRegistry
{
    /// <summary>How long after its last heartbeat a worker still counts as present. The
    /// submit lane ticks every 15 s and the judging pass every 5 min by default; a worker
    /// silent for this long is gone (or wedged), and the UI should say so.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(3);

    public static Task HeartbeatAsync(IDbConnection conn, string workerId, bool browser) =>
        conn.ExecuteAsync(
            """
            INSERT INTO agent_workers (id, browser) VALUES (@id, @browser)
            ON CONFLICT (id) DO UPDATE SET browser = EXCLUDED.browser, seen_at = now()
            """,
            new { id = workerId, browser });

    /// <summary>A worker with a browser has been heard from within <see cref="Freshness"/>.</summary>
    public static Task<bool> BrowserSeenAsync(IDbConnection conn) =>
        conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM agent_workers WHERE browser AND seen_at > now() - @within)",
            new { within = Freshness });

    /// <summary>Any worker has been heard from within <see cref="Freshness"/>.</summary>
    public static Task<bool> WorkerSeenAsync(IDbConnection conn) =>
        conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM agent_workers WHERE seen_at > now() - @within)",
            new { within = Freshness });
}

/// <summary>
/// What the request pipeline asks before queueing a browser run: this process has a
/// browser itself (a single-container deploy), or a worker that does has been heard from
/// recently. The second is the shipped shape — compose, production compose and the
/// quadlets all set <c>Browser__Endpoint</c> on the agent service only.
/// </summary>
public sealed class BrowserAvailability
{
    private readonly BrowserOptions _local;
    private readonly IDbConnection _conn;

    public BrowserAvailability(BrowserOptions local, IDbConnection conn)
    {
        _local = local;
        _conn = conn;
    }

    public async Task<bool> IsAvailableAsync() =>
        _local.IsConfigured || await AgentWorkerRegistry.BrowserSeenAsync(_conn);
}
