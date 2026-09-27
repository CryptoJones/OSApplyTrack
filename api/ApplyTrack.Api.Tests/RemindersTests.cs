// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Endpoints;
using ApplyTrack.Api.Notifications;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Follow-up and interview reminders, and the calendar feed (#354): interviews on an
/// application, what is due, the timeline entries they add, the daily reminder through
/// every channel with its own toggle, the export round trip, and the ICS feed behind a
/// revocable, feed-only token. Each test is a fresh tenant on the shared container.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RemindersTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private long _t;

    public RemindersTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            b.UseSetting("App:PublicBaseUrl", "https://apply.example");
        });
        var (t, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        _t = t;
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private async Task<string> CreateAsync(string company, string status = "applied", string followup = "")
    {
        var res = await _client.PostAsync("/api/apps", Json(new { company, role = "Engineer", status, followup }));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJsonAsync(res)).GetProperty("filename").GetString()!;
    }

    private async Task<NpgsqlConnection> OwnerAsync()
    {
        var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static string Day(int offset) => DateTime.UtcNow.Date.AddDays(offset).ToString("yyyy-MM-dd");

    [Fact]
    public async Task Interviews_are_added_listed_soonest_first_and_removed()
    {
        var name = await CreateAsync("Acme");
        var later = await _client.PostAsync($"/api/apps/{name}/interviews", Json(new { at = "2026-10-02T15:00:00Z", note = "Onsite, 3 rounds" }));
        Assert.Equal(HttpStatusCode.Created, later.StatusCode);
        var sooner = await _client.PostAsync($"/api/apps/{name}/interviews", Json(new { at = "2026-10-01T09:30:00-05:00" }));
        Assert.Equal(HttpStatusCode.Created, sooner.StatusCode);

        var list = await ReadJsonAsync(await _client.GetAsync($"/api/apps/{name}/interviews"));
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(new DateTime(2026, 10, 1, 14, 30, 0, DateTimeKind.Utc), list[0].GetProperty("at").GetDateTime().ToUniversalTime());
        Assert.Equal("Onsite, 3 rounds", list[1].GetProperty("note").GetString());

        var id = list[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/apps/{name}/interviews/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/apps/{name}/interviews/{id}")).StatusCode);
        Assert.Equal(1, (await ReadJsonAsync(await _client.GetAsync($"/api/apps/{name}/interviews"))).GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsync($"/api/apps/{name}/interviews", Json(new { at = "next tuesday" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsync("/api/apps/nope.md/interviews", Json(new { at = "2026-10-01T09:00:00Z" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/apps/nope.md/interviews")).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_interview_cannot_be_read_or_removed()
    {
        var name = await CreateAsync("Acme");
        var created = await ReadJsonAsync(await _client.PostAsync($"/api/apps/{name}/interviews", Json(new { at = "2026-10-02T15:00:00Z" })));
        var id = created.GetProperty("id").GetInt64();

        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/apps/{name}/interviews")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/apps/{name}/interviews/{id}")).StatusCode);
        Assert.Equal(1, (await ReadJsonAsync(await _client.GetAsync($"/api/apps/{name}/interviews"))).GetArrayLength());
    }

    [Fact]
    public async Task The_timeline_carries_the_interviews_and_the_follow_up_among_the_statuses()
    {
        var name = await CreateAsync("Acme", status: "lead", followup: "2099-01-05");
        await _client.PostAsync($"/api/apps/{name}/interviews", Json(new { at = "2099-01-03T15:00:00Z", note = "Panel" }));

        var entries = await ReadJsonAsync(await _client.GetAsync($"/api/apps/{name}/history"));
        Assert.Equal(["status", "interview", "followup"], entries.EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal("Panel", entries[1].GetProperty("note").GetString());
        Assert.Equal("", entries[1].GetProperty("to_status").GetString());
        Assert.StartsWith("2099-01-05T00:00:00", entries[2].GetProperty("at").GetString());
        // A status row keeps its shape.
        Assert.Equal("lead", entries[0].GetProperty("to_status").GetString());
    }

    [Fact]
    public async Task Due_lists_follow_ups_on_or_before_today_in_play_only_and_the_coming_interviews()
    {
        var today = await CreateAsync("Today", followup: Day(0));
        var overdue = await CreateAsync("Overdue", status: "screen", followup: Day(-3));
        await CreateAsync("Tomorrow", followup: Day(1));
        await CreateAsync("Rejected", status: "rejected", followup: Day(-1));
        await CreateAsync("Lead", status: "lead", followup: Day(0));
        await CreateAsync("Junk", followup: "soon");
        await _client.PostAsync($"/api/apps/{today}/interviews", Json(new { at = DateTime.UtcNow.AddDays(2).ToString("O") }));
        await _client.PostAsync($"/api/apps/{today}/interviews", Json(new { at = DateTime.UtcNow.AddDays(9).ToString("O") }));
        await _client.PostAsync($"/api/apps/{today}/interviews", Json(new { at = DateTime.UtcNow.AddDays(-2).ToString("O") }));

        var due = await ReadJsonAsync(await _client.GetAsync($"/api/due?today={Day(0)}"));
        Assert.Equal(Day(0), due.GetProperty("today").GetString());
        var followups = due.GetProperty("followups").EnumerateArray().ToList();
        Assert.Equal([overdue, today], followups.Select(f => f.GetProperty("name").GetString()));
        Assert.True(followups[0].GetProperty("overdue").GetBoolean());
        Assert.False(followups[1].GetProperty("overdue").GetBoolean());
        var interview = Assert.Single(due.GetProperty("interviews").EnumerateArray());
        Assert.Equal(today, interview.GetProperty("name").GetString());

        // The client's own date decides "today".
        var yesterday = await ReadJsonAsync(await _client.GetAsync($"/api/due?today={Day(-1)}"));
        Assert.Equal([overdue], yesterday.GetProperty("followups").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/due?today=tomorrow")).StatusCode);
    }

    [Fact]
    public async Task The_notification_settings_carry_the_reminder_toggle_and_hour()
    {
        var before = await ReadJsonAsync(await _client.GetAsync("/api/notifications"));
        Assert.True(before.GetProperty("notify_followup_due").GetBoolean());
        Assert.Equal(12, before.GetProperty("reminder_hour").GetInt32());
        Assert.False(before.GetProperty("calendar_feed_enabled").GetBoolean());

        var saved = await ReadJsonAsync(await _client.PutAsync("/api/notifications", Json(new { notify_followup_due = false, reminder_hour = 7 })));
        Assert.False(saved.GetProperty("notify_followup_due").GetBoolean());
        Assert.Equal(7, saved.GetProperty("reminder_hour").GetInt32());
        // Another toggle is untouched.
        Assert.True(saved.GetProperty("notify_packet_ready").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsync("/api/notifications", Json(new { reminder_hour = 24 }))).StatusCode);
    }

    [Fact]
    public async Task The_calendar_feed_opens_with_its_token_only_and_stops_when_replaced_or_revoked()
    {
        var name = await CreateAsync("Acme; Inc", followup: "2099-02-01");
        await _client.PostAsync($"/api/apps/{name}/interviews", Json(new { at = "2099-01-20T16:00:00Z", note = "Video, bring questions" }));

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/calendar/feed", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/calendar.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/calendar.ics?token=guess")).StatusCode);

        var made = await ReadJsonAsync(await _client.PostAsync("/api/calendar/feed", null));
        var path = made.GetProperty("path").GetString()!;
        Assert.StartsWith("/api/calendar.ics?token=", path);
        Assert.Equal("https://apply.example" + path, made.GetProperty("url").GetString());
        Assert.True((await ReadJsonAsync(await _client.GetAsync("/api/notifications"))).GetProperty("calendar_feed_enabled").GetBoolean());

        var res = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/calendar", res.Content.Headers.ContentType!.MediaType);
        var ics = await res.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20990201", ics);
        Assert.Contains("SUMMARY:Follow up: Acme\\; Inc · Engineer", ics);
        Assert.Contains("DTSTART:20990120T160000Z", ics);
        Assert.Contains("DTEND:20990120T170000Z", ics);
        Assert.Contains("Video\\, bring questions", ics);
        Assert.Contains("URL:https://apply.example/#app=", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        // The token is stored by hash only.
        await using (var conn = await OwnerAsync())
        {
            var token = Uri.UnescapeDataString(path[(path.IndexOf('=') + 1)..]);
            var stored = await conn.ExecuteScalarAsync<byte[]>(
                "SELECT token_hash FROM api_tokens WHERE tenant_id = @t", new { t = _t });
            Assert.Equal(Tokens.Sha256(token), stored);
            Assert.NotNull(await conn.ExecuteScalarAsync<DateTime?>(
                "SELECT last_used_at FROM api_tokens WHERE tenant_id = @t", new { t = _t }));
        }
        // It opens the feed and nothing else: it is no session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/apps?token=" + path.Split('=')[1])).StatusCode);

        var remade = await ReadJsonAsync(await _client.PostAsync("/api/calendar/feed", null));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(path)).StatusCode);
        var newPath = remade.GetProperty("path").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(newPath)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/calendar/feed")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(newPath)).StatusCode);
        Assert.False((await ReadJsonAsync(await _client.GetAsync("/api/calendar/feed"))).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task One_tenants_feed_never_carries_another_tenants_events()
    {
        await CreateAsync("Mine", followup: "2099-03-01");
        var (o, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        await other.PostAsync("/api/apps", Json(new { company = "Theirs", role = "Engineer", status = "applied", followup = "2099-03-01" }));

        var path = (await ReadJsonAsync(await _client.PostAsync("/api/calendar/feed", null))).GetProperty("path").GetString()!;
        using var anonymous = _factory.CreateClient();
        var ics = await (await anonymous.GetAsync(path)).Content.ReadAsStringAsync();
        Assert.Contains("Mine", ics);
        Assert.DoesNotContain("Theirs", ics);
    }

    [Fact]
    public async Task A_disabled_account_s_feed_goes_dark()
    {
        var path = (await ReadJsonAsync(await _client.PostAsync("/api/calendar/feed", null))).GetProperty("path").GetString()!;
        await using (var conn = await OwnerAsync())
            await conn.ExecuteAsync("UPDATE users SET status = 'disabled' WHERE id = @t", new { t = _t });
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Interviews_travel_in_the_export_and_the_import_restores_them()
    {
        var name = await CreateAsync("Acme");
        await _client.PostAsync($"/api/apps/{name}/interviews", Json(new { at = "2099-01-20T16:00:00Z", note = "Panel" }));
        var export = await ReadJsonAsync(await _client.GetAsync("/api/account/export"));
        var carried = Assert.Single(export.GetProperty("interviews").EnumerateArray());
        Assert.Equal(name, carried.GetProperty("application").GetString());
        Assert.Equal("Panel", carried.GetProperty("note").GetString());

        // Into a fresh account: the interview arrives with its app.
        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        using var fresh = _factory.CreateClient();
        fresh.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        var imported = await fresh.PostAsync("/api/account/import",
            new StringContent(export.GetRawText(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var list = await ReadJsonAsync(await fresh.GetAsync($"/api/apps/{name}/interviews"));
        var one = Assert.Single(list.EnumerateArray());
        Assert.Equal("Panel", one.GetProperty("note").GetString());

        // Re-importing is idempotent: the file's set replaces, never adds.
        await fresh.PostAsync("/api/account/import", new StringContent(export.GetRawText(), Encoding.UTF8, "application/json"));
        Assert.Equal(1, (await ReadJsonAsync(await fresh.GetAsync($"/api/apps/{name}/interviews"))).GetArrayLength());
    }

    [Fact]
    public void The_feed_escapes_text_and_folds_long_lines_on_character_boundaries()
    {
        Assert.Equal("a\\, b\\; c\\\\d\\ne", CalendarFeed.Escape("a, b; c\\d\r\ne"));
        var line = "SUMMARY:" + string.Concat(Enumerable.Repeat("é", 60));
        var folded = CalendarFeed.Fold(line);
        foreach (var part in folded.Split("\r\n"))
            Assert.True(Encoding.UTF8.GetByteCount(part) <= 75);
        Assert.Equal(line, folded.Replace("\r\n ", ""));
        Assert.Equal("SHORT:x", CalendarFeed.Fold("SHORT:x"));
    }

    // ---- The daily reminder ---------------------------------------------------------

    private static readonly EmailOptions Smtp = new() { Host = "smtp.example.com", From = "apply@example.com" };

    private static PacketReadyNotifier Notifier(CapturingNotifier telegram, CapturingEmailSender mail) =>
        new(telegram, new ConfigurationBuilder().Build(), NullLogger<PacketReadyNotifier>.Instance,
            new EmailNotifier(mail, Smtp, NullLogger<EmailNotifier>.Instance));

    private static Task<int> RemindAsync(NpgsqlConnection conn, CapturingNotifier telegram, CapturingEmailSender mail, DateTimeOffset now) =>
        Reminders.SendDueAsync(conn, Notifier(telegram, mail), AgentWorkerTests.Protector, NullLoggerFactory.Instance,
            "https://apply.example", now);

    private static async Task<(NpgsqlConnection Conn, long T, string Address)> ReminderTenantAsync(PostgresFixture pg, bool email = true, bool telegram = true)
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var address = TestAuth.UniqueEmail();
        var t = await TestAuth.EnsureUserAsync(conn, address);
        var settings = new NotificationSettingsRepo(conn, t, AgentWorkerTests.Protector, NullLogger<NotificationSettingsRepo>.Instance);
        await settings.UpsertPreferencesAsync(email);
        await settings.UpsertAsync(telegram, "4242", true, AgentWorkerTests.BotToken);
        await settings.UpsertReminderHourAsync(9);
        return (conn, t, address);
    }

    private static Task AppAsync(NpgsqlConnection conn, long t, string name, string status, string followup) =>
        conn.ExecuteAsync(
            "INSERT INTO applications (tenant_id, name, company, role, status, followup) VALUES (@t, @name, 'Acme', @name, @status, @followup)",
            new { t, name, status, followup });

    private static readonly DateTimeOffset Morning = new(2026, 9, 26, 9, 5, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_reminder_goes_once_a_day_on_every_channel_with_todays_follow_ups_and_the_next_days_interviews()
    {
        var (conn, t, address) = await ReminderTenantAsync(_pg);
        await using var c = conn;
        await AppAsync(conn, t, "today.md", "applied", "2026-09-26");
        await AppAsync(conn, t, "late.md", "screen", "2026-09-20");
        await AppAsync(conn, t, "later.md", "applied", "2026-09-27");
        await AppAsync(conn, t, "over.md", "rejected", "2026-09-26");
        var events = new AppEventRepo(conn, t);
        await events.AddAsync("today.md", new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc), "Phone screen");
        await events.AddAsync("today.md", new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc), "Too far");

        var telegram = new CapturingNotifier();
        var mail = new CapturingEmailSender();
        // Before the tenant's hour, nothing.
        await RemindAsync(conn, telegram, mail, Morning.AddHours(-1));
        Assert.DoesNotContain(telegram.Sent, s => s.Text.Contains("today.md"));

        await RemindAsync(conn, telegram, mail, Morning);
        var sent = Assert.Single(telegram.Sent, s => s.Text.Contains("today.md"));
        Assert.Contains("Follow-ups due today:", sent.Text);
        Assert.Contains("Phone screen", sent.Text);
        Assert.DoesNotContain("Too far", sent.Text);
        Assert.Contains("late.md — was due 2026-09-20", sent.Text);
        Assert.DoesNotContain("later.md", sent.Text);
        Assert.DoesNotContain("over.md", sent.Text);
        Assert.Contains("https://apply.example/#app=today.md", sent.Text);
        var email = Assert.Single(mail.Messages, m => m.To == address);
        Assert.Equal("Due today: 1 interview, 1 follow-up", email.Subject);

        // Again the same day: claimed, so nothing more.
        await RemindAsync(conn, telegram, mail, Morning.AddHours(3));
        Assert.Single(telegram.Sent, s => s.Text.Contains("today.md"));
        Assert.Single(mail.Messages, m => m.To == address);
    }

    [Fact]
    public async Task No_reminder_when_only_overdue_ones_remain_or_the_toggle_is_off()
    {
        var (conn, t, address) = await ReminderTenantAsync(_pg, telegram: false);
        await using var c = conn;
        await AppAsync(conn, t, "stale.md", "applied", "2026-09-01");
        var mail = new CapturingEmailSender();
        await RemindAsync(conn, new CapturingNotifier(), mail, Morning);
        Assert.DoesNotContain(mail.Messages, m => m.To == address);

        var (off, o, offAddress) = await ReminderTenantAsync(_pg, telegram: false);
        await using var c2 = off;
        await AppAsync(off, o, "due.md", "applied", "2026-09-26");
        await new NotificationSettingsRepo(off, o, AgentWorkerTests.Protector, NullLogger<NotificationSettingsRepo>.Instance)
            .UpsertPreferencesAsync(null, followupDue: false);
        await RemindAsync(off, new CapturingNotifier(), mail, Morning);
        Assert.DoesNotContain(mail.Messages, m => m.To == offAddress);
    }

    [Fact]
    public async Task A_day_with_nothing_due_stays_unclaimed_so_a_follow_up_added_later_still_goes()
    {
        var (conn, t, address) = await ReminderTenantAsync(_pg, telegram: false);
        await using var c = conn;
        var mail = new CapturingEmailSender();
        await RemindAsync(conn, new CapturingNotifier(), mail, Morning);
        Assert.DoesNotContain(mail.Messages, m => m.To == address);

        await AppAsync(conn, t, "added.md", "applied", "2026-09-26");
        await RemindAsync(conn, new CapturingNotifier(), mail, Morning.AddHours(4));
        var sent = Assert.Single(mail.Messages, m => m.To == address);
        Assert.Contains("added.md", sent.TextBody);
    }

    [Fact]
    public void The_message_stays_under_telegrams_limit_whatever_the_notes()
    {
        var interviews = Enumerable.Range(1, 40)
            .Select(i => new DueInterview(i, $"a{i}.md", new string('C', 250), new string('R', 250),
                new DateTime(2026, 9, 26, 15, 0, 0, DateTimeKind.Utc), new string('n', 2000))).ToList();
        var (_, text) = Reminders.Message(new DueList("2026-09-26", [], interviews), "https://apply.example");
        Assert.True(text.Length <= Reminders.MaxChars, $"{text.Length} chars");
        Assert.DoesNotContain(new string('n', Reminders.MaxNoteChars + 1), text);
        Assert.EndsWith("to stop hearing about it.", text);
    }

    [Fact]
    public void The_message_counts_what_it_does_not_list()
    {
        var many = Enumerable.Range(1, Reminders.MaxListed + 3)
            .Select(i => new DueFollowup(i, $"a{i}.md", "Acme", "Eng", "applied", "2026-09-26", false)).ToList();
        var (subject, text) = Reminders.Message(new DueList("2026-09-26", many, []), "");
        Assert.Equal($"Due today: {Reminders.MaxListed + 3} follow-ups", subject);
        Assert.Contains("…and 3 more.", text);
        Assert.DoesNotContain("#app=", text);
        Assert.False(Reminders.Worth(new DueList("2026-09-26",
            [new DueFollowup(1, "x.md", "X", "", "applied", "2026-09-01", true)], [])));
    }
}
