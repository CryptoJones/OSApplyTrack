// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Notifications;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The per-tenant notification channels for the ready-to-submit "moo": <c>GET/PUT
/// /api/notifications</c> with a write-only bot token (stored encrypted, never
/// echoed — only <c>has_bot_token</c>), the email switch and the per-event toggles
/// (#355), the daily digest's switches and hour (#336), the reminder's toggle and hour and
/// whether a calendar feed link exists (#354), and <c>POST /api/notifications/test</c> / <c>…/email/test</c> to send a test
/// message on either channel before switching it on.
/// </summary>
public static partial class NotificationsEndpoints
{
    [GeneratedRegex(@"^\d{1,20}:[A-Za-z0-9_-]{20,}$")]
    private static partial Regex BotToken();

    [GeneratedRegex(@"^(-?\d{1,20}|@[A-Za-z0-9_]{5,32})$")]
    private static partial Regex ChatId();

    public static void MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications", async (NotificationSettingsRepo repo, MailboxSettingsRepo mailbox, SecretProtector protector,
            EmailNotifier email, UserRepo users, TenantContext tenant, ApiTokenRepo tokens) =>
        {
            var feed = await tokens.GetCalendarAsync();
            var v = await repo.GetViewAsync();
            var m = await mailbox.GetViewAsync();
            var d = await repo.GetDigestAsync();
            var ev = v.Events ?? new NotificationEvents();
            return Results.Ok(new
            {
                telegram_enabled = v.Enabled,
                has_bot_token = v.HasBotToken,
                telegram_chat_id = v.ChatId,
                email_enabled = v.EmailEnabled,
                email_available = email.Available,
                email_address = (await users.GetAsync(tenant.TenantId))?.Email ?? "",
                notify_packet_ready = ev.PacketReady,
                notify_security_code = ev.SecurityCode,
                notify_submit_failed = ev.SubmitFailed,
                notify_followup_due = ev.FollowupDue,
                reminder_hour = await repo.GetReminderHourAsync(),
                digest_enabled = d.Enabled,
                digest_insults = d.Insults,
                digest_hour = d.Hour,
                calendar_feed_enabled = feed.Enabled,
                calendar_feed_created_at = feed.CreatedAt,
                calendar_feed_last_used_at = feed.LastUsedAt,
                secrets_available = protector.Available,
                mailbox_enabled = m.Enabled,
                mailbox_host = m.Host,
                mailbox_port = m.Port,
                mailbox_username = m.Username,
                has_mailbox_password = m.HasPassword,
                // Outgoing mail for follow-ups (#394): the same account's SMTP host.
                mailbox_smtp_host = m.SmtpHost,
                mailbox_smtp_port = m.SmtpPort,
                mailbox_can_send = m.CanSend,
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
                string? sHost = payload.TryGetProperty("mailbox_smtp_host", out var sh) && sh.ValueKind == JsonValueKind.String ? (sh.GetString() ?? "").Trim() : null;
                int? sPort = payload.TryGetProperty("mailbox_smtp_port", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.TryGetInt32(out var spn) ? spn : null;
                if (sHost is { Length: > 0 })
                {
                    InputLimits.Text("mailbox_smtp_host", sHost, 253);
                    if (!Regex.IsMatch(sHost, @"^[A-Za-z0-9.-]+$"))
                        throw new AppValidationException("that doesn't look like a mail server host name");
                }
                if (sPort is < 1 or > 65535)
                    throw new AppValidationException("the SMTP port is a number from 1 to 65535");
                if (mEnabled is not null || mHost is not null || mPort is not null || mUser is not null || changePassword
                    || sHost is not null || sPort is not null)
                    await mailbox.UpsertAsync(mEnabled, mHost, mPort, mUser, changePassword, password, sHost, sPort);
            }
            // The email switch and the per-event toggles (#355): each present boolean updates
            // its own column, an absent one is left alone.
            bool? Flag(string name) => payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? el.GetBoolean() : null;
            var (emailOn, ready, code, failed) = (Flag("email_enabled"), Flag("notify_packet_ready"),
                Flag("notify_security_code"), Flag("notify_submit_failed"));
            // The daily follow-up and interview reminder (#354): its toggle and UTC hour.
            var due = Flag("notify_followup_due");
            int? reminderHour = null;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("reminder_hour", out var rh))
                reminderHour = rh.ValueKind == JsonValueKind.Number && rh.TryGetInt32(out var h) && h is >= 0 and <= 23
                    ? h : throw new AppValidationException("the reminder hour is a number from 0 to 23 (UTC)");
            var enabled = Flag("telegram_enabled");
            // The daily digest (#336): its switch, "Insult me", and the UTC hour it goes out at.
            var (digestOn, insults) = (Flag("digest_enabled"), Flag("digest_insults"));
            int? digestHour = null;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("digest_hour", out var dh))
                digestHour = dh.ValueKind == JsonValueKind.Number && dh.TryGetInt32(out var dhv) && dhv is >= 0 and <= 23
                    ? dhv : throw new AppValidationException("the digest hour is a number from 0 to 23 (UTC)");

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

            // Saved only once every Telegram field has passed, so a 400 changes nothing here.
            if (emailOn is not null || ready is not null || code is not null || failed is not null || due is not null)
                await repo.UpsertPreferencesAsync(emailOn, ready, code, failed, due);
            if (reminderHour is { } hour)
                await repo.UpsertReminderHourAsync(hour);
            if (enabled is not null || chatId is not null || changeToken)
                await repo.UpsertAsync(enabled, chatId, changeToken, token);
            if (digestOn is not null || insults is not null || digestHour is not null)
                await repo.UpsertDigestAsync(digestOn, insults, digestHour);
            var v = await repo.GetViewAsync();
            var d = await repo.GetDigestAsync();
            var ev = v.Events ?? new NotificationEvents();
            return Results.Ok(new
            {
                telegram_enabled = v.Enabled,
                has_bot_token = v.HasBotToken,
                telegram_chat_id = v.ChatId,
                email_enabled = v.EmailEnabled,
                notify_packet_ready = ev.PacketReady,
                notify_security_code = ev.SecurityCode,
                notify_submit_failed = ev.SubmitFailed,
                notify_followup_due = ev.FollowupDue,
                reminder_hour = await repo.GetReminderHourAsync(),
                digest_enabled = d.Enabled,
                digest_insults = d.Insults,
                digest_hour = d.Hour,
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

        // A test mail to the account's own address, whether or not email is switched on yet.
        app.MapPost("/api/notifications/email/test", async (
            EmailNotifier email, UserRepo users, TenantContext tenant, CancellationToken ct) =>
        {
            if (!email.Available)
                throw new AppValidationException("this instance has no SMTP server configured — the operator sets Email__Host");
            var to = (await users.GetAsync(tenant.TenantId))?.Email ?? "";
            await email.SendAsync(to, "Test message from OSApplyTrack",
                PacketReadyNotifier.EmailBody("🐮 moo — test message from ApplyTrack"), null, ct);
            return Results.Ok(new { ok = true, to });
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
