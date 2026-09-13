// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Security.Cryptography;
using ApplyTrack.Api.Crypto;
using Dapper;
using Microsoft.Extensions.Logging;

namespace ApplyTrack.Api.Data;

/// <summary>The client-safe view: never the password, only whether one is stored.</summary>
public sealed record MailboxSettingsView(bool Enabled, string Host, int Port, string Username, bool HasPassword);

/// <summary>A mailbox to read: the decrypted password with the connection details.</summary>
public sealed record MailboxTarget(string Host, int Port, string Username, string Password);

/// <summary>
/// Tenant-scoped read/write of the single <c>mailbox_settings</c> row — the IMAP
/// mailbox a parked run reads the board's emailed security code from. Same shape as
/// <see cref="NotificationSettingsRepo"/>: the password is sealed at rest and write-only.
/// </summary>
public sealed class MailboxSettingsRepo
{
    private readonly IDbConnection _conn;
    private readonly long _t;
    private readonly SecretProtector _protector;
    private readonly ILogger<MailboxSettingsRepo> _log;

    public MailboxSettingsRepo(IDbConnection conn, long tenantId, SecretProtector protector, ILogger<MailboxSettingsRepo> log)
    {
        _conn = conn;
        _t = tenantId;
        _protector = protector;
        _log = log;
    }

    private sealed record Row(bool Enabled, string Host, int Port, string Username, string PasswordCiphertext);

    public async Task<MailboxSettingsView> GetViewAsync()
    {
        var row = await ReadRowAsync();
        return row is null
            ? new MailboxSettingsView(false, "", 993, "", false)
            : new MailboxSettingsView(row.Enabled, row.Host, row.Port, row.Username, row.PasswordCiphertext.Length > 0);
    }

    /// <summary>The mailbox to read, or null when off, incomplete, or the password can't be decrypted.</summary>
    public Task<MailboxTarget?> GetTargetAsync() => ReadTargetAsync(requireEnabled: true);

    /// <summary>Like <see cref="GetTargetAsync"/> but ignores the on/off switch — for the test button.</summary>
    public Task<MailboxTarget?> GetTargetForTestAsync() => ReadTargetAsync(requireEnabled: false);

    private async Task<MailboxTarget?> ReadTargetAsync(bool requireEnabled)
    {
        var row = await ReadRowAsync();
        if (row is null || (requireEnabled && !row.Enabled))
            return null;
        if (row.Host.Length == 0 || row.Username.Length == 0 || row.PasswordCiphertext.Length == 0 || !_protector.Available)
            return null;
        try
        {
            return new MailboxTarget(row.Host, row.Port, row.Username, _protector.Unprotect(row.PasswordCiphertext));
        }
        catch (CryptographicException)
        {
            _log.LogWarning("Stored mailbox password for tenant {TenantId} failed to decrypt (key rotated?); the mailbox is off until it is re-entered.", _t);
            return null;
        }
    }

    /// <summary>Save. Null fields leave the stored value alone; <paramref name="changePassword"/>
    /// distinguishes "leave it" (false) from "set/clear it" (true; blank clears).</summary>
    public async Task UpsertAsync(bool? enabled, string? host, int? port, string? username, bool changePassword, string? newPassword)
    {
        var ciphertext = "";
        if (changePassword && !string.IsNullOrEmpty(newPassword))
        {
            if (!_protector.Available)
                throw new AppValidationException("this instance can't store a mailbox password (operator must set APPLYTRACK_SECRETS_KEY)");
            ciphertext = _protector.Protect(newPassword);
        }
        var passwordUpdate = changePassword ? "password_ciphertext = EXCLUDED.password_ciphertext," : "";
        await _conn.ExecuteAsync(
            $"""
             INSERT INTO mailbox_settings (tenant_id, enabled, host, port, username, password_ciphertext, updated_at)
             VALUES (@t, coalesce(@enabled, false), coalesce(@host, ''), coalesce(@port, 993), coalesce(@username, ''), @ciphertext, now())
             ON CONFLICT (tenant_id) DO UPDATE SET
                 enabled  = coalesce(@enabled, mailbox_settings.enabled),
                 host     = coalesce(@host, mailbox_settings.host),
                 port     = coalesce(@port, mailbox_settings.port),
                 username = coalesce(@username, mailbox_settings.username),
                 {passwordUpdate}
                 updated_at = now()
             """,
            new { t = _t, enabled, host = host?.Trim(), port, username = username?.Trim(), ciphertext });
    }

    private Task<Row?> ReadRowAsync() =>
        _conn.QuerySingleOrDefaultAsync<Row?>(
            "SELECT enabled, host, port, username, password_ciphertext AS passwordciphertext FROM mailbox_settings WHERE tenant_id = @t",
            new { t = _t });
}
