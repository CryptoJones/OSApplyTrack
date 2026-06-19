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
                + "If you didn't request it, you can ignore this email.\n\n"
                + "Open source: https://github.com/CryptoJones/OSApplyTrack",
            // A LIGHT layout with cyberdeck branding (the cyan applytrack wordmark on a
            // near-black header band). This is deliberate, not a fallback: a forced-dark
            // background does not survive — iOS/Apple Mail and Gmail auto-INVERT a
            // dark-designed email to the device theme, turning the canvas white. A light
            // design renders identically everywhere (verified by test sends to both
            // clients). Table layout + bgcolor attributes, solid hex, inline styles only,
            // no remote images; mono via system fonts since email can't load the vendored.
            HtmlBody =
                $$"""
                <!DOCTYPE html>
                <html>
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="color-scheme" content="light dark">
                </head>
                <body style="margin:0;padding:0;background-color:#eef2f5;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="#eef2f5" style="background-color:#eef2f5;width:100%;margin:0;">
                  <tr>
                    <td align="center" style="padding:28px 16px;font-family:Menlo,Consolas,'DejaVu Sans Mono',monospace;">
                      <table role="presentation" width="460" cellpadding="0" cellspacing="0" border="0" style="width:100%;max-width:460px;text-align:left;border-radius:12px;overflow:hidden;border:1px solid #d4dde2;">
                        <tr><td bgcolor="#07090f" style="background-color:#07090f;padding:18px 22px;font-size:22px;font-weight:800;letter-spacing:-0.01em;">
                          <span style="color:#27d4ff;">apply</span><span style="color:#eef2f5;">track</span>
                        </td></tr>
                        <tr><td bgcolor="#ffffff" style="background-color:#ffffff;padding:26px 22px;">
                          <div style="font-size:18px;font-weight:700;color:#0c121c;padding:0 0 10px;">Sign in to OSApplyTrack</div>
                          <div style="font-size:14px;line-height:1.55;color:#5a6678;padding:0 0 22px;">Click the button below to sign in. This link is single-use and expires in 15 minutes.</div>
                          <a href="{{href}}" style="display:inline-block;background-color:#27d4ff;color:#07090f;text-decoration:none;padding:12px 28px;border-radius:7px;font-size:14px;font-weight:700;">Sign in &#8250;</a>
                          <div style="font-size:12px;line-height:1.5;color:#8a96a3;padding:22px 0 6px;">Or paste this link into your browser:</div>
                          <div style="font-size:12px;line-height:1.5;word-break:break-all;"><a href="{{href}}" style="color:#0e8f78;">{{href}}</a></div>
                        </td></tr>
                      </table>
                      <div style="max-width:460px;font-size:11px;line-height:1.5;color:#9aa6b2;padding:16px 4px 0;font-family:Menlo,Consolas,monospace;">If you didn't request this, you can safely ignore this email.</div>
                      <div style="max-width:460px;font-size:11px;line-height:1.5;color:#9aa6b2;padding:6px 4px 0;font-family:Menlo,Consolas,monospace;"><a href="https://github.com/CryptoJones/OSApplyTrack" style="color:#0e8f78;text-decoration:none;">github.com/CryptoJones/OSApplyTrack</a></div>
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
