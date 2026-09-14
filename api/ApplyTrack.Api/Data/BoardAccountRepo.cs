// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Security.Cryptography;
using ApplyTrack.Api.Crypto;
using Dapper;
using Microsoft.Extensions.Logging;

namespace ApplyTrack.Api.Data;

/// <summary>The client-safe view of one board account: never the password, only whether one is stored.</summary>
public sealed record BoardAccountView(string Host, string Username, bool HasPassword, DateTimeOffset UpdatedAt);

/// <summary>A board account the browser can sign in with: host, username, decrypted password.</summary>
public sealed record BoardAccount(string Host, string Username, string Password)
{
    /// <summary>Does this account cover <paramref name="host"/> — the same host, a subdomain
    /// of it, or the same registrable domain (two-label approximation)? An account saved
    /// as <c>successfactors.com</c> signs in at <c>career4.successfactors.com</c>.</summary>
    public bool Covers(string host)
    {
        var h = Normalize(host);
        var mine = Normalize(Host);
        if (h.Length == 0 || mine.Length == 0) return false;
        if (h == mine || h.EndsWith("." + mine, StringComparison.Ordinal)) return true;
        static string Reg(string x) { var p = x.Split('.'); return p.Length <= 2 ? x : string.Join('.', p[^2..]); }
        return Reg(h) == Reg(mine);
    }

    /// <summary>The account for a host, or null.</summary>
    public static BoardAccount? For(IEnumerable<BoardAccount>? accounts, string host) =>
        accounts?.FirstOrDefault(a => a.Covers(host));

    /// <summary>A host as stored and compared: lower-case, no scheme, no path, no leading www.</summary>
    public static string Normalize(string host)
    {
        var h = (host ?? "").Trim().ToLowerInvariant();
        if (Uri.TryCreate(h.Contains("://") ? h : "https://" + h, UriKind.Absolute, out var u) && u.Host.Length > 0) h = u.Host;
        h = h.TrimEnd('.');
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }
}

/// <summary>
/// Tenant-scoped read/write of <c>board_accounts</c>: the candidate's own sign-ins on
/// ATSs that only take applications from a signed-in account (SAP SuccessFactors, #216).
/// Same shape as <see cref="MailboxSettingsRepo"/>: the password is sealed at rest and
/// write-only. Every query filters on the tenant.
/// </summary>
public sealed class BoardAccountRepo
{
    private readonly IDbConnection _conn;
    private readonly long _t;
    private readonly SecretProtector _protector;
    private readonly ILogger _log;

    public BoardAccountRepo(IDbConnection conn, long tenantId, SecretProtector protector, ILogger log)
    {
        _conn = conn;
        _t = tenantId;
        _protector = protector;
        _log = log;
    }

    // Npgsql hands a timestamptz back as a UTC DateTime; the view carries it as an offset.
    private sealed record Row(string Host, string Username, string PasswordCiphertext, DateTime UpdatedAt)
    {
        public DateTimeOffset UpdatedAtOffset => new(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc));
    }

    public async Task<List<BoardAccountView>> ListAsync()
    {
        var rows = await ReadRowsAsync();
        return rows.Select(r => new BoardAccountView(r.Host, r.Username, r.PasswordCiphertext.Length > 0, r.UpdatedAtOffset)).ToList();
    }

    /// <summary>Every account the browser can sign in with — host, username and the decrypted
    /// password. An account with no password is a sign-in by emailed code under that address
    /// (its Password is ""). One whose password will not decrypt (key rotated) is left out
    /// with a warning.</summary>
    public async Task<List<BoardAccount>> TargetsAsync()
    {
        var out_ = new List<BoardAccount>();
        foreach (var r in await ReadRowsAsync())
        {
            if (r.Username.Length == 0) continue;
            if (r.PasswordCiphertext.Length == 0) { out_.Add(new BoardAccount(r.Host, r.Username, "")); continue; }
            if (!_protector.Available) continue;
            try { out_.Add(new BoardAccount(r.Host, r.Username, _protector.Unprotect(r.PasswordCiphertext))); }
            catch (CryptographicException)
            {
                _log.LogWarning("Stored board account password for tenant {TenantId} at {Host} failed to decrypt (key rotated?); re-enter it.", _t, r.Host);
            }
        }
        return out_;
    }

    /// <summary>Save one account. A null password keeps the stored one; an empty string clears it.</summary>
    public async Task UpsertAsync(string host, string username, string? password)
    {
        host = BoardAccount.Normalize(host);
        if (host.Length == 0 || !host.Contains('.'))
            throw new AppValidationException("the board host is a hostname like career4.successfactors.com");
        var ciphertext = "";
        if (!string.IsNullOrEmpty(password))
        {
            if (!_protector.Available)
                throw new AppValidationException("this instance can't store a board password (operator must set APPLYTRACK_SECRETS_KEY)");
            ciphertext = _protector.Protect(password);
        }
        var passwordUpdate = password is null ? "" : "password_ciphertext = EXCLUDED.password_ciphertext,";
        await _conn.ExecuteAsync(
            $"""
             INSERT INTO board_accounts (tenant_id, host, username, password_ciphertext, updated_at)
             VALUES (@t, @host, @username, @ciphertext, now())
             ON CONFLICT (tenant_id, host) DO UPDATE SET
                 username = EXCLUDED.username,
                 {passwordUpdate}
                 updated_at = now()
             """,
            new { t = _t, host, username = username.Trim(), ciphertext });
    }

    public async Task<bool> DeleteAsync(string host) =>
        await _conn.ExecuteAsync("DELETE FROM board_accounts WHERE tenant_id = @t AND host = @host",
            new { t = _t, host = BoardAccount.Normalize(host) }) > 0;

    private async Task<List<Row>> ReadRowsAsync() =>
        (await _conn.QueryAsync<Row>(
            "SELECT host, username, password_ciphertext AS passwordciphertext, updated_at AS updatedat FROM board_accounts WHERE tenant_id = @t ORDER BY host",
            new { t = _t })).ToList();
}
