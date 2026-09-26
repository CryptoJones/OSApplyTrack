// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Notifications;

/// <summary>
/// Email as a notification channel (#355): the instance's own SMTP sender — the one the
/// sign-in mail already needs (<c>Email__*</c>) — to the account's own address, so a
/// self-hoster who will never set up a Telegram bot still hears from the agent. Not an
/// <see cref="INotifier"/>: that is Telegram's transport (a bot token, a chat id, and a
/// reply to read back), and mail has none of the three. Anything that mails a tenant —
/// the moo here, the daily digest (#336) — goes through <see cref="SendAsync"/>, so the
/// failure shape and the "no SMTP on this container" answer are the same everywhere.
/// </summary>
public sealed class EmailNotifier(IEmailSender sender, EmailOptions options, ILogger<EmailNotifier> log)
{
    private const int MaxReasonChars = 200;

    /// <summary>True when this process has an SMTP host to relay through. Without one the
    /// console sender stands in, which delivers nothing — so the channel is off, not "sent".</summary>
    public bool Available => options.IsConfigured;

    /// <summary>
    /// Mail <paramref name="to"/>. Throws <see cref="NotificationFailedException"/> when
    /// there is no SMTP host here or the relay refuses — the message names the failure,
    /// never the SMTP password.
    /// </summary>
    public async Task SendAsync(string to, string subject, string text, string? html = null, CancellationToken ct = default)
    {
        if (!Available)
            throw new NotificationFailedException("no SMTP server is configured on this instance (Email__Host)");
        if (string.IsNullOrWhiteSpace(to))
            throw new NotificationFailedException("the account has no email address");
        try
        {
            await sender.SendAsync(new OutgoingEmail(to.Trim(), subject, text, html), ct);
        }
        catch (Exception ex) when (ex is not NotificationFailedException
            && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            var reason = ex.Message.Split('\n')[0].Trim();
            if (reason.Length > MaxReasonChars) reason = reason[..MaxReasonChars];
            log.LogWarning("email to {Email} failed: {Type}: {Reason}", to, ex.GetType().Name, reason);
            throw new NotificationFailedException($"the mail server refused the message ({ex.GetType().Name}: {reason})");
        }
    }
}
