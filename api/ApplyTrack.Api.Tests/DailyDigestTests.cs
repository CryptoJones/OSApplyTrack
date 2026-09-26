// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Notifications;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The daily applied-jobs digest (#336): the subject never changes shape, it goes every
/// day (00 included) to the account's own address, lists only that tenant's applications
/// applied the previous UTC day, goes at most once a day, and insults only on request.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DailyDigestTests(PostgresFixture pg)
{
    private static readonly EmailOptions Smtp = new() { Host = "smtp.example.com", From = "apply@example.com" };
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 13, 5, 0, TimeSpan.Zero);
    private const string Yesterday = "2026-09-25";

    [Theory]
    [InlineData(0, "00 application(s) submitted.")]
    [InlineData(1, "01 application(s) submitted.")]
    [InlineData(13, "13 application(s) submitted.")]
    [InlineData(99, "99 application(s) submitted.")]
    public void The_subject_is_two_digits_and_the_neutral_plural(int count, string subject) =>
        Assert.Equal(subject, DailyDigest.Subject(count));

    [Fact]
    public void At_least_thirty_distinct_quips_ship_and_none_repeats_yesterdays()
    {
        Assert.True(DailyDigest.Quips.Length >= 30);
        Assert.Equal(DailyDigest.Quips.Length, DailyDigest.Quips.Distinct().Count());
        var random = new Random(7);
        var seen = new HashSet<int>();
        for (var i = 0; i < 2000; i++)
        {
            var last = i % DailyDigest.Quips.Length;
            var pick = DailyDigest.PickQuip(last, random);
            Assert.NotEqual(last, pick);
            Assert.InRange(pick, 0, DailyDigest.Quips.Length - 1);
            seen.Add(pick);
        }
        Assert.Equal(DailyDigest.Quips.Length, seen.Count);
        Assert.InRange(DailyDigest.PickQuip(null, random), 0, DailyDigest.Quips.Length - 1);
    }

    [Fact]
    public void The_body_lists_each_number_company_role_posting_and_a_link_back()
    {
        var (text, html) = DailyDigest.Body(new DateOnly(2026, 9, 25),
            [new DigestItem(15339, "acme-eng.md", "Acme <Labs>", "Engineer", "https://jobs.example/1"),
             new DigestItem(15340, "beta.md", "Beta", "", "javascript:alert(1)")],
            "Meat bags suck.", "https://apply.example");
        Assert.StartsWith("Meat bags suck.", text);
        Assert.Contains("#15339 Acme <Labs> · Engineer", text);
        Assert.Contains("Posting: https://jobs.example/1", text);
        Assert.Contains("https://apply.example/#app=acme-eng.md", text);
        Assert.Contains("#15340 Beta", text);
        Assert.DoesNotContain("javascript:", text);
        Assert.Contains("Acme &lt;Labs&gt;", html);
        Assert.DoesNotContain("<Labs>", html);
        Assert.Contains("href=\"https://jobs.example/1\"", html);
        Assert.DoesNotContain("javascript:", html);

        var (empty, _) = DailyDigest.Body(new DateOnly(2026, 9, 25), [], null, "");
        Assert.StartsWith("No applications", empty);
    }

    private async Task<(NpgsqlConnection Conn, long T, string Address)> TenantAsync(bool digest, bool insults = false, int hour = 0)
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var address = TestAuth.UniqueEmail();
        var t = await TestAuth.EnsureUserAsync(conn, address);
        var settings = new NotificationSettingsRepo(conn, t, AgentWorkerTests.Protector, NullLogger<NotificationSettingsRepo>.Instance);
        await settings.UpsertDigestAsync(digest, insults, hour);
        return (conn, t, address);
    }

    private static Task AppAsync(NpgsqlConnection conn, long t, string name, string applied) =>
        conn.ExecuteAsync(
            "INSERT INTO applications (tenant_id, name, company, role, status, link, applied) "
            + "VALUES (@t, @name, 'Acme', @name, 'applied', 'https://jobs.example/' || @name, @applied)",
            new { t, name, applied });

    private static EmailNotifier Email(CapturingEmailSender sender) => new(sender, Smtp, NullLogger<EmailNotifier>.Instance);

    private static Task<int> SendAsync(NpgsqlConnection conn, CapturingEmailSender sender, DateTimeOffset? now = null) =>
        DailyDigest.SendDueAsync(conn, Email(sender), "https://apply.example", now ?? Now, NullLogger.Instance, new Random(1));

    [Fact]
    public async Task Yesterdays_applications_go_once_to_the_accounts_own_address_and_no_one_elses()
    {
        var (conn, t, address) = await TenantAsync(digest: true);
        var (other, o, _) = await TenantAsync(digest: false);
        await using var c1 = conn;
        await using var c2 = other;
        await AppAsync(conn, t, "one.md", Yesterday);
        await AppAsync(conn, t, "two.md", Yesterday);
        await AppAsync(conn, t, "today.md", "2026-09-26");
        await AppAsync(conn, t, "older.md", "2026-09-24");
        await AppAsync(other, o, "theirs.md", Yesterday);

        var mail = new CapturingEmailSender();
        await SendAsync(conn, mail);
        var sent = Assert.Single(mail.Messages, m => m.To == address);
        Assert.Equal("02 application(s) submitted.", sent.Subject);
        Assert.Contains("one.md", sent.TextBody);
        Assert.Contains("two.md", sent.TextBody);
        Assert.DoesNotContain("today.md", sent.TextBody);
        Assert.DoesNotContain("older.md", sent.TextBody);
        Assert.DoesNotContain("theirs.md", sent.TextBody);
        Assert.NotNull(sent.HtmlBody);
        // Insults are off by default: the body goes straight to the list.
        Assert.DoesNotContain(DailyDigest.Quips, q => sent.TextBody.Contains(q));
        // The other tenant's digest is off: its application went to no one.
        Assert.DoesNotContain(mail.Messages, m => m.TextBody.Contains("theirs.md"));

        // The day is claimed: a second run the same day sends nothing more.
        await SendAsync(conn, mail, Now.AddMinutes(10));
        Assert.Single(mail.Messages, m => m.To == address);
        // The next day goes again.
        await SendAsync(conn, mail, Now.AddDays(1));
        Assert.Equal(2, mail.Messages.Count(m => m.To == address));
    }

    [Fact]
    public async Task A_day_with_nothing_still_sends_00_and_insults_on_request()
    {
        var (conn, _, address) = await TenantAsync(digest: true, insults: true);
        await using var _ = conn;
        var mail = new CapturingEmailSender();
        await SendAsync(conn, mail);
        var sent = Assert.Single(mail.Messages, m => m.To == address);
        Assert.Equal("00 application(s) submitted.", sent.Subject);
        Assert.Contains(DailyDigest.Quips, q => sent.TextBody.StartsWith(q));

        // And tomorrow's insult is a different one.
        await SendAsync(conn, mail, Now.AddDays(1));
        var bodies = mail.Messages.Where(m => m.To == address).Select(m => m.TextBody.Split('\n')[0]).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.NotEqual(bodies[0], bodies[1]);
    }

    [Fact]
    public async Task Nothing_goes_before_the_chosen_hour_or_without_smtp()
    {
        var (conn, _, address) = await TenantAsync(digest: true, hour: 20);
        await using var _ = conn;
        var mail = new CapturingEmailSender();
        await SendAsync(conn, mail);
        Assert.DoesNotContain(mail.Messages, m => m.To == address);

        var noSmtp = new EmailNotifier(mail, new EmailOptions(), NullLogger<EmailNotifier>.Instance);
        Assert.Equal(0, await DailyDigest.SendDueAsync(conn, noSmtp, "", Now.AddHours(8), NullLogger.Instance));
        Assert.DoesNotContain(mail.Messages, m => m.To == address);

        await SendAsync(conn, mail, Now.AddHours(8));
        Assert.Single(mail.Messages, m => m.To == address);
    }

    [Fact]
    public async Task The_digest_hour_is_validated_and_defaults_to_noon_utc()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var fresh = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        var repo = new NotificationSettingsRepo(conn, fresh, AgentWorkerTests.Protector, NullLogger<NotificationSettingsRepo>.Instance);
        Assert.Equal(new DigestSettings(false, false, 12), await repo.GetDigestAsync());
        await Assert.ThrowsAsync<AppValidationException>(() => repo.UpsertDigestAsync(true, null, 24));
        await repo.UpsertDigestAsync(true, null, null);
        Assert.Equal(new DigestSettings(true, false, 12), await repo.GetDigestAsync());
    }
}
