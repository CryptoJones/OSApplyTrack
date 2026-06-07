// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Tenant-scoped company blacklist — C# heir to the Python <c>Blacklist</c>. The
/// stored <c>company</c> is always the normalized key (<see cref="Slug.NormCompany"/>),
/// so add/remove are case- and punctuation-insensitive and <see cref="ListAsync"/>
/// returns the same normalized, sorted keys the Python poller compares against.
/// </summary>
public sealed class BlacklistRepo
{
    private readonly IDbConnection _conn;
    private readonly long _t;

    public BlacklistRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    public async Task<IReadOnlyList<string>> ListAsync()
    {
        var rows = await _conn.QueryAsync<string>(
            "SELECT company FROM blacklist WHERE tenant_id = @t ORDER BY company",
            new { t = _t });
        return rows.ToList();
    }

    /// <summary>Add the normalized company key; returns whether a new row was inserted.</summary>
    public async Task<bool> AddAsync(string company)
    {
        var key = Slug.NormCompany(company);
        if (key.Length == 0)
            return false;
        var affected = await _conn.ExecuteAsync(
            """
            INSERT INTO blacklist (tenant_id, company) VALUES (@t, @key)
            ON CONFLICT (tenant_id, company) DO NOTHING
            """,
            new { t = _t, key });
        return affected > 0;
    }

    /// <summary>Remove the normalized company key; returns whether a row was deleted.</summary>
    public async Task<bool> RemoveAsync(string company)
    {
        var key = Slug.NormCompany(company);
        var affected = await _conn.ExecuteAsync(
            "DELETE FROM blacklist WHERE tenant_id = @t AND company = @key",
            new { t = _t, key });
        return affected > 0;
    }

    /// <summary>
    /// Flip this company's not-yet-acted-on leads (status lead/ready) to "passed",
    /// bumping their version. Heir to <c>_pass_open_for</c>; the normalized-company
    /// match is the SQL twin of <c>_norm_company</c>. Returns the number flipped.
    /// </summary>
    public async Task<int> PassOpenLeadsAsync(string company)
    {
        var key = Slug.NormCompany(company);
        return await _conn.ExecuteAsync(
            """
            UPDATE applications SET
                status = 'passed', version = version + 1, updated_at = now()
            WHERE tenant_id = @t
              AND status IN ('lead', 'ready')
              AND trim(both '-' from regexp_replace(lower(company), '[^a-z0-9]+', '-', 'g')) = @key
            """,
            new { t = _t, key });
    }
}
