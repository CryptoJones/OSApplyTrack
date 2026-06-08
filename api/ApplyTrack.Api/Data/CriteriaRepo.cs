// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Tenant-scoped read/write of the single <c>search_profiles</c> row — C# heir to
/// the Python <c>Criteria.load</c>/<c>save</c>. Absence of a row means "fall back
/// to <see cref="Criteria.Defaults"/>", matching the file-missing branch in Python.
/// The Python poller reads the same columns directly in Steps 3-4.
/// </summary>
public sealed class CriteriaRepo
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IDbConnection _conn;
    private readonly long _t;

    public CriteriaRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    public async Task<Criteria> GetAsync()
    {
        // Compose the row into the exact JSON shape Criteria.FromJson expects so the
        // GET output reuses every normalization rule (empty keywords -> defaults,
        // score clamp, junk-source drop). One query, no underscore-map dependency.
        var json = await _conn.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT jsonb_build_object(
                'keywords', keywords,
                'default_lane', default_lane,
                'min_fit_score', min_fit_score,
                'remote_only', remote_only,
                'exclude_locations', exclude_locations,
                'sources', sources,
                'ats_boards', ats_boards
            )::text
            FROM search_profiles
            WHERE tenant_id = @t
            """,
            new { t = _t });

        if (json is null)
            return Criteria.Defaults();

        using var doc = JsonDocument.Parse(json);
        return Criteria.FromJson(doc.RootElement);
    }

    public async Task UpsertAsync(Criteria c, IDbTransaction? tx = null)
    {
        await _conn.ExecuteAsync(
            """
            INSERT INTO search_profiles
                (tenant_id, keywords, default_lane, min_fit_score, remote_only,
                 exclude_locations, sources, ats_boards, updated_at)
            VALUES
                (@t, CAST(@keywords AS jsonb), @lane, @score, @remote,
                 CAST(@exclude AS jsonb), CAST(@sources AS jsonb), CAST(@boards AS jsonb), now())
            ON CONFLICT (tenant_id) DO UPDATE SET
                keywords          = EXCLUDED.keywords,
                default_lane      = EXCLUDED.default_lane,
                min_fit_score     = EXCLUDED.min_fit_score,
                remote_only       = EXCLUDED.remote_only,
                exclude_locations = EXCLUDED.exclude_locations,
                sources           = EXCLUDED.sources,
                ats_boards        = EXCLUDED.ats_boards,
                updated_at        = now()
            """,
            new
            {
                t = _t,
                keywords = JsonSerializer.Serialize(c.Keywords, JsonOpts),
                lane = c.DefaultLane,
                score = c.MinFitScore,
                remote = c.RemoteOnly,
                exclude = JsonSerializer.Serialize(c.ExcludeLocations, JsonOpts),
                sources = JsonSerializer.Serialize(c.Sources, JsonOpts),
                boards = JsonSerializer.Serialize(c.AtsBoards, JsonOpts),
            },
            tx);
    }
}
