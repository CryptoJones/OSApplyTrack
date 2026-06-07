// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>An account. For v1 <c>tenant_id == Id</c>.</summary>
public sealed record User(long Id, string Email, string Status);

/// <summary>
/// Read/write of the <c>users</c> table. Not tenant-scoped: this is where a tenant
/// is born (signup) and resolved (the auth/me lookup), so it sits above the
/// per-tenant choke-point rather than behind it.
/// </summary>
public sealed class UserRepo
{
    private readonly IDbConnection _conn;

    public UserRepo(IDbConnection conn) => _conn = conn;

    /// <summary>
    /// Upsert by email, returning the user id (== tenant_id for v1). Idempotent so a
    /// magic-link request for a new or existing address always resolves a user — the
    /// no-op ON CONFLICT update is just there to make RETURNING fire on both paths.
    /// </summary>
    public Task<long> EnsureAsync(string email) =>
        _conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (email) VALUES (@email)
            ON CONFLICT (email) DO UPDATE SET status = users.status
            RETURNING id
            """,
            new { email });

    public Task<User?> GetAsync(long id) =>
        _conn.QuerySingleOrDefaultAsync<User?>(
            "SELECT id, email, status FROM users WHERE id = @id", new { id });
}
