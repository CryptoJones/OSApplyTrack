// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Tenant-scoped read/write of the generated cover letter for an application, keyed
/// on the app's slug <c>name</c> (the same public key the SPA uses). One letter per
/// application — drafting again overwrites. The composite FK to <c>applications</c>
/// means a letter only exists while its application does.
/// </summary>
public sealed class CoverLetterRepo
{
    private readonly IDbConnection _conn;
    private readonly long _t;

    private readonly Crypto.SecretProtector _protector;

    public CoverLetterRepo(IDbConnection conn, long tenantId, Crypto.SecretProtector protector)
    {
        _conn = conn;
        _t = tenantId;
        _protector = protector;
    }

    /// <summary>The drafted letter body for an app, or null when none has been generated.
    /// Sealed at rest; a body an older release stored in the clear is read as is.</summary>
    public async Task<string?> GetBodyAsync(string appName)
    {
        var n = Slug.Normalize(appName);
        var body = await _conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT body FROM cover_letters WHERE tenant_id = @t AND application_name = @n",
            new { t = _t, n });
        return body is null ? null : Crypto.SecretProtector.IsProtected(body) ? _protector.Unprotect(body) : body;
    }

    public async Task UpsertAsync(string appName, string body, string model, IDbTransaction? tx = null)
    {
        var n = Slug.Normalize(appName);
        await _conn.ExecuteAsync(
            """
            INSERT INTO cover_letters (tenant_id, application_name, body, model, updated_at)
            VALUES (@t, @n, @body, @model, now())
            ON CONFLICT (tenant_id, application_name) DO UPDATE SET
                body       = EXCLUDED.body,
                model      = EXCLUDED.model,
                updated_at = now()
            """,
            new { t = _t, n, body = _protector.Protect(body), model },
            tx);
    }

    /// <summary>Discard the letter; returns false when there was nothing to delete.</summary>
    public async Task<bool> DeleteAsync(string appName)
    {
        var n = Slug.Normalize(appName);
        var affected = await _conn.ExecuteAsync(
            "DELETE FROM cover_letters WHERE tenant_id = @t AND application_name = @n",
            new { t = _t, n });
        return affected > 0;
    }
}
