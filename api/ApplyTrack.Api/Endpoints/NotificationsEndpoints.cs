// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Notifications;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The per-tenant Telegram target for the ready-to-submit "moo": <c>GET/PUT
/// /api/notifications</c> with a write-only bot token (stored encrypted, never
/// echoed — only <c>has_bot_token</c>), and <c>POST /api/notifications/test</c> to send
/// a test message before switching it on.
/// </summary>
public static partial class NotificationsEndpoints
{
    [GeneratedRegex(@"^\d{1,20}:[A-Za-z0-9_-]{20,}$")]
    private static partial Regex BotToken();

    [GeneratedRegex(@"^(-?\d{1,20}|@[A-Za-z0-9_]{5,32})$")]
    private static partial Regex ChatId();

    public static void MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications", async (NotificationSettingsRepo repo, MailboxSettingsRepo mailbox, SecretProtector protector) =>
        {
            var v = await repo.GetViewAsync();
            var m = await mailbox.GetViewAsync();
            return Results.Ok(new
            {
                telegram_enabled = v.Enabled,
                has_bot_token = v.HasBotToken,
                telegram_chat_id = v.ChatId,
                secrets_available = protector.Available,
                mailbox_enabled = m.Enabled,
                mailbox_host = m.Host,
                mailbox_port = m.Port,
                mailbox_username = m.Username,
                has_mailbox_password = m.HasPassword,
            });
        });

        app.MapPut("/api/notifications", async (JsonElement payload, NotificationSettingsRepo repo, MailboxSettingsRepo mailbox) =>
        {
            // The mailbox half: any of its fields present updates that field; the password
            // is omitted-to-keep, blank-to-clear, like the bot token.
            if (payload.ValueKind == JsonValueKind.Object)
            {
                bool? mEnabled = payload.TryGetProperty("mailbox_enabled", out var me) && me.ValueKind is JsonValueKind.True or JsonValueKind.False ? me.GetBoolean() : null;
                string? mHost = payload.TryGetProperty("mailbox_host", out var mh) && mh.ValueKind == JsonValueKind.String ? (mh.GetString() ?? "").Trim() : null;
                int? mPort = payload.TryGetProperty("mailbox_port", out var mp) && mp.ValueKind == JsonValueKind.Number && mp.TryGetInt32(out var pn) ? pn : null;
                string? mUser = payload.TryGetProperty("mailbox_username", out var mu) && mu.ValueKind == JsonValueKind.String ? (mu.GetString() ?? "").Trim() : null;
                JsonElement pw = default;
                var changePassword = payload.TryGetProperty("mailbox_password", out pw);
                var password = changePassword ? (pw.ValueKind == JsonValueKind.String ? pw.GetString() ?? "" : "").Trim() : null;
                if (mHost is { Length: > 0 })
                {
                    InputLimits.Text("mailbox_host", mHost, 253);
                    if (!Regex.IsMatch(mHost, @"^[A-Za-z0-9.-]+$"))
                        throw new AppValidationException("that doesn't look like a mail server host name");
                }
                if (mPort is < 1 or > 65535)
                    throw new AppValidationException("the mailbox port is a number from 1 to 65535");
                if (mUser is { Length: > 0 }) InputLimits.Text("mailbox_username", mUser, 320);
                if (password is { Length: > 0 }) InputLimits.Text("mailbox_password", password, 512);
                if (mEnabled is not null || mHost is not null || mPort is not null || mUser is not null || changePassword)
                    await mailbox.UpsertAsync(mEnabled, mHost, mPort, mUser, changePassword, password);
            }
            bool? enabled = payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("telegram_enabled", out var en)
                && en.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? en.GetBoolean()
                : null;

            string? chatId = payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("telegram_chat_id", out var c) && c.ValueKind == JsonValueKind.String
                ? (c.GetString() ?? "").Trim()
                : null;
            if (chatId is { Length: > 0 })
            {
                InputLimits.Text("telegram_chat_id", chatId, InputLimits.TelegramChatId);
                if (!ChatId().IsMatch(chatId))
                    throw new AppValidationException(
                        "that doesn't look like a Telegram chat id (a number, or @channel)");
            }

            // Omitted = leave the stored token alone; present = set it, or clear when blank.
            JsonElement tok = default;
            var changeToken = payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("telegram_bot_token", out tok);
            var token = changeToken ? (tok.ValueKind == JsonValueKind.String ? tok.GetString() ?? "" : "").Trim() : null;
            if (token is { Length: > 0 })
            {
                InputLimits.Text("telegram_bot_token", token, InputLimits.TelegramBotToken);
                if (!BotToken().IsMatch(token))
                    throw new AppValidationException("that doesn't look like a Telegram bot token");
            }

            await repo.UpsertAsync(enabled, chatId, changeToken, token);
            var v = await repo.GetViewAsync();
            return Results.Ok(new
            {
                telegram_enabled = v.Enabled,
                has_bot_token = v.HasBotToken,
                telegram_chat_id = v.ChatId,
            });
        });

        // Prove the mailbox opens: sign in and count the inbox. Nothing is read.
        app.MapPost("/api/notifications/mailbox/test", async (
            MailboxSettingsRepo mailbox, ISecurityCodeSource codes, CancellationToken ct) =>
        {
            var target = await mailbox.GetTargetForTestAsync()
                ?? throw new AppValidationException("save the mailbox host, username and password first");
            try
            {
                var count = await codes.TestAsync(target, ct);
                return Results.Ok(new { ok = true, messages = count });
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not AppValidationException)
            {
                throw new AppValidationException("the mailbox did not open: " + ex.Message.Split('\n')[0]);
            }
        }).RequireRateLimiting("notify");

        app.MapPost("/api/notifications/test", async (
            NotificationSettingsRepo repo, INotifier notifier, CancellationToken ct) =>
        {
            var target = await repo.GetTargetForTestAsync()
                ?? throw new AppValidationException("save a bot token and chat id first");
            await notifier.SendAsync(target.BotToken, target.ChatId,
                "🐮 moo — test message from ApplyTrack", ct);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("notify");
    }
}
