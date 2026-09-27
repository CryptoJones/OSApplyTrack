// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Globalization;
using System.Text;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using Dapper;
using Npgsql;

namespace ApplyTrack.Api.Notifications;

/// <summary>
/// The daily reminder (#354): once a day, at the tenant's reminder hour (UTC), the
/// follow-ups due that UTC day and the interviews in the next 24 hours, sent through the
/// same channels as every other moo (Telegram and email, #355) and gated by its own
/// per-event toggle. A day with nothing due today sends nothing (and stays unclaimed, so
/// something dated today later still goes); overdue follow-ups ride along on a day that
/// does, so a stale date nudges without nagging every morning. The day is claimed before
/// the send, so it goes at most once however many times this runs.
/// </summary>
public static class Reminders
{
    /// <summary>How many of each list one message names; the rest are counted.</summary>
    public const int MaxListed = 10;

    /// <summary>A note quoted in the message is cut to this; the whole message to <see cref="MaxChars"/>
    /// (Telegram refuses a message over 4096 characters).</summary>
    public const int MaxNoteChars = 120;
    public const int MaxChars = 4000;

    /// <summary>The window of interviews a reminder covers, from the moment it goes.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private static string Who(string company, string role)
    {
        var who = string.Join(" · ", new[] { company.Trim(), role.Trim() }.Where(s => s.Length > 0));
        return who.Length > 0 ? who : "an application";
    }

