// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Notifications;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Email as a notification channel (#355): the moo fans out to every channel the tenant
/// has on, goes to the account's own address, answers to the per-event toggles, and a
/// failure on one channel neither stops the other nor escapes to the caller.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EmailNotifierTests(PostgresFixture pg)
{
    private static readonly EmailOptions Smtp = new() { Host = "smtp.example.com", From = "apply@example.com" };

    private static EmailNotifier Email(CapturingEmailSender sender, EmailOptions? options = null) =>
        new(sender, options ?? Smtp, NullLogger<EmailNotifier>.Instance);

    private static PacketReadyNotifier Notifier(CapturingNotifier telegram, EmailNotifier? email)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicBaseUrl"] = "https://apply.example" })
            .Build();
        return new PacketReadyNotifier(telegram, config, NullLogger<PacketReadyNotifier>.Instance, email);
    }

    private async Task<(NpgsqlConnection Conn, long Tenant, string Address, NotificationSettingsRepo Settings)> TenantAsync(
        bool telegram, bool email)
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var address = TestAuth.UniqueEmail();
        var t = await TestAuth.EnsureUserAsync(conn, address);
        var settings = new NotificationSettingsRepo(conn, t, AgentWorkerTests.Protector, NullLogger<NotificationSettingsRepo>.Instance);
        if (telegram) await settings.UpsertAsync(true, "4242", true, AgentWorkerTests.BotToken);
        if (email) await settings.UpsertPreferencesAsync(true);
        return (conn, t, address, settings);
    }

    private static async Task<List<(string Kind, string Channel)>> EventsAsync(NpgsqlConnection conn, long t) =>
        (await conn.QueryAsync<(string, string)>(
            "SELECT kind, coalesce(detail->>'channel', '') FROM agent_events WHERE tenant_id = @t ORDER BY id", new { t })).ToList();

    private static Task Notify(PacketReadyNotifier n, NpgsqlConnection conn, long t, NotificationSettingsRepo s,
        PacketReadyNotifier.Moment moment, string detail = "") =>
        n.NotifyAsync(s, new AgentPacketRepo(conn, t, AgentWorkerTests.Protector), new AgentEventRepo(conn, t),
            "acme-engineer.md", "Acme", "Engineer", CancellationToken.None, moment, detail);

    [Fact]
    public async Task The_moo_fans_out_to_telegram_and_to_the_accounts_own_address()
    {
        var (conn, t, address, settings) = await TenantAsync(telegram: true, email: true);
        await using var _ = conn;
        var telegram = new CapturingNotifier();
        var mail = new CapturingEmailSender();

        await Notify(Notifier(telegram, Email(mail)), conn, t, settings, PacketReadyNotifier.Moment.Code, "ada@example.com");

        Assert.Contains("reply here", Assert.Single(telegram.Sent).Text);
        var sent = Assert.Single(mail.Messages);
        Assert.Equal(address, sent.To);
        Assert.Equal("Security code needed: Acme · Engineer", sent.Subject);
        // Nobody reads a reply to the mail, so it never asks for one.
        Assert.DoesNotContain("reply here", sent.TextBody);
        Assert.Contains("paste it in the app", sent.TextBody);
        Assert.Contains("https://apply.example/#app=acme-engineer.md", sent.TextBody);
        Assert.Equal([(PacketReadyNotifier.SentEvent, "telegram"), (PacketReadyNotifier.SentEvent, "email")], await EventsAsync(conn, t));
    }

    [Fact]
    public async Task Email_alone_is_enough_and_telegram_is_never_needed()
    {
        var (conn, t, address, settings) = await TenantAsync(telegram: false, email: true);
        await using var _ = conn;
        var telegram = new CapturingNotifier();
        var mail = new CapturingEmailSender();

        await Notify(Notifier(telegram, Email(mail)), conn, t, settings, PacketReadyNotifier.Moment.SubmitFailed, "the board said no");

        Assert.Empty(telegram.Sent);
        var sent = Assert.Single(mail.Messages);
        Assert.Equal(address, sent.To);
        Assert.Equal("Submission failed: Acme · Engineer", sent.Subject);
        Assert.Contains("did not go through — the board said no", sent.TextBody);
    }

    [Fact]
    public async Task A_toggled_off_event_goes_nowhere_and_the_others_still_go()
    {
        var (conn, t, _, settings) = await TenantAsync(telegram: true, email: true);
        await using var _ = conn;
        await settings.UpsertPreferencesAsync(null, submitFailed: false);
        var telegram = new CapturingNotifier();
        var mail = new CapturingEmailSender();
        var n = Notifier(telegram, Email(mail));

        await Notify(n, conn, t, settings, PacketReadyNotifier.Moment.SubmitFailed, "nope");
        Assert.Empty(telegram.Sent);
        Assert.Empty(mail.Messages);

        await Notify(n, conn, t, settings, PacketReadyNotifier.Moment.Submitted);
        Assert.Single(telegram.Sent);
        Assert.Single(mail.Messages);
        // The email switch was left alone by the toggle's save.
        Assert.True((await settings.GetViewAsync()).EmailEnabled);
    }

    [Fact]
    public async Task A_failed_mail_is_recorded_and_telegram_still_goes()
    {
        var (conn, t, _, settings) = await TenantAsync(telegram: true, email: true);
        await using var _ = conn;
        var telegram = new CapturingNotifier();
        var mail = new CapturingEmailSender(new InvalidOperationException("550 relay denied"));

        await Notify(Notifier(telegram, Email(mail)), conn, t, settings, PacketReadyNotifier.Moment.Submitted);

        Assert.Single(telegram.Sent);
        Assert.Equal([(PacketReadyNotifier.SentEvent, "telegram"), (PacketReadyNotifier.FailedEvent, "email")], await EventsAsync(conn, t));
        var reason = await conn.ExecuteScalarAsync<string>(
            "SELECT detail->>'reason' FROM agent_events WHERE tenant_id = @t AND kind = @k", new { t, k = PacketReadyNotifier.FailedEvent });
        Assert.Contains("550 relay denied", reason);
    }

    [Fact]
    public async Task Email_on_with_no_smtp_on_this_container_says_so_in_the_events()
    {
        // The agent container needs Email__* too; when it has none the moo must not vanish.
        var (conn, t, _, settings) = await TenantAsync(telegram: false, email: true);
        await using var _ = conn;
        var mail = new CapturingEmailSender();

        await Notify(Notifier(new CapturingNotifier(), Email(mail, new EmailOptions())), conn, t, settings, PacketReadyNotifier.Moment.Submitted);

        Assert.Empty(mail.Messages);
        var reason = await conn.ExecuteScalarAsync<string>(
            "SELECT detail->>'reason' FROM agent_events WHERE tenant_id = @t AND kind = @k", new { t, k = PacketReadyNotifier.FailedEvent });
        Assert.Contains("Email__Host", reason);
    }

    [Fact]
    public async Task The_plain_moo_reaches_email_too()
    {
        var (conn, t, address, settings) = await TenantAsync(telegram: false, email: true);
        await using var _ = conn;
        var mail = new CapturingEmailSender();

        Assert.True(await Notifier(new CapturingNotifier(), Email(mail)).SendAsync(settings, "Check your LinkedIn app", CancellationToken.None,
            PacketReadyNotifier.Kind.SecurityCode, "LinkedIn wants you to confirm the sign-in"));

        var sent = Assert.Single(mail.Messages);
        Assert.Equal(address, sent.To);
        Assert.StartsWith("Check your LinkedIn app", sent.TextBody);
    }

    [Fact]
    public async Task Email_off_sends_no_mail_and_the_default_toggles_are_all_on()
    {
        var (conn, t, _, settings) = await TenantAsync(telegram: true, email: false);
        await using var _ = conn;
        var mail = new CapturingEmailSender();

        await Notify(Notifier(new CapturingNotifier(), Email(mail)), conn, t, settings, PacketReadyNotifier.Moment.Submitted);

        Assert.Empty(mail.Messages);
        Assert.Equal(new NotificationEvents(true, true, true), (await settings.GetViewAsync()).Events);
    }

    [Fact]
    public async Task The_notifier_refuses_cleanly_without_smtp_and_wraps_a_relay_failure()
    {
        var off = Email(new CapturingEmailSender(), new EmailOptions());
        Assert.False(off.Available);
        var ex = await Assert.ThrowsAsync<NotificationFailedException>(() => off.SendAsync("ada@example.com", "s", "t"));
        Assert.Contains("Email__Host", ex.Message);

        var refused = Email(new CapturingEmailSender(new InvalidOperationException("535 auth failed\nsecond line")));
        ex = await Assert.ThrowsAsync<NotificationFailedException>(() => refused.SendAsync("ada@example.com", "s", "t"));
        Assert.Contains("535 auth failed", ex.Message);
        Assert.DoesNotContain("second line", ex.Message);
    }

    [Fact]
    public void Subjects_say_what_happened_then_to_which_application()
    {
        Assert.Equal("Ready to submit: Acme · Engineer", PacketReadyNotifier.BuildSubject("Acme", "Engineer"));
        Assert.Equal("Filled in, needs you: Acme", PacketReadyNotifier.BuildSubject("Acme", "", PacketReadyNotifier.Moment.Filled, "notice period"));
        Assert.Equal("Filled in, ready to click Apply: an application", PacketReadyNotifier.BuildSubject("", "", PacketReadyNotifier.Moment.Filled));
        Assert.Equal("Sign-in code needed: Acme", PacketReadyNotifier.BuildSubject("Acme", "", PacketReadyNotifier.Moment.SignIn));
        Assert.Equal(PacketReadyNotifier.Kind.SecurityCode, PacketReadyNotifier.KindOf(PacketReadyNotifier.Moment.SignIn));
        Assert.Equal(PacketReadyNotifier.Kind.PacketReady, PacketReadyNotifier.KindOf(PacketReadyNotifier.Moment.Submitted));
        Assert.Equal(PacketReadyNotifier.Kind.SubmitFailed, PacketReadyNotifier.KindOf(PacketReadyNotifier.Moment.SubmitFailed));
    }
}
