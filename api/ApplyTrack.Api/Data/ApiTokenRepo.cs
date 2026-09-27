// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using ApplyTrack.Api.Auth;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>Whether the tenant has a calendar feed link, and when it was made and last read.</summary>
public sealed record CalendarFeedView(bool Enabled, DateTime? CreatedAt, DateTime? LastUsedAt);

/// <summary>
/// Scoped secret tokens (<c>api_tokens</c>, #354) — for callers that can't carry the session
/// cookie. The only scope today is <c>calendar</c>: one per tenant, opening its ICS feed and
/// nothing else. Stored by sha256 only; the plaintext leaves the server once, when made, and
/// making a new one replaces (revokes) the old. The personal API tokens (#356) are meant to
/// join this table with scopes of their own.
/// </summary>
public sealed class ApiTokenRepo
{
    public const string CalendarScope = "calendar";

    private readonly IDbConnection _conn;
    private readonly long _t;

    public ApiTokenRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    private sealed record Row(DateTime CreatedAt, DateTime? LastUsedAt);

    public async Task<CalendarFeedView> GetCalendarAsync()
    {
        var row = await _conn.QuerySingleOrDefaultAsync<Row?>(
            "SELECT created_at AS createdat, last_used_at AS lastusedat FROM api_tokens WHERE tenant_id = @t AND scope = @scope",
            new { t = _t, scope = CalendarScope });
        return row is { } r ? new CalendarFeedView(true, r.CreatedAt, r.LastUsedAt) : new CalendarFeedView(false, null, null);
    }

    /// <summary>Make the tenant's calendar token — replacing any it had — and return the plaintext.</summary>
    public async Task<string> CreateCalendarAsync()
    {
        var token = Tokens.NewOpaque();
        await _conn.ExecuteAsync(
            """
            INSERT INTO api_tokens (tenant_id, scope, token_hash) VALUES (@t, @scope, @hash)
            ON CONFLICT (tenant_id) WHERE scope = 'calendar' DO UPDATE SET
                token_hash = EXCLUDED.token_hash, created_at = now(), last_used_at = NULL
            """,
            new { t = _t, scope = CalendarScope, hash = Tokens.Sha256(token) });
        return token;
    }

    /// <summary>Turn the feed off: the link stops working at once. False when there was none.</summary>
    public async Task<bool> RevokeCalendarAsync() =>
        await _conn.ExecuteAsync(
            "DELETE FROM api_tokens WHERE tenant_id = @t AND scope = @scope", new { t = _t, scope = CalendarScope }) > 0;

    /// <summary>
    /// The tenant a token opens <paramref name="scope"/> for, or null — unknown, revoked, the
    /// wrong scope, or an account that is no longer active. Stamps its last use.
    /// </summary>
    public static async Task<long?> ResolveAsync(IDbConnection conn, string? token, string scope)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
            return null;
        return await conn.QuerySingleOrDefaultAsync<long?>(
            """
            UPDATE api_tokens k SET last_used_at = now()
            FROM users u
            WHERE k.token_hash = @hash AND k.scope = @scope AND u.id = k.tenant_id AND u.status = 'active'
            RETURNING k.tenant_id
            """,
            new { hash = Tokens.Sha256(token.Trim()), scope });
    }
}