    private static string Plural(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";

    /// <summary>True when the day has something to say: a follow-up due today, or an interview.</summary>
    public static bool Worth(DueList due) => due.Interviews.Count > 0 || due.Followups.Any(f => !f.Overdue);

    /// <summary>The subject line and the message. Pure, for tests.</summary>
    public static (string Subject, string Text) Message(DueList due, string publicBaseUrl)
    {
        var today = due.Followups.Where(f => !f.Overdue).ToList();
        var overdue = due.Followups.Where(f => f.Overdue).ToList();
        var parts = new List<string>();
        if (due.Interviews.Count > 0) parts.Add(Plural(due.Interviews.Count, "interview"));
        if (today.Count > 0) parts.Add(Plural(today.Count, "follow-up"));
        var subject = $"Due today: {(parts.Count > 0 ? string.Join(", ", parts) : "nothing")}";

        var text = new StringBuilder($"📅 Due today ({due.Today}, UTC)\n");
        void Section<T>(string heading, IReadOnlyList<T> items, Func<T, string> line, Func<T, string> name)
        {
            if (items.Count == 0) return;
            text.Append('\n').Append(heading).Append('\n');
            foreach (var item in items.Take(MaxListed))
            {
                text.Append("• ").Append(line(item)).Append('\n');
                if (PacketReadyNotifier.DeepLink(publicBaseUrl, name(item)) is { } link)
                    text.Append("  ").Append(link).Append('\n');
            }
            if (items.Count > MaxListed)
                text.Append($"…and {items.Count - MaxListed} more.\n");
        }
        Section("Interviews in the next 24 hours:", due.Interviews,
            i => $"{i.At.ToUniversalTime().ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture)} UTC — {Who(i.Company, i.Role)}"
                + (Clip(i.Note.Trim().ReplaceLineEndings(" "), MaxNoteChars) is { Length: > 0 } note ? $" ({note})" : ""),
            i => i.Name);
        Section("Follow-ups due today:", today, f => $"#{f.Id} {Who(f.Company, f.Role)}", f => f.Name);
        Section($"Overdue follow-ups ({overdue.Count}):", overdue,
            f => $"#{f.Id} {Who(f.Company, f.Role)} — was due {f.Followup}", f => f.Name);
        const string footer = "\nMove a follow-up's date, or clear it, to stop hearing about it.";
        var body = text.ToString();
        if (body.Length + footer.Length > MaxChars)
            body = body[..(MaxChars - footer.Length - 2)] + "…\n";
        return (subject, body + footer);
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    /// <summary>
    /// Remind every tenant whose reminder is due at <paramref name="now"/>: the event on, a
    /// channel on, its UTC hour reached, and today not yet claimed. Returns how many went out.
    /// </summary>
    public static async Task<int> SendDueAsync(IDbConnection conn, PacketReadyNotifier notifier, SecretProtector protector,
        ILoggerFactory logs, string publicBaseUrl, DateTimeOffset now, CancellationToken ct = default)
    {
        var log = logs.CreateLogger(typeof(Reminders));
        var utc = now.UtcDateTime;
        var day = DateOnly.FromDateTime(utc);
        var sent = 0;
        foreach (var t in await conn.QueryAsync<long>(
            """
            SELECT tenant_id FROM notification_settings
            WHERE notify_followup_due AND (telegram_enabled OR email_enabled) AND reminder_hour <= @hour
              AND (reminder_last_day IS NULL OR reminder_last_day < @day::date)
            ORDER BY tenant_id
            """,
            new { hour = utc.Hour, day = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await SendTenantAsync(conn, t, day, utc, notifier, protector, logs, publicBaseUrl, ct))
                    sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One tenant's trouble never stops the others' reminders.
                log.LogWarning(ex, "reminder for tenant {TenantId} ({Day}) failed", t, day);
            }
        }
        return sent;
    }

    /// <summary>Claim <paramref name="day"/> for tenant <paramref name="t"/>, read what is due,
    /// and send it when there is anything. True when a channel took it.</summary>
    public static async Task<bool> SendTenantAsync(IDbConnection conn, long t, DateOnly day, DateTime now,
        PacketReadyNotifier notifier, SecretProtector protector, ILoggerFactory logs, string publicBaseUrl,
        CancellationToken ct = default)
    {
        DueList due;
        // The claim and the read are one transaction: a failure between them rolls the claim
        // back, so the day is tried again rather than lost. It commits before the send, so a
        // refused message is never retried (at most once).
        using (var tx = conn.BeginTransaction())
        {
            var claimed = await conn.ExecuteAsync(
                """
                UPDATE notification_settings SET reminder_last_day = @day::date
                WHERE tenant_id = @t AND notify_followup_due
                  AND (reminder_last_day IS NULL OR reminder_last_day < @day::date)
                """,
                new { t, day = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, tx);
            if (claimed == 0) return false;
            due = await new AppEventRepo(conn, t).DueAsync(day, now, now + Window, tx);
            // Nothing to say yet: leave the day unclaimed, so a follow-up dated today or an
            // interview added later in the day still gets its reminder on a later sweep.
            if (!Worth(due))
            {
                tx.Rollback();
                return false;
            }
            tx.Commit();
        }
        var (subject, text) = Message(due, publicBaseUrl);
        var settings = new NotificationSettingsRepo(conn, t, protector, logs.CreateLogger<NotificationSettingsRepo>());
        return await notifier.SendAsync(settings, text, ct, PacketReadyNotifier.Kind.FollowupDue, subject);
    }
}

/// <summary>
/// Runs <see cref="Reminders.SendDueAsync"/> every few minutes, in the migrating (api)
/// container only, beside the daily digest; the day's claim makes a second one harmless.
/// </summary>
public sealed class ReminderWorker(NpgsqlDataSource db, PacketReadyNotifier notifier, SecretProtector protector,
    IConfiguration config, ILoggerFactory logs) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var log = logs.CreateLogger<ReminderWorker>();
        var baseUrl = (config["App:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
        try
        {
            await Task.Delay(InitialDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var conn = await db.OpenConnectionAsync(ct);
                    var n = await Reminders.SendDueAsync(conn, notifier, protector, logs, baseUrl, DateTimeOffset.UtcNow, ct);
                    if (n > 0) log.LogInformation("reminders: sent {Count}", n);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "reminder sweep failed; next try in {Minutes} min", Interval.TotalMinutes);
                }
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
