// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Notifications;

/// <summary>One message a person sent the tenant's bot: which chat, when, and the text.</summary>
public sealed record TelegramReply(long UpdateId, string ChatId, DateTimeOffset SentAt, string Text);

/// <summary>
/// Delivers one plain-text message to a tenant's Telegram chat, and reads what came
/// back. The one small abstraction over the transport, so tests can capture instead
/// of send.
/// </summary>
public interface INotifier
{
    /// <summary>Send. Throws <see cref="Data.NotificationFailedException"/> on any failure;
    /// the message never carries the bot token.</summary>
    Task SendAsync(string botToken, string chatId, string text, CancellationToken ct = default);

    /// <summary>
    /// The messages people have sent the bot since <paramref name="offset"/> (a Telegram
    /// update id; null for "whatever is pending"), oldest first. Passing an offset confirms
    /// everything before it, so the caller keeps the last update id + 1. Throws
    /// <see cref="Data.NotificationFailedException"/> on any failure.
    /// </summary>
    Task<IReadOnlyList<TelegramReply>> ReadRepliesAsync(string botToken, long? offset, CancellationToken ct = default);
}
