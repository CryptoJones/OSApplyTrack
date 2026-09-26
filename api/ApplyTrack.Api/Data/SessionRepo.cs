// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>A signed-in browser, as the owner sees it in "where am I signed in". <c>Id</c> is
/// the stored hash — a handle for revoking it, never usable as a cookie.</summary>
public sealed record SessionInfo(
    string Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt,
    string UserAgent, bool Current);

/// <summary>A resolved session: the user it belongs to, and the new expiry when this request
/// slid it forward (the cookie must be re-issued to match), else null.</summary>
public sealed record ResolvedSession(long UserId, string Id, DateTimeOffset? RenewedUntil);

/// <summary>
/// Server-side sessions: the cookie holds an opaque high-entropy id; the row holds only
/// its sha256 (#346), so a database read cannot mint a login. Server-side (not JWT) so
/// logout / revocation is instant — deleting the row kills the session immediately.
/// Expiry slides: <see cref="IdleTtl"/> after the last use, never past
/// <see cref="MaxLifetime"/> from sign-in.
/// </summary>
public sealed class SessionRepo
{
    public static readonly TimeSpan IdleTtl = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(90);

    // Touch last_seen_at / slide the expiry at most this often, so a busy tab isn't a
    // write per request.
    private static readonly TimeSpan TouchEvery = TimeSpan.FromHours(1);

    private const int MaxUserAgent = 256;

    private readonly IDbConnection _conn;

    public SessionRepo(IDbConnection conn) => _conn = conn;

    /// <summary>The stored form of a cookie's session id: lowercase hex sha256.</summary>
    public static string Hash(string sid) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));

    /// <summary>Store a new session by hash; returns when it expires (for the cookie).</summary>
    public async Task<DateTimeOffset> CreateAsync(string sid, long userId, string? userAgent)
    {
        var expiresAt = DateTimeOffset.UtcNow + IdleTtl;
        var ua = (userAgent ?? "").Trim();
        if (ua.Length > MaxUserAgent) ua = ua[..MaxUserAgent];
        await _conn.ExecuteAsync(
            """
            INSERT INTO sessions (id, user_id, expires_at, user_agent)
            VALUES (@id, @userId, @expiresAt, @ua)
            """,
            new { id = Hash(sid), userId, expiresAt, ua });
        return expiresAt;
    }

    /// <summary>
    /// Resolve a session cookie to its user, or null if missing, expired, or the account is
    /// not <c>active</c> (an operator disables an account by setting <c>users.status</c>).
    /// This is the lookup the per-request tenancy choke-point builds the scoped repo on.
    /// At most once per <see cref="TouchEvery"/> it slides the expiry forward.
    /// </summary>
    public async Task<ResolvedSession?> ResolveAsync(string sid)
    {
        var id = Hash(sid);
        var rows = await _conn.QueryAsync<(long UserId, DateTime LastSeenAt, DateTime CreatedAt)>(
            """
            SELECT s.user_id, s.last_seen_at, s.created_at
            FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.id = @id AND s.expires_at > now() AND u.status = 'active'
            """,
            new { id });
        if (!rows.Any())
            return null;
        var row = rows.First();

        var now = DateTimeOffset.UtcNow;
        if (now - Utc(row.LastSeenAt) < TouchEvery)
            return new ResolvedSession(row.UserId, id, null);

        var renewed = Min(now + IdleTtl, Utc(row.CreatedAt) + MaxLifetime);
        await _conn.ExecuteAsync(
            "UPDATE sessions SET last_seen_at = now(), expires_at = @renewed WHERE id = @id AND user_id = @uid",
            new { id, renewed, uid = row.UserId });
        return new ResolvedSession(row.UserId, id, renewed);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

    /// <summary>Drop the session this cookie holds (logout).</summary>
    public Task DeleteAsync(string sid) =>
        _conn.ExecuteAsync("DELETE FROM sessions WHERE id = @id", new { id = Hash(sid) });

    /// <summary>The user's live sessions, most recently used first; <paramref name="currentId"/>
    /// (a stored hash) is flagged as this browser.</summary>
    public async Task<IReadOnlyList<SessionInfo>> ListAsync(long userId, string? currentId)
    {
        var rows = await _conn.QueryAsync<(string Id, DateTime CreatedAt, DateTime LastSeenAt, DateTime ExpiresAt, string UserAgent)>(
            """
            SELECT id, created_at, last_seen_at, expires_at, user_agent
            FROM sessions WHERE user_id = @userId AND expires_at > now()
            ORDER BY last_seen_at DESC, created_at DESC
            """,
            new { userId });
        return rows.Select(r => new SessionInfo(
            r.Id, Utc(r.CreatedAt), Utc(r.LastSeenAt), Utc(r.ExpiresAt), r.UserAgent, r.Id == currentId)).ToList();
    }

    /// <summary>Sign out everywhere else: drop every session of this user but
    /// <paramref name="keepId"/>. Returns how many were revoked.</summary>
    public Task<int> DeleteOthersAsync(long userId, string? keepId) =>
        _conn.ExecuteAsync(
            "DELETE FROM sessions WHERE user_id = @userId AND id IS DISTINCT FROM @keepId",
            new { userId, keepId });

    /// <summary>Revoke one of this user's sessions by its listed id. False when it isn't theirs
    /// (or is already gone).</summary>
    public async Task<bool> DeleteByIdAsync(long userId, string id) =>
        await _conn.ExecuteAsync(
            "DELETE FROM sessions WHERE user_id = @userId AND id = @id",
            new { userId, id }) > 0;
}
