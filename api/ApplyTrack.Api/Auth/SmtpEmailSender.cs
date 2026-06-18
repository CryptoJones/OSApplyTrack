// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ApplyTrack.Api.Auth;

/// <summary>
/// Real <see cref="IEmailSender"/>: delivers the magic-link email over SMTP via
/// MailKit (the library Microsoft recommends over the obsolete
/// <c>System.Net.Mail.SmtpClient</c>). Wired up only when an SMTP host is configured;
/// otherwise <see cref="ConsoleEmailSender"/> stands in. See <see cref="EmailOptions"/>
/// for why this is plain SMTP rather than any one vendor's API.
///
/// Port choice drives the TLS handshake: 465 uses implicit TLS (connect-then-TLS),
/// anything else negotiates STARTTLS when the server offers it. A blank username
/// sends unauthenticated — only sane against a trusted local relay.
/// </summary>
public sealed class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendMagicLinkAsync(string email, string link)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(options.FromName, options.EffectiveFrom));
        msg.To.Add(MailboxAddress.Parse(email));
        msg.Subject = "Your OSApplyTrack sign-in link";
        msg.Body = new BodyBuilder
        {
            TextBody =
                $"Click to sign in to OSApplyTrack:\n\n{link}\n\n"
                + "This link is single-use and expires in 15 minutes. "
                + "If you didn't request it, you can ignore this email.",
        }.ToMessageBody();

        using var client = new SmtpClient { Timeout = options.TimeoutSeconds * 1000 };
        var tls = options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(options.Host, options.Port, tls);
        if (!string.IsNullOrWhiteSpace(options.Username))
            await client.AuthenticateAsync(options.Username, options.Password);
        await client.SendAsync(msg);
        await client.DisconnectAsync(quit: true);

        // Breadcrumb only — never log the link itself (it embeds a live login token).
        log.LogInformation("magic-link emailed to {Email} via {Host}:{Port}", email, options.Host, options.Port);
    }
}
