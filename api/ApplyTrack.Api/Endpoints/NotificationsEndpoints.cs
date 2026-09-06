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
        app.MapGet("/api/notifications", async (NotificationSettingsRepo repo, SecretProtector protector) =>
        {
            var v = await repo.GetViewAsync();
            return Results.Ok(new
            {
                telegram_enabled = v.Enabled,
                has_bot_token = v.HasBotToken,
                telegram_chat_id = v.ChatId,
                secrets_available = protector.Available,
            });
        });

        app.MapPut("/api/notifications", async (JsonElement payload, NotificationSettingsRepo repo) =>
        {
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
