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
        var href = System.Net.WebUtility.HtmlEncode(link);
        msg.Body = new BodyBuilder
        {
            // Plain-text alternative — always present so the message is multipart
            // (text+HTML), which filters trust more than an HTML-only body.
            TextBody =
                $"Sign in to OSApplyTrack:\n\n{link}\n\n"
                + "This link is single-use and expires in 15 minutes. "
                + "If you didn't request it, you can ignore this email.",
            // Styled to match the app's default "midnight" theme (see wwwroot/app.css)
            // so the email looks like part of OSApplyTrack. Inline styles only (clients
            // strip <style>/external CSS) and no remote images (blocked by default and
            // they hurt deliverability).
            HtmlBody =
                $$"""
                <body style="margin:0;padding:0;background:#0c0e13;">
                  <div style="max-width:460px;margin:0 auto;padding:32px 24px;font-family:'Bricolage Grotesque',system-ui,-apple-system,Segoe UI,Roboto,sans-serif;">
                    <div style="font-size:28px;font-weight:800;letter-spacing:-0.01em;margin:0 0 24px;">
                      <span style="color:#7d9bff;">apply</span><span style="color:#e7eaf0;">track</span>
                    </div>
                    <div style="background:#161a22;border:1px solid #232834;border-radius:10px;padding:28px;">
                      <h1 style="margin:0 0 12px;font-size:19px;font-weight:700;color:#e7eaf0;">Sign in to OSApplyTrack</h1>
                      <p style="margin:0 0 24px;font-size:15px;line-height:1.55;color:#9aa3b2;">Click the button below to sign in. This link is single-use and expires in 15 minutes.</p>
                      <a href="{{href}}" style="display:inline-block;background:#7d9bff;color:#0a0c10;text-decoration:none;padding:12px 26px;border-radius:7px;font-size:15px;font-weight:700;">Sign in</a>
                      <p style="margin:24px 0 6px;font-size:13px;line-height:1.5;color:#626c7d;">Or paste this link into your browser:</p>
                      <p style="margin:0;font-size:13px;line-height:1.5;word-break:break-all;"><a href="{{href}}" style="color:#5fd0bf;">{{href}}</a></p>
                    </div>
                    <p style="margin:20px 4px 0;font-size:12px;line-height:1.5;color:#626c7d;">If you didn't request this, you can safely ignore this email.</p>
                  </div>
                </body>
                """,
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
