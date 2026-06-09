// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Tenant-scoped read/write of the single <c>resume_profiles</c> row — the same
/// one-row-per-tenant shape as <see cref="CriteriaRepo"/>. Absence of a row means
/// "empty résumé" (<see cref="Resume.Empty"/>). The GET composes the row into the
/// JSON shape <see cref="Resume.FromJson"/> expects so every normalization rule is
/// reused.
/// </summary>
public sealed class ResumeRepo
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IDbConnection _conn;
    private readonly long _t;

    public ResumeRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    public async Task<Resume> GetAsync()
    {
        var json = await _conn.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT jsonb_build_object(
                'full_name', full_name,
                'headline', headline,
                'location', location,
                'summary', summary,
                'experience', experience,
                'skills', skills,
                'certifications', certifications,
                'links', links
            )::text
            FROM resume_profiles
            WHERE tenant_id = @t
            """,
            new { t = _t });

        if (json is null)
            return Resume.Empty();

        using var doc = JsonDocument.Parse(json);
        return Resume.FromJson(doc.RootElement);
    }

    public async Task UpsertAsync(Resume r, IDbTransaction? tx = null)
    {
        await _conn.ExecuteAsync(
            """
            INSERT INTO resume_profiles
                (tenant_id, full_name, headline, location, summary,
                 experience, skills, certifications, links, updated_at)
            VALUES
                (@t, @full, @headline, @location, @summary,
                 CAST(@experience AS jsonb), CAST(@skills AS jsonb),
                 CAST(@certs AS jsonb), CAST(@links AS jsonb), now())
            ON CONFLICT (tenant_id) DO UPDATE SET
                full_name      = EXCLUDED.full_name,
                headline       = EXCLUDED.headline,
                location       = EXCLUDED.location,
                summary        = EXCLUDED.summary,
                experience     = EXCLUDED.experience,
                skills         = EXCLUDED.skills,
                certifications = EXCLUDED.certifications,
                links          = EXCLUDED.links,
                updated_at     = now()
            """,
            new
            {
                t = _t,
                full = r.FullName,
                r.Headline,
                r.Location,
                r.Summary,
                experience = JsonSerializer.Serialize(r.Experience, JsonOpts),
                skills = JsonSerializer.Serialize(r.Skills, JsonOpts),
                certs = JsonSerializer.Serialize(r.Certifications, JsonOpts),
                links = JsonSerializer.Serialize(r.Links, JsonOpts),
            },
            tx);
    }
}
