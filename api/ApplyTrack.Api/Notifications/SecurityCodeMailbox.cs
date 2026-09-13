// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Scrape;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace ApplyTrack.Api.Notifications;

/// <summary>Where a parked run gets the security code the board emailed the candidate.</summary>
public interface ISecurityCodeSource
{
    /// <summary>The newest code in the mailbox for this company sent after <paramref name="since"/>, or null.</summary>
    Task<string?> FindCodeAsync(MailboxTarget target, string recipient, string company, DateTimeOffset since, CancellationToken ct);

    /// <summary>Connect, sign in, open the inbox: the message count, or an exception with the reason.</summary>
    Task<int> TestAsync(MailboxTarget target, CancellationToken ct);
}

/// <summary>
/// Reads the candidate's mailbox over IMAP (MailKit, already here for SMTP) for the mail
/// Greenhouse sends when its captcha fallback wants a code — subject "Security code for
/// your application to &lt;company&gt;", body "Copy and paste this code into the security
/// code field on your application: &lt;CODE&gt;". Read-only, newest first, only mail that
/// arrived after the click. The host is resolved and refused when private, the same rule
/// every other user-supplied destination gets.
/// </summary>
public sealed partial class ImapSecurityCodeSource : ISecurityCodeSource
{
    [GeneratedRegex(@"security code field on your application:?\s*([A-Za-z0-9]{6,12})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Labelled();

    // A line that is nothing but the code: 6–12 characters with a digit or a capital
    // after the first character, so "Regards" and "Greenhouse" never qualify.
    [GeneratedRegex(@"(?m)^[ \t]*(?=[A-Za-z0-9]{6,12}[ \t]*\r?$)(?=[A-Za-z0-9]*\d|[A-Za-z0-9]+[A-Z])([A-Za-z0-9]{6,12})[ \t]*\r?$")]
    private static partial Regex BareLine();

    [GeneratedRegex(@"security code", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityCodeSubject();

    /// <summary>The code in a security-code email's text, or null. Public for tests.</summary>
    public static string? ExtractCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Labelled().Match(text);
        if (m.Success) return m.Groups[1].Value;
        var b = BareLine().Match(text);
        return b.Success ? b.Groups[1].Value : null;
    }

    /// <summary>Does the subject name this company? Lenient: the company's first word is enough.</summary>
    public static bool SubjectMatches(string subject, string company)
    {
        if (!SecurityCodeSubject().IsMatch(subject)) return false;
        var first = company.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length == 0 || subject.Contains(first, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> FindCodeAsync(MailboxTarget target, string recipient, string company, DateTimeOffset since, CancellationToken ct)
    {
        using var client = await OpenAsync(target, ct);
        // IMAP's SINCE is a date, not a time: ask for the day and filter by time below.
        var query = SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1)).And(SearchQuery.SubjectContains("code"));
        var uids = await client.Inbox.SearchAsync(query, ct);
        foreach (var uid in uids.Reverse())
        {
            var msg = await client.Inbox.GetMessageAsync(uid, ct);
            if (msg.Date < since.AddMinutes(-2)) continue;
            if (!SubjectMatches(msg.Subject ?? "", company)) continue;
            if (recipient.Length > 0 && !msg.To.Mailboxes.Any(a => a.Address.Equals(recipient, StringComparison.OrdinalIgnoreCase))
                && !msg.Cc.Mailboxes.Any(a => a.Address.Equals(recipient, StringComparison.OrdinalIgnoreCase)))
                continue;
            var text = msg.TextBody ?? StripHtml(msg.HtmlBody ?? "");
            var code = ExtractCode(text);
            if (code is not null) return code;
        }
        await client.DisconnectAsync(true, ct);
        return null;
    }

    public async Task<int> TestAsync(MailboxTarget target, CancellationToken ct)
    {
        using var client = await OpenAsync(target, ct);
        var count = client.Inbox.Count;
        await client.DisconnectAsync(true, ct);
        return count;
    }

    private static async Task<ImapClient> OpenAsync(MailboxTarget target, CancellationToken ct)
    {
        await GuardHostAsync(target.Host, ct);
        var client = new ImapClient { Timeout = 20_000 };
        try
        {
            await client.ConnectAsync(target.Host, target.Port,
                target.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(target.Username, target.Password, ct);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The same rule as every other user-supplied destination: never a private address.</summary>
    private static async Task GuardHostAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, ct); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            throw new AppValidationException("the mailbox host could not be resolved");
        }
        if (addresses.Length == 0 || addresses.Any(JobPageFetcher.IsBlockedAddress))
            throw new AppValidationException("refusing to connect to a private address for the mailbox");
    }

    private static string StripHtml(string html)
    {
        var text = Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</tr>|</h\d>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"[ \t]+", " ");
    }
}
