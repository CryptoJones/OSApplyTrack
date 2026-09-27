// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using ApplyTrack.Api.Auth;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>Whether the tenant has a calendar feed link, and when it was made and last read.</summary>
public sealed record CalendarFeedView(bool Enabled, DateTime? CreatedAt, DateTime? LastUsedAt);

/// <summary>A personal API token as its owner sees it listed — never the secret.</summary>
public sealed record ApiTokenView(long Id, string Name, string Scope, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>A token just made: the listing plus the plaintext, which is in this answer only.</summary>
public sealed record NewApiToken(long Id, string Name, string Scope, DateTime CreatedAt, DateTime? LastUsedAt, string Token);

/// <summary>Who a bearer token speaks for, and how far.</summary>
public sealed record BearerHit(long Id, long TenantId, string Scope);

/// <summary>
/// Scoped secret tokens (<c>api_tokens</c>) — for callers that can't carry the session
/// cookie. <c>calendar</c> (#354): one per tenant, opening its ICS feed and nothing else;
/// making a new one replaces (revokes) the old. <c>read</c> and <c>write</c> (#356): personal
/// API tokens, as many as <see cref="MaxPersonal"/>, sent as <c>Authorization: Bearer</c>.
/// Stored by sha256 only; the plaintext leaves the server once, when made.
/// </summary>
public sealed class ApiTokenRepo
{
    public const string CalendarScope = "calendar";
    public const string ReadScope = "read";
    public const string WriteScope = "write";

    /// <summary>Personal tokens carry this prefix, so a leaked one is recognisable (and
    /// scannable) for what it is. Calendar tokens predate it and stay bare.</summary>
    public const string Prefix = "atk_";

    /// <summary>How many personal tokens one account may hold.</summary>
    public const int MaxPersonal = 25;

    public const int MaxNameLength = 80;

    /// <summary>Each personal token's own request budget (Program.cs's global limiter).</summary>
    public const int RequestsPerMinute = 120;

    /// <summary>How stale <c>last_used_at</c> may get before a use writes it again.</summary>
    public static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

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

    /// <summary>The tenant's personal tokens, newest first.</summary>
    public async Task<IReadOnlyList<ApiTokenView>> ListAsync() =>
        (await _conn.QueryAsync<ApiTokenView>(
            """
            SELECT id, name, scope, created_at AS createdat, last_used_at AS lastusedat
            FROM api_tokens WHERE tenant_id = @t AND scope IN ('read', 'write')
            ORDER BY created_at DESC, id DESC
            """,
            new { t = _t })).AsList();

    /// <summary>Make a personal token and return it with its plaintext.</summary>
    public async Task<NewApiToken> CreateAsync(string? name, string? scope)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0)
            throw new AppValidationException("name is required");
        if (n.Length > MaxNameLength)
            throw new AppValidationException($"name must be at most {MaxNameLength} characters");
        var sc = (scope ?? "").Trim().ToLowerInvariant();
        if (sc is not (ReadScope or WriteScope))
            throw new AppValidationException("scope must be 'read' or 'write'");

        var token = Prefix + Tokens.NewOpaque();
        // The cap is checked in the insert itself, so two racing requests can't both slip under it.
        var row = await _conn.QuerySingleOrDefaultAsync<ApiTokenView?>(
            """
            INSERT INTO api_tokens (tenant_id, scope, name, token_hash)
            SELECT @t, @scope, @name, @hash
            WHERE (SELECT count(*) FROM api_tokens WHERE tenant_id = @t AND scope IN ('read', 'write')) < @max
            RETURNING id, name, scope, created_at AS createdat, last_used_at AS lastusedat
            """,
            new { t = _t, scope = sc, name = n, hash = Tokens.Sha256(token), max = MaxPersonal })
            ?? throw new AppValidationException($"at most {MaxPersonal} API tokens; revoke one first");
        return new NewApiToken(row.Id, row.Name, row.Scope, row.CreatedAt, row.LastUsedAt, token);
    }

    /// <summary>Revoke a personal token: it stops working at once. False when it isn't the tenant's.</summary>
    public async Task<bool> RevokeAsync(long id) =>
        await _conn.ExecuteAsync(
            "DELETE FROM api_tokens WHERE tenant_id = @t AND id = @id AND scope IN ('read', 'write')",
            new { t = _t, id }) > 0;

    /// <summary>
    /// The personal token behind an <c>Authorization: Bearer</c> value, or null — unknown,
    /// revoked, a calendar token, or an account that is no longer active. Stamps its last
    /// use, but at most once per <see cref="LastUsedGranularity"/>, so a busy script isn't
    /// a write per request.
    /// </summary>
    public static async Task<BearerHit?> ResolveBearerAsync(IDbConnection conn, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256 || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        return await conn.QuerySingleOrDefaultAsync<BearerHit?>(
            """
            WITH hit AS (
                SELECT k.id, k.tenant_id, k.scope, k.last_used_at
                FROM api_tokens k JOIN users u ON u.id = k.tenant_id
                WHERE k.token_hash = @hash AND k.scope IN ('read', 'write') AND u.status = 'active'
            ), stamp AS (
                UPDATE api_tokens SET last_used_at = now()
                FROM hit
                WHERE api_tokens.id = hit.id
                  AND (hit.last_used_at IS NULL OR hit.last_used_at < now() - @every)
            )
            SELECT id, tenant_id AS tenantid, scope FROM hit
            """,
            new { hash = Tokens.Sha256(token), every = LastUsedGranularity });
    }
}
