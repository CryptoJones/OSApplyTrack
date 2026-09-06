// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Notifications;

/// <summary>
/// Delivers one plain-text message to a tenant's Telegram chat. The one small
/// abstraction over the transport, so tests can capture instead of send.
/// </summary>
public interface INotifier
{
    /// <summary>Send. Throws <see cref="Data.NotificationFailedException"/> on any failure;
    /// the message never carries the bot token.</summary>
    Task SendAsync(string botToken, string chatId, string text, CancellationToken ct = default);
}
