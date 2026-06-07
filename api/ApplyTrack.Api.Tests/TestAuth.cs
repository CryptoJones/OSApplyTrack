// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Auth;
using Dapper;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Test-side seeding for the auth spine. Lets a test stand up an authenticated
/// tenant by writing a user + session row directly (the same rows the middleware
/// reads), skipping the email round-trip when the login flow itself isn't under
/// test. A unique email per call yields a fresh, empty tenant on the shared
/// container.
/// </summary>
internal static class TestAuth
{
    /// <summary>Inserts a fresh user and a live session for it; returns the tenant id and session id.</summary>
    public static async Task<(long TenantId, string Sid)> SeedSessionAsync(
        string connectionString, string? email = null)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var tenantId = await EnsureUserAsync(conn, email ?? UniqueEmail());
        var sid = Tokens.NewOpaque();
        await conn.ExecuteAsync(
            "INSERT INTO sessions (id, user_id, expires_at) VALUES (@sid, @uid, now() + interval '1 day')",
            new { sid, uid = tenantId });
        return (tenantId, sid);
    }

    public static Task<long> EnsureUserAsync(NpgsqlConnection conn, string email) =>
        conn.ExecuteScalarAsync<long>(
            "INSERT INTO users (email) VALUES (@email) "
            + "ON CONFLICT (email) DO UPDATE SET status = users.status RETURNING id",
            new { email });

    public static string UniqueEmail() => $"test-{Guid.NewGuid():N}@example.com";
}
