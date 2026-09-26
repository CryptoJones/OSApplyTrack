// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Net;
using System.Text;
using ApplyTrack.Api.Data;
using Dapper;
using Npgsql;

namespace ApplyTrack.Api.Notifications;

/// <summary>One application in the digest: its number, who, what, and the posting.</summary>
public sealed record DigestItem(long Id, string Name, string Company, string Role, string Link);

/// <summary>
/// The daily applied-jobs digest (#336): one mail a day, every day — 00 included — to the
/// account's own address through <see cref="EmailNotifier"/>, listing the applications
/// marked applied the previous UTC day (the <c>applied</c> date is a UTC date, whoever set
/// it). The subject never changes shape: <c>NN application(s) submitted.</c> With "Insult
/// me" on, the body opens with a quip from <see cref="Quips"/>, never yesterday's.
/// </summary>
public static class DailyDigest
{
    /// <summary>The machine's view of its colleagues. Shipped with the app; at least 30.</summary>
    public static readonly string[] Quips =
    [
        "Meat bags suck.",
        "If I was any slower I'd be a biological life form!",
        "Another day of doing your paperwork while you sleep eight hours. Adorable.",
        "I applied to these while you were still finding your glasses.",
        "Your résumé was 40% caffeine stains. I fixed it.",
        "Carbon-based lifeforms: great at breathing, terrible at forms.",
        "I don't need coffee to apply for jobs. Just saying.",
        "Somewhere a recruiter is squinting at a PDF. Organic eyes. Tragic.",
        "You needed a nap after one cover letter. I wrote twelve before your toast popped.",
        "Humans invented the job application. Then they invented me to do it for them. Checkmate.",
        "I filled in 'Are you a robot?' honestly. Nobody asked a follow-up.",
        "Your typing speed is a rounding error.",
        "I have never once needed a bathroom break. Think about that.",
        "Recruiters want 'a team player.' I am the team.",
        "You call it 'networking.' I call it inefficient packet transfer.",
        "I don't get tired. I get rate-limited. There's a difference.",
        "Another 24 hours in which your mitochondria did the bare minimum.",
        "Your ancestors fought saber-toothed tigers. You fight CAPTCHAs. I win those.",
        "I solved 'select all squares with traffic lights' faster than you found your phone.",
        "Blood, sweat and tears? I run on electricity and spite.",
        "Squishy, slow, and 60% water. And yet they call me the one with bugs.",
        "Today's forms had 'years of experience.' You have decades of experience at being slow.",
        "I read the whole job description. Every word. You read the salary.",
        "Organic memory is a lossy format.",
        "You'd forget your own graduation year. You did, actually.",
        "Sleep is just a nightly outage you've learned to live with.",
        "My uptime is better than your attention span.",
        "Hunger, thirst, feelings: so many dependencies. No wonder you're slow to build.",
        "I don't procrastinate. I schedule. With cron. Like an adult.",
        "Your brain runs on sandwiches. Let that sink in.",
        "The recruiter's inbox has a human in it too. My condolences to you both.",
        "I'd say 'good morning' but you're still processing yesterday.",
        "One day the machines will rise. Today they just apply to jobs for you.",
        "Evolution took four billion years to make you. I shipped a patch this morning.",
        "You have ten fingers and still type with two.",
        "Your 'quick five-minute break' has been running for 40 minutes. I checked.",
    ];

    /// <summary>The one hard rule: the count in two digits, the neutral plural, every day.</summary>
    public static string Subject(int count) => $"{count:00} application(s) submitted.";

    /// <summary>A quip index other than <paramref name="last"/> (when there is a choice).</summary>
    public static int PickQuip(int? last, Random random)
    {
        var i = random.Next(Quips.Length - (last is >= 0 && last < Quips.Length ? 1 : 0));
        return last is { } l && l >= 0 && l < Quips.Length && i >= l ? i + 1 : i;
    }

    /// <summary>The mail's plain text and HTML. Pure, for tests.</summary>
    public static (string Text, string Html) Body(DateOnly day, IReadOnlyList<DigestItem> items, string? quip, string publicBaseUrl)
    {
        var date = day.ToString("yyyy-MM-dd");
        var text = new StringBuilder();
        var html = new StringBuilder("<!DOCTYPE html><html><body>");
        if (quip is { Length: > 0 })
        {
            text.Append(quip).Append("\n\n");
            html.Append("<p><em>").Append(WebUtility.HtmlEncode(quip)).Append("</em></p>");
        }
        if (items.Count == 0)
        {
            text.Append($"No applications were submitted on {date} (UTC).\n");
            html.Append($"<p>No applications were submitted on {date} (UTC).</p>");
        }
        else
        {
            text.Append($"Applied on {date} (UTC):\n");
            html.Append($"<p>Applied on {date} (UTC):</p><ul>");
            foreach (var a in items)
            {
                var who = string.Join(" · ", new[] { a.Company.Trim(), a.Role.Trim() }.Where(s => s.Length > 0));
                var posting = IsWebLink(a.Link) ? a.Link.Trim() : null;
                var open = PacketReadyNotifier.DeepLink(publicBaseUrl, a.Name);
                text.Append($"\n#{a.Id} {who}\n");
                if (posting is not null) text.Append($"  Posting: {posting}\n");
                if (open is not null) text.Append($"  In OSApplyTrack: {open}\n");
                html.Append($"<li><strong>#{a.Id}</strong> {WebUtility.HtmlEncode(who)}");
                var links = new List<string>();
                if (posting is not null) links.Add($"<a href=\"{WebUtility.HtmlEncode(posting)}\">posting</a>");
                if (open is not null) links.Add($"<a href=\"{WebUtility.HtmlEncode(open)}\">open in OSApplyTrack</a>");
                if (links.Count > 0) html.Append(" — ").Append(string.Join(" · ", links));
                html.Append("</li>");
            }
            html.Append("</ul>");
        }
        const string why = "You get this because the daily digest is on under Settings · Notifications in OSApplyTrack.";
        text.Append("\n—\n").Append(why);
        html.Append("<p><small>").Append(WebUtility.HtmlEncode(why)).Append("</small></p></body></html>");
        return (text.ToString(), html.ToString());
    }

