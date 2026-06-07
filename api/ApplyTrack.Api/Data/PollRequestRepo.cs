// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Tenant-scoped writer for the on-demand poll queue (<c>poll_requests</c>). The
/// SPA's "Poll now" button hits <c>POST /api/poll</c>, which enqueues one row
/// here; the decoupled Python worker drains the queue (<c>applytrack poll
/// --drain</c>) and polls this tenant out of band. .NET only ever inserts — the
/// worker owns the DELETE — so this repo's whole job is the enqueue.
/// </summary>
public sealed class PollRequestRepo
{
    private readonly IDbConnection _conn;
    private readonly long _t;

    public PollRequestRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    /// <summary>Enqueue an on-demand discovery poll for this tenant.</summary>
    public async Task EnqueueAsync()
    {
        await _conn.ExecuteAsync(
            "INSERT INTO poll_requests (tenant_id) VALUES (@t)",
            new { t = _t });
    }
}
