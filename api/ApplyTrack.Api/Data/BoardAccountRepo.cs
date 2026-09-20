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

/// <summary>The tenant's MyGreenhouse sign-in (#218) and whether a session is kept for it (#221).</summary>
public sealed record PortalAccount(string Host, string Username, bool HasSession, DateTimeOffset? SessionExpiresAt);

/// <summary>A board account the browser can sign in with: host, username, decrypted password.</summary>
/// <param name="Session">The kept, signed-in session for this host, when there is one the browser
/// should start from instead of signing in: LinkedIn's <c>{"li_at":…,"JSESSIONID":…}</c>, the same
/// sealed value the poller searches with (#278). Empty for every other account.</param>
public sealed record BoardAccount(string Host, string Username, string Password, string Session = "")
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
    /// <summary>Board-account hosts that mean the candidate's MyGreenhouse sign-in — the same
    /// list the poller reads (<c>mygreenhouse.ACCOUNT_HOSTS</c>).</summary>
    public static readonly string[] PortalHosts = ["greenhouse.io", "my.greenhouse.io"];
    /// <summary>Board-account hosts that mean the candidate's LinkedIn sign-in (#233) — the
    /// same list the poller reads (<c>linkedin.ACCOUNT_HOSTS</c>).</summary>
    public static readonly string[] LinkedInHosts = ["linkedin.com", "www.linkedin.com"];
    /// <summary>Board-account hosts that mean the candidate's Handshake sign-in (#267) — the
    /// same list the poller reads (<c>handshake.ACCOUNT_HOSTS</c>).</summary>
    public static readonly string[] HandshakeHosts = ["joinhandshake.com", "app.joinhandshake.com"];

    /// <summary>The one privileged, cross-tenant query the agent's pass runs (#221): tenants
    /// whose MyGreenhouse account has no kept session, or one that runs out within
    /// <c>@ahead</c>. Everything after it goes through the tenant-scoped repo.</summary>
    public const string PortalRenewalDueSql =
        "SELECT DISTINCT tenant_id FROM board_accounts WHERE host = ANY(@hosts) AND username <> '' "
        + "AND (session_ciphertext = '' OR session_expires_at IS NULL OR session_expires_at < now() + @ahead) ORDER BY tenant_id";

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
    private sealed record Row(string Host, string Username, string PasswordCiphertext, DateTime UpdatedAt, string SessionCiphertext = "", DateTime? SessionExpiresAt = null)
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
            var session = KeptSession(r);
            if (r.PasswordCiphertext.Length == 0) { out_.Add(new BoardAccount(r.Host, r.Username, "", session)); continue; }
            if (!_protector.Available) continue;
            try { out_.Add(new BoardAccount(r.Host, r.Username, _protector.Unprotect(r.PasswordCiphertext), session)); }
            catch (CryptographicException)
            {
                _log.LogWarning("Stored board account password for tenant {TenantId} at {Host} failed to decrypt (key rotated?); re-enter it.", _t, r.Host);
            }
        }
        return out_;
    }

    /// <summary>The LinkedIn account's kept session, unsealed, for the browser to start signed in
    /// with (#278) — and "" for any other host, an expired one, or one that will not decrypt.
    /// Only LinkedIn: MyGreenhouse's and Handshake's sessions are the poller's, never the form's.</summary>
    private string KeptSession(Row r)
    {
        if (r.SessionCiphertext.Length == 0 || !_protector.Available || !LinkedInHosts.Contains(r.Host, StringComparer.OrdinalIgnoreCase))
            return "";
        if (r.SessionExpiresAt is { } expires && DateTime.SpecifyKind(expires, DateTimeKind.Utc) <= DateTime.UtcNow)
            return "";
        try { return _protector.Unprotect(r.SessionCiphertext); }
        catch (CryptographicException) { return ""; }
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

    /// <summary>The tenant's MyGreenhouse account, or null when none is saved.</summary>
    public Task<PortalAccount?> PortalAsync() => AccountOnAsync(PortalHosts);

    /// <summary>The tenant's LinkedIn account (#233), or null when none is saved.</summary>
    public Task<PortalAccount?> LinkedInAsync() => AccountOnAsync(LinkedInHosts);

    /// <summary>The tenant's Handshake account (#267), or null when none is saved.</summary>
    public Task<PortalAccount?> HandshakeAsync() => AccountOnAsync(HandshakeHosts);

    /// <summary>The tenant's board account on one of <paramref name="hosts"/> — a signed-in
    /// discovery source's — with whether a session is kept for it, or null.</summary>
    public async Task<PortalAccount?> AccountOnAsync(string[] hosts)
    {
        var row = await _conn.QueryFirstOrDefaultAsync<(string Host, string Username, bool HasSession, DateTime? ExpiresAt)>(
            "SELECT host, username, session_ciphertext <> '' AS hassession, session_expires_at FROM board_accounts "
            + "WHERE tenant_id = @t AND host = ANY(@hosts) ORDER BY host LIMIT 1",
            new { t = _t, hosts });
        if (row.Username is not { Length: > 0 }) return null;
        return new PortalAccount(row.Host, row.Username, row.HasSession,
            row.ExpiresAt is { } e ? new DateTimeOffset(DateTime.SpecifyKind(e, DateTimeKind.Utc)) : null);
    }

    /// <summary>Keep a portal session on the account row, sealed the way the poller reads it
    /// (<c>applytrack.secrets</c> unseals the same format). An empty session clears it.</summary>
    public async Task SavePortalSessionAsync(string host, string session, DateTimeOffset? expiresAt)
    {
        var sealed_ = "";
        if (session.Length > 0)
        {
            if (!_protector.Available)
                throw new AppValidationException("this instance can't store a portal session (operator must set APPLYTRACK_SECRETS_KEY)");
            sealed_ = _protector.Protect(session);
        }
        await _conn.ExecuteAsync(
            "UPDATE board_accounts SET session_ciphertext = @cipher, session_expires_at = @expires, updated_at = now() "
            + "WHERE tenant_id = @t AND host = @host",
            new { t = _t, host = BoardAccount.Normalize(host), cipher = sealed_, expires = session.Length > 0 ? expiresAt : null });
    }

    public async Task<bool> DeleteAsync(string host) =>
        await _conn.ExecuteAsync("DELETE FROM board_accounts WHERE tenant_id = @t AND host = @host",
            new { t = _t, host = BoardAccount.Normalize(host) }) > 0;

    private async Task<List<Row>> ReadRowsAsync() =>
        (await _conn.QueryAsync<Row>(
            "SELECT host, username, password_ciphertext AS passwordciphertext, updated_at AS updatedat, "
            + "session_ciphertext AS sessionciphertext, session_expires_at AS sessionexpiresat FROM board_accounts WHERE tenant_id = @t ORDER BY host",
            new { t = _t })).ToList();
}
