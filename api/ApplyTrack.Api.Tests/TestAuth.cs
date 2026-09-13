// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Crypto;
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
    /// <summary>The master key every test host and repo uses, so rows written by one are
    /// readable by another. Hosts opt out with <c>Secrets:Key</c> = "" plus a key file.</summary>
    internal static readonly SecretProtector Protector = new(MasterKey);
    internal const string MasterKey = "test-master-key";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void ConfigureSecretsForTheSuite()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLYTRACK_SECRETS_KEY")))
            Environment.SetEnvironmentVariable("APPLYTRACK_SECRETS_KEY", MasterKey);
    }

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

    /// <summary>A fresh user, allowed to use auto-apply (the operator's allowlist row) unless
    /// a test takes that away with <see cref="DisallowAgentAsync"/>.</summary>
    public static async Task<long> EnsureUserAsync(NpgsqlConnection conn, string email)
    {
        var id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO users (email) VALUES (@email) "
            + "ON CONFLICT (email) DO UPDATE SET status = users.status RETURNING id",
            new { email });
        await conn.ExecuteAsync("INSERT INTO agent_allowlist (tenant_id, note) VALUES (@id, 'test') ON CONFLICT DO NOTHING", new { id });
        return id;
    }

    /// <summary>Take the tenant off the operator's auto-apply allowlist.</summary>
    public static Task<int> DisallowAgentAsync(NpgsqlConnection conn, long tenantId) =>
        conn.ExecuteAsync("DELETE FROM agent_allowlist WHERE tenant_id = @t", new { t = tenantId });

    public static string UniqueEmail() => $"test-{Guid.NewGuid():N}@example.com";
}
