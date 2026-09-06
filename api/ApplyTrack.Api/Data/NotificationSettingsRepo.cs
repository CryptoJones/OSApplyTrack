// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Security.Cryptography;
using ApplyTrack.Api.Crypto;
using Dapper;
using Microsoft.Extensions.Logging;

namespace ApplyTrack.Api.Data;

/// <summary>The client-safe view: whether it's on, whether a token is stored (never the token), the chat id.</summary>
public sealed record TelegramSettingsView(bool Enabled, bool HasBotToken, string ChatId);

/// <summary>A deliverable target: the decrypted token plus the chat id.</summary>
public sealed record TelegramTarget(string BotToken, string ChatId);

/// <summary>
/// Tenant-scoped read/write of the single <c>notification_settings</c> row. The bot
/// token is encrypted at rest via <see cref="SecretProtector"/> and never returned
/// to the client — the same shape as <see cref="LlmSettingsRepo"/>.
/// </summary>
public sealed class NotificationSettingsRepo
{
    private readonly IDbConnection _conn;
    private readonly long _t;
    private readonly SecretProtector _protector;
    private readonly ILogger<NotificationSettingsRepo> _log;

    public NotificationSettingsRepo(
        IDbConnection conn, long tenantId, SecretProtector protector, ILogger<NotificationSettingsRepo> log)
    {
        _conn = conn;
        _t = tenantId;
        _protector = protector;
        _log = log;
    }

    private sealed record Row(bool TelegramEnabled, string TelegramBotTokenCiphertext, string TelegramChatId);

    public async Task<TelegramSettingsView> GetViewAsync()
    {
        var row = await ReadRowAsync();
        return row is null
            ? new TelegramSettingsView(false, false, "")
            : new TelegramSettingsView(row.TelegramEnabled, row.TelegramBotTokenCiphertext.Length > 0, row.TelegramChatId);
    }

    /// <summary>The target to send to, or null when off, unconfigured, or the token can't be decrypted.</summary>
    public async Task<TelegramTarget?> GetTargetAsync() => await ReadTargetAsync(requireEnabled: true);

    /// <summary>Like <see cref="GetTargetAsync"/> but ignores the on/off switch — for the test button.</summary>
    public async Task<TelegramTarget?> GetTargetForTestAsync() => await ReadTargetAsync(requireEnabled: false);

    private async Task<TelegramTarget?> ReadTargetAsync(bool requireEnabled)
    {
        var row = await ReadRowAsync();
        if (row is null || (requireEnabled && !row.TelegramEnabled))
            return null;
        if (row.TelegramBotTokenCiphertext.Length == 0 || row.TelegramChatId.Length == 0 || !_protector.Available)
            return null;
        try
        {
            return new TelegramTarget(_protector.Unprotect(row.TelegramBotTokenCiphertext), row.TelegramChatId);
        }
        catch (CryptographicException)
        {
            _log.LogWarning(
                "Stored Telegram bot token for tenant {TenantId} failed to decrypt "
                + "(APPLYTRACK_SECRETS_KEY changed/rotated?); notifications are off until it is re-entered.", _t);
            return null;
        }
    }

    /// <summary>
    /// Save. Null <paramref name="enabled"/> / <paramref name="chatId"/> leave the stored
    /// value alone. <paramref name="changeToken"/> distinguishes "leave the token alone"
    /// (false) from "set/clear it" (true; blank clears). Storing a token needs a master key.
    /// </summary>
    public async Task UpsertAsync(bool? enabled, string? chatId, bool changeToken, string? newTokenPlaintext)
    {
        var ciphertext = "";
        if (changeToken && !string.IsNullOrEmpty(newTokenPlaintext))
        {
            if (!_protector.Available)
                throw new AppValidationException(
                    "this instance can't store a Telegram bot token (operator must set APPLYTRACK_SECRETS_KEY)");
            ciphertext = _protector.Protect(newTokenPlaintext);
        }
        var tokenUpdate = changeToken
            ? "telegram_bot_token_ciphertext = EXCLUDED.telegram_bot_token_ciphertext," : "";
        await _conn.ExecuteAsync(
            $"""
             INSERT INTO notification_settings (
                 tenant_id, telegram_enabled, telegram_bot_token_ciphertext, telegram_chat_id, updated_at)
             VALUES (@t, coalesce(@enabled, false), @ciphertext, coalesce(@chatId, ''), now())
             ON CONFLICT (tenant_id) DO UPDATE SET
                 telegram_enabled = coalesce(@enabled, notification_settings.telegram_enabled),
                 {tokenUpdate}
                 telegram_chat_id = coalesce(@chatId, notification_settings.telegram_chat_id),
                 updated_at       = now()
             """,
            new { t = _t, enabled, ciphertext, chatId = chatId?.Trim() });
    }

    private Task<Row?> ReadRowAsync() =>
        _conn.QuerySingleOrDefaultAsync<Row?>(
            "SELECT telegram_enabled AS telegramenabled, "
            + "telegram_bot_token_ciphertext AS telegrambottokenciphertext, "
            + "telegram_chat_id AS telegramchatid "
            + "FROM notification_settings WHERE tenant_id = @t",
            new { t = _t });
}