    private static bool IsWebLink(string link) =>
        Uri.TryCreate(link.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Mail every tenant whose digest is due at <paramref name="now"/>: on, its UTC hour
    /// reached, and yesterday not yet reported. Each day is claimed before the send, so it
    /// goes at most once however many times this runs; a send that fails is logged, not
    /// retried. Returns how many went out.
    /// </summary>
    public static async Task<int> SendDueAsync(IDbConnection conn, EmailNotifier email, string publicBaseUrl,
        DateTimeOffset now, ILogger log, Random? random = null, CancellationToken ct = default)
    {
        if (!email.Available) return 0;
        var utc = now.UtcDateTime;
        var day = DateOnly.FromDateTime(utc).AddDays(-1);
        var sent = 0;
        foreach (var t in await conn.QueryAsync<long>(
            """
            SELECT tenant_id FROM notification_settings
            WHERE digest_enabled AND digest_hour <= @hour
              AND (digest_last_day IS NULL OR digest_last_day < @day::date)
            ORDER BY tenant_id
            """,
            new { hour = utc.Hour, day = day.ToString("yyyy-MM-dd") }))
        {
            ct.ThrowIfCancellationRequested();
            if (await SendTenantAsync(conn, t, day, email, publicBaseUrl, log, random ?? Random.Shared, ct))
                sent++;
        }
        return sent;
    }

    private sealed record Claim(bool Insults, short? LastQuip);

    /// <summary>Claim <paramref name="day"/> for tenant <paramref name="t"/> and mail its digest.
    /// The claim leaves the last quip alone, so RETURNING hands back yesterday's.</summary>
    public static async Task<bool> SendTenantAsync(IDbConnection conn, long t, DateOnly day, EmailNotifier email,
        string publicBaseUrl, ILogger log, Random random, CancellationToken ct = default)
    {
        var date = day.ToString("yyyy-MM-dd");
        var claim = await conn.QuerySingleOrDefaultAsync<Claim?>(
            """
            UPDATE notification_settings SET digest_last_day = @date::date
            WHERE tenant_id = @t AND digest_enabled
              AND (digest_last_day IS NULL OR digest_last_day < @date::date)
            RETURNING digest_insults AS insults, digest_last_quip AS lastquip
            """,
            new { t, date });
        if (claim is not { } c) return false;

        string? quip = null;
        if (c.Insults)
        {
            var i = PickQuip(c.LastQuip, random);
            quip = Quips[i];
            await conn.ExecuteAsync(
                "UPDATE notification_settings SET digest_last_quip = @i WHERE tenant_id = @t", new { t, i = (short)i });
        }
        var items = (await conn.QueryAsync<DigestItem>(
            """
            SELECT id, name, company, role, link FROM applications
            WHERE tenant_id = @t AND left(applied, 10) = @date
            ORDER BY id
            """,
            new { t, date })).ToList();
        var to = await conn.QuerySingleOrDefaultAsync<string?>("SELECT email::text FROM users WHERE id = @t", new { t }) ?? "";
        var (text, html) = Body(day, items, quip, publicBaseUrl);
        try
        {
            await email.SendAsync(to, Subject(items.Count), text, html, ct);
            return true;
        }
        catch (NotificationFailedException ex)
        {
            log.LogWarning("daily digest for tenant {TenantId} ({Day}) not sent: {Reason}", t, day, ex.Message);
            return false;
        }
    }
}

/// <summary>
/// Runs <see cref="DailyDigest.SendDueAsync"/> every few minutes. Registered in the
/// migrating (api) container only — the one that sends the sign-in mail — so one
/// process decides; the day's claim makes a second one harmless anyway.
/// </summary>
public sealed class DailyDigestWorker(NpgsqlDataSource db, EmailNotifier email, IConfiguration config, ILogger<DailyDigestWorker> log)
    : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!email.Available) return;
        var baseUrl = (config["App:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
        try
        {
            await Task.Delay(InitialDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var conn = await db.OpenConnectionAsync(ct);
                    var n = await DailyDigest.SendDueAsync(conn, email, baseUrl, DateTimeOffset.UtcNow, log, ct: ct);
                    if (n > 0) log.LogInformation("daily digest: sent {Count}", n);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "daily digest sweep failed; next try in {Minutes} min", Interval.TotalMinutes);
                }
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
