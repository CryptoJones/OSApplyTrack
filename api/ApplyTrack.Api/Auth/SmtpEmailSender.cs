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
            // Cyberdeck theme (CryptoJones/cyberdeck-theme), matching the mobile site.
            // A full HTML document with color-scheme:dark + supported-color-schemes:dark
            // is REQUIRED: without it, iOS/Apple Mail (and Gmail) normalize the email to
            // the device's LIGHT appearance — forcing a white background and darkening
            // the light text (which is what made earlier versions render white). The dark
            // canvas sits on a full-width <table> with a bgcolor attribute (clients strip
            // <body> backgrounds); solid hex borders (no rgba); inline styles only; no
            // remote images; mono via system fonts since email can't load the vendored one.
            HtmlBody =
                $$"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="color-scheme" content="dark">
                <meta name="supported-color-schemes" content="dark">
                </head>
                <body style="margin:0;padding:0;background-color:#07090f;color-scheme:dark;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="#07090f" style="background-color:#07090f;width:100%;margin:0;">
                  <tr>
                    <td align="center" bgcolor="#07090f" style="background-color:#07090f;padding:32px 16px;font-family:Menlo,Consolas,'DejaVu Sans Mono',monospace;">
                      <table role="presentation" width="460" cellpadding="0" cellspacing="0" border="0" style="width:100%;max-width:460px;text-align:left;">
                        <tr><td style="padding:0 0 22px;font-size:26px;font-weight:800;letter-spacing:-0.01em;color:#cfd8e3;">
                          <span style="color:#27d4ff;">apply</span>track
                        </td></tr>
                        <tr><td bgcolor="#0c121c" style="background-color:#0c121c;border:1px solid #1c5566;border-radius:10px;padding:26px;">
                          <div style="font-size:18px;font-weight:700;color:#cfd8e3;padding:0 0 12px;">Sign in to OSApplyTrack</div>
                          <div style="font-size:14px;line-height:1.55;color:#9fb0b0;padding:0 0 22px;">Click the button below to sign in. This link is single-use and expires in 15 minutes.</div>
                          <a href="{{href}}" style="display:inline-block;background-color:#27d4ff;color:#07090f;text-decoration:none;padding:12px 28px;border-radius:7px;font-size:14px;font-weight:700;">Sign in &#8250;</a>
                          <div style="font-size:12px;line-height:1.5;color:#6b7886;padding:22px 0 6px;">Or paste this link into your browser:</div>
                          <div style="font-size:12px;line-height:1.5;word-break:break-all;"><a href="{{href}}" style="color:#55ff99;">{{href}}</a></div>
                        </td></tr>
                        <tr><td style="padding:18px 2px 0;font-size:11px;line-height:1.5;color:#5a6678;">If you didn't request this, you can safely ignore this email.</td></tr>
                      </table>
                    </td>
                  </tr>
                </table>
                </body>
                </html>
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
