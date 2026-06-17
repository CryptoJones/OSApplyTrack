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
    /// magic-link request for a new or existing address always resolves a user.
    /// <c>ON CONFLICT DO NOTHING</c> (not <c>DO UPDATE</c>) so the common existing-user
    /// login path writes no dead tuple — the CTE returns the freshly-inserted id, or
    /// falls through to SELECT the existing one. citext email column = case-insensitive
    /// match, same as the unique index the conflict targets.
    /// </summary>
    public Task<long> EnsureAsync(string email) =>
        _conn.ExecuteScalarAsync<long>(
            """
            WITH ins AS (
                INSERT INTO users (email) VALUES (@email)
                ON CONFLICT (email) DO NOTHING
                RETURNING id
            )
            SELECT id FROM ins
            UNION ALL
            SELECT id FROM users WHERE email = @email
            LIMIT 1
            """,
            new { email });

    public Task<User?> GetAsync(long id) =>
        _conn.QuerySingleOrDefaultAsync<User?>(
            "SELECT id, email, status FROM users WHERE id = @id", new { id });

    /// <summary>
    /// Delete the account. The per-tenant tables carry <c>ON DELETE CASCADE</c> FKs to
    /// <c>users(id)</c> (migrations 0005/0006/0009), so this one statement also drops the
    /// tenant's applications, search profile, blacklist, seen ledger, queued poll
    /// requests, sessions, and magic tokens. Returns rows affected (0 if already gone).
    /// </summary>
    public Task<int> DeleteAsync(long id) =>
        _conn.ExecuteAsync("DELETE FROM users WHERE id = @id", new { id });
}
