// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace ApplyTrack.Api.Notifications;

/// <summary>One message of the mail an application's contact and the candidate have
/// exchanged, as the follow-up drafter reads it: untrusted text, cut short.</summary>
public sealed record MailSnippet(DateTimeOffset Date, string From, string Subject, string Text);

/// <summary>
/// The candidate's own mailbox as the follow-up engine uses it (#394): the thread with a
/// contact, read over IMAP, and the follow-up itself, sent over SMTP as the candidate.
/// Never the instance's relay — a follow-up is the person's mail to an employer, and the
/// operator's sender must not become a way for any tenant to mail anyone.
/// </summary>
public interface IFollowUpMail
{
    /// <summary>The latest messages from or to <paramref name="address"/>, newest last.
    /// Best-effort: the caller drafts without them when this throws.</summary>
    Task<IReadOnlyList<MailSnippet>> ThreadAsync(MailboxTarget imap, string address, CancellationToken ct);

    /// <summary>Send one plain-text message from the mailbox's own address.</summary>
    Task SendAsync(MailboxTarget smtp, string to, string subject, string body, CancellationToken ct);
}

public sealed class MailKitFollowUpMail : IFollowUpMail
{
    private const int MaxMessages = 5;
    private const int MaxChars = 1500;

    public async Task<IReadOnlyList<MailSnippet>> ThreadAsync(MailboxTarget imap, string address, CancellationToken ct)
    {
        using var client = await ImapSecurityCodeSource.OpenAsync(imap, ct);
        var found = new List<MailSnippet>();
        await ReadAsync(client.Inbox, SearchQuery.FromContains(address), found, ct);
        // What the candidate sent lives in Sent, where the provider names one.
        try
        {
            if (client.Capabilities.HasFlag(MailKit.Net.Imap.ImapCapabilities.SpecialUse)
                && client.GetFolder(SpecialFolder.Sent) is { } sent)
            {
                await sent.OpenAsync(FolderAccess.ReadOnly, ct);
                await ReadAsync(sent, SearchQuery.ToContains(address), found, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No Sent folder to read is not a reason to lose the inbox half.
        }
        await client.DisconnectAsync(true, ct);
        return found.OrderBy(m => m.Date).TakeLast(MaxMessages).ToList();
    }

    private static async Task ReadAsync(IMailFolder folder, SearchQuery query, List<MailSnippet> into, CancellationToken ct)
    {
        var uids = await folder.SearchAsync(query, ct);
        foreach (var uid in uids.Reverse().Take(MaxMessages))
        {
            var msg = await folder.GetMessageAsync(uid, ct);
            var text = (msg.TextBody ?? ImapSecurityCodeSource.StripHtml(msg.HtmlBody ?? "")).Trim();
            if (text.Length > MaxChars) text = text[..MaxChars].TrimEnd() + " […]";
            into.Add(new MailSnippet(msg.Date, msg.From.ToString(), msg.Subject ?? "", text));
        }
    }

    public async Task SendAsync(MailboxTarget smtp, string to, string subject, string body, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(smtp.Username));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };

        // The host is the tenant's: resolve it once, refuse private addresses, and dial the
        // pinned address — the same no-rebinding rule the IMAP side follows (#231).
        var socket = await ImapSecurityCodeSource.DialPublicAsync(smtp.Host, smtp.Port, ct);
        using var client = new SmtpClient { Timeout = 30_000 };
        try
        {
            await client.ConnectAsync(socket, smtp.Host, smtp.Port,
                smtp.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(smtp.Username, smtp.Password, ct);
            await client.SendAsync(msg, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
