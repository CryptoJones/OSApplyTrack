// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Security.Cryptography;
using ApplyTrack.Api.Crypto;
using Dapper;
using Microsoft.Extensions.Logging;

namespace ApplyTrack.Api.Data;

/// <summary>The client-safe view: whether Telegram is on, whether a token is stored (never
/// the token), the chat id; whether email is on; and which events notify at all.</summary>
public sealed record TelegramSettingsView(bool Enabled, bool HasBotToken, string ChatId,
    bool EmailEnabled = false, NotificationEvents? Events = null);

/// <summary>The per-event toggles (#355). They gate every channel; all default on.
/// <c>FollowupDue</c> is the daily follow-up and interview reminder (#354).</summary>
public sealed record NotificationEvents(bool PacketReady = true, bool SecurityCode = true, bool SubmitFailed = true,
    bool FollowupDue = true);

/// <summary>Where a notification goes right now: the Telegram target when that channel is on
/// and usable, the account's address when email is on, and which events are wanted.</summary>
public sealed record NotificationChannels(TelegramTarget? Telegram, string? Email, NotificationEvents Events);

/// <summary>The daily applied-jobs digest (#336): on or off, whether it opens with an
/// insult, and the UTC hour it goes out at. All off by default; noon UTC.</summary>
public sealed record DigestSettings(bool Enabled = false, bool Insults = false, int Hour = DigestSettings.DefaultHour)
{
    public const int DefaultHour = 12;
}

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

    private sealed record Row(bool TelegramEnabled, string TelegramBotTokenCiphertext, string TelegramChatId,
        bool EmailEnabled, bool NotifyPacketReady, bool NotifySecurityCode, bool NotifySubmitFailed, bool NotifyFollowupDue)
    {
        public NotificationEvents Events => new(NotifyPacketReady, NotifySecurityCode, NotifySubmitFailed, NotifyFollowupDue);
    }

    public async Task<TelegramSettingsView> GetViewAsync()
    {
        var row = await ReadRowAsync();
        return row is null
            ? new TelegramSettingsView(false, false, "", false, new NotificationEvents())
            : new TelegramSettingsView(row.TelegramEnabled, row.TelegramBotTokenCiphertext.Length > 0, row.TelegramChatId,
                row.EmailEnabled, row.Events);
    }

    /// <summary>
    /// Every channel a notification should fan out to, in one read: the Telegram target
    /// (as <see cref="GetTargetAsync"/>) and, when email is on, the account's own address —
    /// the one it signs in with, read here so it can never be pointed at anyone else.
    /// </summary>
    public async Task<NotificationChannels> GetChannelsAsync()
    {
        var row = await ReadRowAsync();
        if (row is null)
            return new NotificationChannels(null, null, new NotificationEvents());
        var telegram = row.TelegramEnabled ? Decrypt(row) : null;
        string? email = null;
        if (row.EmailEnabled)
        {
            email = await _conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT email::text FROM users WHERE id = @t", new { t = _t });
            if (string.IsNullOrWhiteSpace(email)) email = null;
        }
        return new NotificationChannels(telegram, email, row.Events);
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
        return Decrypt(row);
    }

    private TelegramTarget? Decrypt(Row row)
    {
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

    /// <summary>
    /// Save the email switch and the per-event toggles. Null leaves a value alone. Email
    /// needs no address or secret stored: it goes to the account's own address.
    /// </summary>
    public async Task UpsertPreferencesAsync(
        bool? emailEnabled, bool? packetReady = null, bool? securityCode = null, bool? submitFailed = null,
        bool? followupDue = null, IDbTransaction? tx = null) =>
        await _conn.ExecuteAsync(
            """
            INSERT INTO notification_settings (
                tenant_id, email_enabled, notify_packet_ready, notify_security_code, notify_submit_failed,
                notify_followup_due, updated_at)
            VALUES (@t, coalesce(@emailEnabled, false), coalesce(@ready, true), coalesce(@code, true), coalesce(@failed, true),
                coalesce(@due, true), now())
            ON CONFLICT (tenant_id) DO UPDATE SET
                email_enabled        = coalesce(@emailEnabled, notification_settings.email_enabled),
                notify_packet_ready  = coalesce(@ready, notification_settings.notify_packet_ready),
                notify_security_code = coalesce(@code, notification_settings.notify_security_code),
                notify_submit_failed = coalesce(@failed, notification_settings.notify_submit_failed),
                notify_followup_due  = coalesce(@due, notification_settings.notify_followup_due),
                updated_at           = now()
            """,
            new { t = _t, emailEnabled, ready = packetReady, code = securityCode, failed = submitFailed, due = followupDue }, tx);

    /// <summary>The UTC hour the daily reminder (#354) goes out at; noon by default.</summary>
    public async Task<int> GetReminderHourAsync() =>
        await _conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT reminder_hour::int FROM notification_settings WHERE tenant_id = @t", new { t = _t })
        ?? DigestSettings.DefaultHour;

    /// <summary>Save the reminder's UTC hour (0–23).</summary>
    public async Task UpsertReminderHourAsync(int hour, IDbTransaction? tx = null)
    {
        if (hour is < 0 or > 23)
            throw new AppValidationException("the reminder hour is a number from 0 to 23 (UTC)");
        await _conn.ExecuteAsync(
            """
            INSERT INTO notification_settings (tenant_id, reminder_hour, updated_at) VALUES (@t, @hour, now())
            ON CONFLICT (tenant_id) DO UPDATE SET reminder_hour = @hour, updated_at = now()
            """,
            new { t = _t, hour = (short)hour }, tx);
    }

    public async Task<DigestSettings> GetDigestAsync() =>
        await _conn.QuerySingleOrDefaultAsync<DigestSettings?>(
            "SELECT digest_enabled AS enabled, digest_insults AS insults, digest_hour::int AS hour "
            + "FROM notification_settings WHERE tenant_id = @t",
            new { t = _t }) ?? new DigestSettings();

    /// <summary>Save the digest's switches and hour (0–23, UTC). Null leaves a value alone.</summary>
    public async Task UpsertDigestAsync(bool? enabled, bool? insults, int? hour, IDbTransaction? tx = null)
    {
        if (hour is < 0 or > 23)
            throw new AppValidationException("the digest hour is a number from 0 to 23 (UTC)");
        await _conn.ExecuteAsync(
            """
            INSERT INTO notification_settings (tenant_id, digest_enabled, digest_insults, digest_hour, updated_at)
            VALUES (@t, coalesce(@enabled, false), coalesce(@insults, false), coalesce(@hour, @defaultHour), now())
            ON CONFLICT (tenant_id) DO UPDATE SET
                digest_enabled = coalesce(@enabled, notification_settings.digest_enabled),
                digest_insults = coalesce(@insults, notification_settings.digest_insults),
                digest_hour    = coalesce(@hour, notification_settings.digest_hour),
                updated_at     = now()
            """,
            new { t = _t, enabled, insults, hour = (short?)hour, defaultHour = (short)DigestSettings.DefaultHour }, tx);
    }

    private Task<Row?> ReadRowAsync() =>
        _conn.QuerySingleOrDefaultAsync<Row?>(
            "SELECT telegram_enabled AS telegramenabled, "
            + "telegram_bot_token_ciphertext AS telegrambottokenciphertext, "
            + "telegram_chat_id AS telegramchatid, "
            + "email_enabled AS emailenabled, notify_packet_ready AS notifypacketready, "
            + "notify_security_code AS notifysecuritycode, notify_submit_failed AS notifysubmitfailed, "
            + "notify_followup_due AS notifyfollowupdue "
            + "FROM notification_settings WHERE tenant_id = @t",
            new { t = _t });
}
