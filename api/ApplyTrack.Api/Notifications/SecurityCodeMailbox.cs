// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Net.Sockets;
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
    /// <summary>The newest code in the mailbox for this company sent after <paramref name="since"/>, or null.
    /// With <paramref name="boardHost"/> set, the run is parked on a board's email sign-in (#210)
    /// and the mail is the board's, not the company's: the answer is the sign-in link on that
    /// host, or the code, whichever the mail carries.</summary>
    Task<string?> FindCodeAsync(MailboxTarget target, string recipient, string company, DateTimeOffset since, CancellationToken ct, string boardHost = "");

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

    // MyGreenhouse's sign-in mail (#221): "Your security code is:" then the code on a line
    // of its own between runs of asterisks. Six to ten characters with at least one digit,
    // so "code is expiring" never qualifies.
    [GeneratedRegex(@"\bcode\s+is\b[^A-Za-z0-9]{0,80}(?=[A-Za-z]*\d)([A-Za-z0-9]{6,10})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodeIs();

    [GeneratedRegex(@"security code", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityCodeSubject();

    /// <summary>The code in a security-code email's text, or null. Public for tests.</summary>
    public static string? ExtractCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Labelled().Match(text);
        if (m.Success) return m.Groups[1].Value;
        var c = CodeIs().Match(text);
        if (c.Success) return c.Groups[1].Value;
        var b = BareLine().Match(text);
        return b.Success ? b.Groups[1].Value : null;
    }

    /// <summary>Does the subject name this company? Lenient: the company's first word is enough.</summary>
    public static bool SubjectMatches(string subject, string company)
    {
        if (!SecurityCodeSubject().IsMatch(subject)) return false;
        // The company's first WORD, punctuation off: "Dragos, Inc." is filed as "Dragos," and
        // Greenhouse's subject says "…your application to Dragos" — the comma alone kept the
        // code unread for eight minutes, twice, on a real submission.
        var first = company.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        first = first.Trim().Trim(',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '&');
        return first.Length == 0 || subject.Contains(first, StringComparison.OrdinalIgnoreCase);
    }

    // A sign-in mail's subject, when the sender's domain does not already give it away.
    [GeneratedRegex(@"code|link|sign.?in|log.?in|verify|verification|confirm|continue|anmeld|connexion|inicia|acceso", RegexOptions.IgnoreCase)]
    private static partial Regex SignInSubject();

    [GeneratedRegex(@"https?://[^\s<>""'\)\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex AnyLink();

    // The link that signs the candidate in, told from the footer's: it carries a token or
    // says what it is for.
    [GeneratedRegex(@"token|code|otp|verif|auth|login|log-in|signin|sign-in|magic|confirm|session|continue", RegexOptions.IgnoreCase)]
    private static partial Regex SignInLinkWords();

    [GeneratedRegex(@"unsubscribe|preferences|privacy|terms|imprint|impressum|help|support|settings|notification", RegexOptions.IgnoreCase)]
    private static partial Regex FooterLinkWords();

    // "Your code is 482913", "Code: 482913", "enter 482913" — a short run of digits near the word.
    [GeneratedRegex(@"(?:code|otp|pin)\D{0,40}?(\d{4,8})\b|\b(\d{4,8})\D{0,20}(?:is your|as your|est votre|ist ihr) .{0,20}code", RegexOptions.IgnoreCase)]
    private static partial Regex DigitCode();

    /// <summary>Does this host belong to the board — the same host, or a subdomain of it, or
    /// the same registrable domain (two-label approximation)?</summary>
    public static bool OnBoard(string host, string boardHost)
    {
        host = host.Trim().TrimStart('.').ToLowerInvariant();
        boardHost = boardHost.Trim().ToLowerInvariant();
        if (boardHost.StartsWith("www.", StringComparison.Ordinal)) boardHost = boardHost[4..];
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.Length == 0 || boardHost.Length == 0) return false;
        if (host == boardHost || host.EndsWith("." + boardHost, StringComparison.Ordinal)) return true;
        static string Reg(string h) { var p = h.Split('.'); return p.Length <= 2 ? h : string.Join('.', p[^2..]); }
        return Reg(host) == Reg(boardHost);
    }

    // Platforms that sign candidates in for many employers from one sender: every tenant's mail
    // comes from the same no-reply on the same domain, so the sender cannot say whose code it is.
    // Two Oracle runs a couple of minutes apart read each other's code (#318).
    private static readonly string[] SharedSenders = ["oraclecloud.com", "taleo.net"];

    /// <summary>Is this board a tenant of a platform whose sign-in mail is sent for every employer
    /// alike, so the mail has to name the employer to be this run's?</summary>
    public static bool SharedSender(string boardHost) =>
        SharedSenders.Any(d => OnBoard(boardHost, d));

    // Words that say nothing about which employer a mail is for.
    private static readonly HashSet<string> CompanyFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "inc", "llc", "ltd", "co", "corp", "corporation", "company",
        "group", "holdings", "plc", "gmbh", "sa", "ag", "restaurants",
    };

    [GeneratedRegex(@"\(([A-Za-z0-9&]{2,12})\)")]
    private static partial Regex Parenthesised();

    /// <summary>Does the mail name this company — its first telling word, or the short name it
    /// carries in parentheses ("The Depository Trust &amp; Clearing Corporation (DTCC)")? A
    /// company with no telling word matches anything. Public for tests.</summary>
    public static bool NamesCompany(string text, string company)
    {
        var names = Parenthesised().Matches(company).Select(m => m.Groups[1].Value).ToList();
        var first = Regex.Split(Parenthesised().Replace(company, " "), @"[^\p{L}\p{N}&']+")
            .FirstOrDefault(w => w.Length >= 2 && !CompanyFiller.Contains(w));
        if (first is not null) names.Add(first);
        if (names.Count == 0) return true;
        return names.Any(n => Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(n)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// What a board's sign-in mail hands the run (#210): the sign-in link on the board's own
    /// host — one that carries a token or names its purpose, never the footer's privacy or
    /// unsubscribe links — else the code the mail spells out. A link is preferred when both
    /// are there: it signs the candidate in whatever the page is showing. Null when the mail
    /// carries neither. Public for tests.
    /// </summary>
    public static string? ExtractSignIn(string text, string boardHost)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match m in AnyLink().Matches(text))
        {
            var raw = m.Value.TrimEnd('.', ',', ';', ':');
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || !OnBoard(u.Host, boardHost)) continue;
            var tail = u.PathAndQuery;
            if (FooterLinkWords().IsMatch(tail)) continue;
            if (SignInLinkWords().IsMatch(tail)) return raw;
        }
        var code = ExtractCode(text);
        if (code is not null) return code;
        var d = DigitCode().Match(text);
        return d.Success ? (d.Groups[1].Success ? d.Groups[1].Value : d.Groups[2].Value) : null;
    }

    public async Task<string?> FindCodeAsync(MailboxTarget target, string recipient, string company, DateTimeOffset since, CancellationToken ct, string boardHost = "")
    {
        using var client = await OpenAsync(target, ct);
        // IMAP's SINCE is a date, not a time: ask for the day and filter by time below.
        var signIn = boardHost.Trim().Length > 0;
        SearchQuery query = SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1));
        if (!signIn) query = query.And(SearchQuery.SubjectContains("code"));
        var uids = await client.Inbox.SearchAsync(query, ct);
        foreach (var uid in uids.Reverse())
        {
            var msg = await client.Inbox.GetMessageAsync(uid, ct);
            if (msg.Date < since.AddMinutes(-2)) continue;
            if (signIn)
            {
                // The board's mail, by its sender — or by a subject that says sign-in.
                var fromBoard = msg.From.Mailboxes.Any(a => OnBoard(a.Address.Split('@').LastOrDefault() ?? "", boardHost));
                if (!fromBoard && !SignInSubject().IsMatch(msg.Subject ?? "")) continue;
            }
            else if (!SubjectMatches(msg.Subject ?? "", company)) continue;
            if (recipient.Length > 0 && !msg.To.Mailboxes.Any(a => a.Address.Equals(recipient, StringComparison.OrdinalIgnoreCase))
                && !msg.Cc.Mailboxes.Any(a => a.Address.Equals(recipient, StringComparison.OrdinalIgnoreCase)))
                continue;
            var text = msg.TextBody ?? StripHtml(msg.HtmlBody ?? "");
            if (signIn && SharedSender(boardHost) && !NamesCompany((msg.Subject ?? "") + "\n" + text, company)) continue;
            var code = signIn ? ExtractSignIn(text, boardHost) : ExtractCode(text);
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
        // Resolve the host once, dial a public address pinned from that resolution, then hand
        // MailKit the already-connected socket (the host is still passed so TLS validates the
        // certificate against it). Connecting to the pinned address closes the check-then-connect
        // gap the old code had — it resolved and rejected private, then let ImapClient.Connect
        // resolve the same name a second time, so a rebinding server could answer public at
        // check time and private at connect time (#231). Same no-TOCTOU pattern JobPageFetcher
        // uses for the scrape path (#51).
        var socket = await DialPublicAsync(target.Host, target.Port, ct);
        var client = new ImapClient { Timeout = 20_000 };
        try
        {
            await client.ConnectAsync(socket, target.Host, target.Port,
                target.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(target.Username, target.Password, ct);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Resolve the host once and return a connected socket to one of its public
    /// addresses, or throw the validation exception when it resolves to nothing public.</summary>
    private static async Task<Socket> DialPublicAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, ct); }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new AppValidationException("the mailbox host could not be resolved");
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(PublicAddressesOrThrow(addresses), port, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The resolved addresses that are safe to dial, or the validation exception when
    /// the host resolves to nothing public — the same rule every other user-supplied destination
    /// gets. Pinning the connection to exactly these addresses is what removes the rebinding
    /// window: the name is resolved once and only these addresses are dialed, so a later
    /// resolution can never swap in a private target. Public for tests.</summary>
    public static IPAddress[] PublicAddressesOrThrow(IPAddress[] resolved)
    {
        var publicAddresses = Array.FindAll(resolved, a => !JobPageFetcher.IsBlockedAddress(a));
        if (publicAddresses.Length == 0)
            throw new AppValidationException("refusing to connect to a private address for the mailbox");
        return publicAddresses;
    }

    private static string StripHtml(string html)
    {
        var text = Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</tr>|</h\d>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"[ \t]+", " ");
    }
}
