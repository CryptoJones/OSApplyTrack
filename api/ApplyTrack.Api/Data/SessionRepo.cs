// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Server-side sessions: the cookie holds an opaque high-entropy id that is the row
/// PK. Server-side (not JWT) so logout / revocation is instant — deleting the row
/// kills the session immediately.
/// </summary>
public sealed class SessionRepo
{
    private readonly IDbConnection _conn;

    public SessionRepo(IDbConnection conn) => _conn = conn;

    public Task CreateAsync(string id, long userId, DateTimeOffset expiresAt) =>
        _conn.ExecuteAsync(
            "INSERT INTO sessions (id, user_id, expires_at) VALUES (@id, @userId, @expiresAt)",
            new { id, userId, expiresAt });

    /// <summary>
    /// Resolve a session cookie to its user id, or null if missing/expired. This is
    /// the lookup the per-request tenancy choke-point builds the scoped repo on.
    /// </summary>
    public Task<long?> ResolveUserIdAsync(string id) =>
        _conn.ExecuteScalarAsync<long?>(
            "SELECT user_id FROM sessions WHERE id = @id AND expires_at > now()",
            new { id });

    public Task DeleteAsync(string id) =>
        _conn.ExecuteAsync("DELETE FROM sessions WHERE id = @id", new { id });
}
