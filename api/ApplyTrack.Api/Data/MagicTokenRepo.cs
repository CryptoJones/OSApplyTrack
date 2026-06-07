// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Single-use magic-link tokens. The caller hashes the emailed token with sha256
/// and only the hash ever reaches the DB, so a database read cannot mint a login.
/// </summary>
public sealed class MagicTokenRepo
{
    private readonly IDbConnection _conn;

    public MagicTokenRepo(IDbConnection conn) => _conn = conn;

    public Task CreateAsync(long userId, byte[] tokenSha256, DateTimeOffset expiresAt) =>
        _conn.ExecuteAsync(
            """
            INSERT INTO magic_tokens (user_id, token_sha256, expires_at)
            VALUES (@userId, @tokenSha256, @expiresAt)
            """,
            new { userId, tokenSha256, expiresAt });

    /// <summary>
    /// Atomically spend a token: stamps <c>used_at</c> and returns the owning user id,
    /// but only if the token exists, is unexpired, and was never used. Doing it in one
    /// UPDATE … RETURNING closes the double-use race — two clicks can't both win.
    /// Returns null when the token is missing, expired, or already spent.
    /// </summary>
    public Task<long?> ConsumeAsync(byte[] tokenSha256) =>
        _conn.ExecuteScalarAsync<long?>(
            """
            UPDATE magic_tokens SET used_at = now()
            WHERE token_sha256 = @tokenSha256 AND used_at IS NULL AND expires_at > now()
            RETURNING user_id
            """,
            new { tokenSha256 });
}
