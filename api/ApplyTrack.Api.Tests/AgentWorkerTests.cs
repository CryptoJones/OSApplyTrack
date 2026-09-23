// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text.Json;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Agent.Greenhouse;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Endpoints;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Materials;
using ApplyTrack.Api.Notifications;
using ApplyTrack.Api.Scrape;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// One unattended pass of the worker against the test Postgres: it judges only the
/// tenants that opted in, only leads over the agent's own bar, at most the per-run
/// cap, never the same lead twice; a `proceed` becomes a packet in `ready` and one
/// moo; a `skip` touches nothing but the audit trail.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AgentWorkerTests(PostgresFixture pg)
{
    internal const string BotToken = "123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef";
    internal static readonly SecretProtector Protector = new("test-master-key");

    internal static AgentWorker NewWorker(
        StubLlmClient stub, string connectionString, CapturingNotifier notifier, LlmOptions? llm = null,
        BrowserOptions? browser = null, JobPageFetcher? fetcher = null,
        IBrowserSubmitter? submitter = null, ISecurityCodeSource? codes = null, INotifier? telegram = null,
        AgentOptions? options = null, IPortalSessionRenewer? portal = null, ILinkedInSessionRenewer? linkedin = null,
        IAccountCreator? accountCreator = null)
    {
        var evaluator = new LeadEvaluator(
            new FitJudge(new StructuredCompleter(stub)), fetcher ?? new JobPageFetcher(),
            NullLogger<LeadEvaluator>.Instance);
        var greenhouse = new GreenhouseBoard(
            new StubHttpClientFactory(CapturingHandler.Always(HttpStatusCode.NotFound, "{}")),
            NullLogger<GreenhouseBoard>.Instance);
        var browserOptions = browser ?? new BrowserOptions();
        var builder = new PacketBuilder(greenhouse, new AnswerDrafter(new StructuredCompleter(stub)),
            new CoverLetterDrafter(stub), evaluator, browserOptions,
            new FormDiscoverer(browserOptions, NullLogger<FormDiscoverer>.Instance), NullLogger<PacketBuilder>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicBaseUrl"] = "https://apply.example" })
            .Build();
        var ready = new PacketReadyNotifier(notifier, config, NullLogger<PacketReadyNotifier>.Instance);
        return new AgentWorker(
            connectionString, options ?? new AgentOptions { Enabled = true },
            llm ?? new LlmOptions { BaseUrl = "http://stub/v1", Model = "stub-model" },
            Protector, evaluator, builder, ready, browserOptions,
            submitter ?? new BrowserSubmitter(browserOptions, NullLogger<BrowserSubmitter>.Instance), NullLoggerFactory.Instance,
            codes, telegram, portal, linkedin, accountCreator: accountCreator);
    }

    private async Task<(NpgsqlConnection Conn, long Tenant)> SeedTenantAsync(bool enabled, bool telegram = true, bool allowed = true)
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var t = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        if (!allowed) await TestAuth.DisallowAgentAsync(conn, t);
        await new AgentSettingsRepo(conn, t).UpsertAsync(new AgentSettings
        {
            Enabled = enabled, MinFitScore = 70, MaxPerRun = 2, MaxPerDay = 3, Phone = "555-0100",
        });
        await new ResumeRepo(conn, t, Protector).UpsertAsync(new Resume { FullName = "Ada Byte", Summary = "Ships .NET." });
        if (telegram)
            await new NotificationSettingsRepo(conn, t, Protector, NullLogger<NotificationSettingsRepo>.Instance)
                .UpsertAsync(true, "4242", true, BotToken);
        var apps = new ApplicationRepo(conn, t);
        await apps.CreateAsync(new AppFields { Company = "High", Role = "Engineer", Score = "90" });
        await apps.CreateAsync(new AppFields { Company = "Mid", Role = "Engineer", Score = "75" });
        await apps.CreateAsync(new AppFields { Company = "Low", Role = "Engineer", Score = "60" });
        await apps.CreateAsync(new AppFields { Company = "Junk", Role = "Engineer", Score = "high" });
        await apps.CreateAsync(new AppFields { Company = "Applied", Role = "Engineer", Score = "95", Status = "applied" });
        return (conn, t);
    }

    /// <summary>A tenant with a MyGreenhouse board account and, unless told otherwise, a mailbox (#221).</summary>
    private async Task<(NpgsqlConnection Conn, long Tenant)> SeedPortalTenantAsync(bool mailbox = true)
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var t = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        await new BoardAccountRepo(conn, t, Protector, NullLogger<BoardAccountRepo>.Instance).UpsertAsync("greenhouse.io", "ada@example.com", null);
        if (mailbox)
            await new MailboxSettingsRepo(conn, t, Protector, NullLogger<MailboxSettingsRepo>.Instance)
                .UpsertAsync(true, "imap.example.com", 993, "ada@example.com", true, "app-pw");
        return (conn, t);
    }

    private static Task<(string Cipher, DateTime? Expires)> PortalRowAsync(NpgsqlConnection conn, long t) =>
        conn.QuerySingleAsync<(string, DateTime?)>(
            "SELECT session_ciphertext, session_expires_at FROM board_accounts WHERE tenant_id = @t AND host = 'greenhouse.io'", new { t });

    [Fact]
    public async Task A_portal_account_without_a_session_is_signed_in_once_and_the_session_kept_sealed()
    {
        var (conn, t) = await SeedPortalTenantAsync();
        await using var _ = conn;
        var renewer = new FakePortalRenewer(_ => new PortalSession("sess-fresh", DateTimeOffset.UtcNow.AddDays(13)));
        var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(),
            codes: new FakeCodeSource(() => null), portal: renewer);

        Assert.Equal(1, await worker.RenewPortalSessionsAsync(CancellationToken.None));
        Assert.Equal(["ada@example.com"], renewer.Emails);
        var (cipher, expires) = await PortalRowAsync(conn, t);
        Assert.Equal("sess-fresh", Protector.Unprotect(cipher));
        Assert.NotNull(expires);
        Assert.InRange(expires!.Value, DateTime.UtcNow.AddDays(12), DateTime.UtcNow.AddDays(14));
        Assert.Contains(("", AgentEventRepo.Kinds.PortalSession), await EventsAsync(conn, t));
        // Fresh now: nothing is due, and the account is not signed in again.
        Assert.Equal(0, await worker.RenewPortalSessionsAsync(CancellationToken.None));
        Assert.Single(renewer.Emails);
    }

    [Fact]
    public async Task A_failed_renewal_is_recorded_and_not_retried_within_the_hour()
    {
        var (conn, t) = await SeedPortalTenantAsync();
        await using var _ = conn;
        var renewer = new FakePortalRenewer(_ => throw new PortalRenewalException("no security code arrived in the mailbox"));
        var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(),
            codes: new FakeCodeSource(() => null), portal: renewer);

        Assert.Equal(0, await worker.RenewPortalSessionsAsync(CancellationToken.None));
        Assert.Equal(0, await worker.RenewPortalSessionsAsync(CancellationToken.None));
        Assert.Single(renewer.Emails);   // one try; the next waits for PortalRetry
        var (cipher, _) = await PortalRowAsync(conn, t);
        Assert.Equal("", cipher);
        var reasons = (await conn.QueryAsync<string>(
            "SELECT detail->>'reason' FROM agent_events WHERE tenant_id = @t AND kind = 'error'", new { t })).ToList();
        Assert.Contains(reasons, r => r.Contains("no security code arrived"));
    }

    [Fact]
    public async Task Without_a_mailbox_the_renewal_says_so_and_never_asks_the_portal_for_a_code()
    {
        var (conn, t) = await SeedPortalTenantAsync(mailbox: false);
        await using var _ = conn;
        var renewer = new FakePortalRenewer(_ => new PortalSession("never", DateTimeOffset.UtcNow));
        var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(),
            codes: new FakeCodeSource(() => null), portal: renewer);

        Assert.Equal(0, await worker.RenewPortalSessionsAsync(CancellationToken.None));
        Assert.Empty(renewer.Emails);
        var reasons = (await conn.QueryAsync<string>(
            "SELECT detail->>'reason' FROM agent_events WHERE tenant_id = @t AND kind = 'error'", new { t })).ToList();
        Assert.Contains(reasons, r => r.Contains("Settings · Notifications"));
    }

    // A tenant with a LinkedIn account (and no MyGreenhouse one: the renewal query is
    // cross-tenant, so a stray portal row here would show up in the portal tests above).
    private async Task<(NpgsqlConnection Conn, long Tenant)> SeedLinkedInTenantAsync(string? password, bool mailbox = true)
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var t = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        await new BoardAccountRepo(conn, t, Protector, NullLogger<BoardAccountRepo>.Instance).UpsertAsync("linkedin.com", "ada@example.com", password);
        if (mailbox)
            await new MailboxSettingsRepo(conn, t, Protector, NullLogger<MailboxSettingsRepo>.Instance)
                .UpsertAsync(true, "imap.example.com", 993, "ada@example.com", true, "app-pw");
        return (conn, t);
    }

    [Fact]
    public async Task The_browser_is_handed_the_kept_linkedin_session_and_only_while_it_is_live()
    {
        // Easy Apply starts signed in from the session the renewer keeps (#278); the form-filling
        // side never saw it before — only the poller did.
        var (conn, t) = await SeedLinkedInTenantAsync("hunter2");
        await using var _ = conn;
        var accounts = new BoardAccountRepo(conn, t, Protector, NullLogger<BoardAccountRepo>.Instance);
        Assert.Equal("", Assert.Single(await accounts.TargetsAsync()).Session);

        var session = LinkedInSessionRenewer.Encode("AQED-token", "ajax:1");
        await accounts.SavePortalSessionAsync("linkedin.com", session, DateTimeOffset.UtcNow.AddDays(30));
        var live = Assert.Single(await accounts.TargetsAsync());
        Assert.Equal(session, live.Session);
        Assert.Equal("hunter2", live.Password);

        await accounts.SavePortalSessionAsync("linkedin.com", session, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal("", Assert.Single(await accounts.TargetsAsync()).Session);
    }

    [Fact]
    public async Task A_linkedin_account_with_a_password_is_signed_in_once_and_the_cookies_kept_sealed()
    {
        var (conn, t) = await SeedLinkedInTenantAsync("hunter2");
        await using var _ = conn;
        var session = LinkedInSessionRenewer.Encode("AQEDA-fresh", "ajax:1");
        var renewer = new FakeLinkedInRenewer(_ => new PortalSession(session, DateTimeOffset.UtcNow.AddDays(300)));
        var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(),
            codes: new FakeCodeSource(() => null), linkedin: renewer);

        // The renewal query is cross-tenant: other tests' due accounts may be signed in on the same call.
        Assert.True(await worker.RenewLinkedInSessionsAsync(CancellationToken.None) >= 1);
        Assert.Contains(("ada@example.com", "hunter2", true), renewer.Attempts);   // the mailbox is offered for a PIN
        var attempts = renewer.Attempts.Count;
        var (cipher, expires) = await conn.QuerySingleAsync<(string, DateTime?)>(
            "SELECT session_ciphertext, session_expires_at FROM board_accounts WHERE tenant_id = @t AND host = 'linkedin.com'", new { t });
        Assert.Equal(session, Protector.Unprotect(cipher));
        Assert.InRange(expires!.Value, DateTime.UtcNow.AddDays(299), DateTime.UtcNow.AddDays(301));
        Assert.Contains(("", AgentEventRepo.Kinds.PortalSession), await EventsAsync(conn, t));
        // Fresh now: nothing is due, and the account is not signed in again.
        Assert.Equal(0, await worker.RenewLinkedInSessionsAsync(CancellationToken.None));
        Assert.Equal(attempts, renewer.Attempts.Count);
    }

    [Fact]
    public async Task A_linkedin_account_without_a_password_is_reported_and_never_tried()
    {
        var (conn, t) = await SeedLinkedInTenantAsync(null, mailbox: false);
        await using var _ = conn;
        var renewer = new FakeLinkedInRenewer(_ => throw new PortalRenewalException("never"));
        var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(), linkedin: renewer);

        Assert.Equal(0, await worker.RenewLinkedInSessionsAsync(CancellationToken.None));
        Assert.Empty(renewer.Attempts);
        var reasons = (await conn.QueryAsync<string>(
            "SELECT detail->>'reason' FROM agent_events WHERE tenant_id = @t AND kind = 'error'", new { t })).ToList();
        Assert.Contains(reasons, r => r.Contains("no password"));
    }

    [Fact]
    public async Task A_linkedin_renewal_that_stopped_on_the_app_tap_is_recorded_and_not_retried_at_once()
    {
        var (conn, t) = await SeedLinkedInTenantAsync("hunter2", mailbox: false);
        await using var _ = conn;
        var renewer = new FakeLinkedInRenewer(_ => throw new PortalRenewalException("LinkedIn waited 240 s for the tap in the LinkedIn app and none came"));
        var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(), linkedin: renewer);

        Assert.Equal(0, await worker.RenewLinkedInSessionsAsync(CancellationToken.None));
        Assert.Equal(0, await worker.RenewLinkedInSessionsAsync(CancellationToken.None));
        Assert.Single(renewer.Attempts);
        Assert.False(renewer.Attempts[0].Mailbox);   // no mailbox: no PIN reader offered
        var reasons = (await conn.QueryAsync<string>(
            "SELECT detail->>'reason' FROM agent_events WHERE tenant_id = @t AND kind = 'error'", new { t })).ToList();
        Assert.Contains(reasons, r => r.Contains("tap in the LinkedIn app"));
    }

    [Fact]
    public async Task A_sign_in_now_is_taken_at_once_despite_the_six_hour_wait_and_only_once()
    {
        // The tap in the LinkedIn app has to come within minutes of the sign-in, and the pass's
        // own clock puts it at no hour anyone chose: every attempt from 2026-09-21 to 09-22 ran
        // out unanswered. Sign in now (POST /api/board-accounts/{host}/renew) runs it when the
        // person has their phone; a request is taken once, so a try that fails waits to be asked.
        var (conn, t) = await SeedLinkedInTenantAsync("hunter2", mailbox: false);
        await using var _ = conn;
        var renewer = new FakeLinkedInRenewer(_ => throw new PortalRenewalException("LinkedIn waited 240 s for the tap in the LinkedIn app and none came"));
        var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(), linkedin: renewer);
        var accounts = new BoardAccountRepo(conn, t, Protector, NullLogger<BoardAccountRepo>.Instance);

        Assert.Equal(0, await worker.RenewLinkedInSessionsAsync(CancellationToken.None));
        Assert.Equal(0, await worker.RenewLinkedInSessionsAsync(CancellationToken.None));
        Assert.Single(renewer.Attempts);

        Assert.True(await accounts.RequestRenewalAsync("www.linkedin.com"));
        Assert.NotNull(Assert.Single(await accounts.ListAsync()).RenewRequestedAt);
        Assert.Equal(0, await worker.RenewLinkedInSessionsAsync(CancellationToken.None));
        Assert.Equal(2, renewer.Attempts.Count);
        // Taken: the row is clear and the next pass waits again.
        Assert.Null(Assert.Single(await accounts.ListAsync()).RenewRequestedAt);
        Assert.Equal(0, await worker.RenewLinkedInSessionsAsync(CancellationToken.None));
        Assert.Equal(2, renewer.Attempts.Count);
        // Only a signed-in source's account keeps a session to renew.
        await accounts.UpsertAsync("career4.successfactors.com", "ada@example.com", "pw");
        await Assert.ThrowsAsync<AppValidationException>(() => accounts.RequestRenewalAsync("career4.successfactors.com"));
        Assert.False(await accounts.RequestRenewalAsync("app.joinhandshake.com"));   // no account saved there
    }

    [Fact]
    public async Task An_easy_apply_run_with_no_kept_linkedin_session_stands_down_and_goes_again_once_one_is_kept()
    {
        // Signed out, LinkedIn serves the posting with "Sign in" and "Join now" and nothing can be
        // attempted: twenty-five packets each spent a browser trip and a retry finding that out.
        // Now the run stands down before the browser, the reconciler leaves it alone (a retry
        // would stand down the same way, and it is not a failure of the packet's), and the
        // renewal that keeps a session queues it again itself — including runs from before the
        // rule, which said it in words.
        var (conn, t, _) = await ReadyTenantAsync(dryRun: true);
        await using var _ = conn;
        await conn.ExecuteAsync("UPDATE agent_settings SET linkedin_easy = true WHERE tenant_id = @t", new { t });
        var apps = new ApplicationRepo(conn, t);
        foreach (var (name, id) in new[] { ("high-engineer.md", "4468125457"), ("mid-engineer.md", "4468125458") })
        {
            var rec = await apps.GetAsync(name);
            await apps.UpdateStructuredAsync(name, rec!.Fields with { Link = $"https://www.linkedin.com/jobs/view/{id}", Source = AtsProvider.LinkedInEasySource }, null);
        }
        var accounts = new BoardAccountRepo(conn, t, Protector, NullLogger<BoardAccountRepo>.Instance);
        await accounts.UpsertAsync("linkedin.com", "ada-easy@example.com", "hunter2");
        // mid: a run from before the rule, which found the signed-out page in the browser.
        await new AgentEvidenceRepo(conn, t, Protector).RecordAsync("mid-engineer.md", AgentEvidenceRepo.Kinds.Failed,
            "https://www.linkedin.com/jobs/view/4468125458", "",
            new { error = "LinkedIn showed its signed-out page — the kept session is not valid in the browser; it is renewed on the next pass, and nothing was attempted", dry_run = true, mapped = Array.Empty<string>(), unmapped = Array.Empty<string>() }, null);
        await conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE done_at IS NULL AND tenant_id <> @t", new { t });
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true);
        var fake = new FakeSubmitter((link, _, _, _, _) => Task.FromResult(FakeSubmitter.Clean(link)));
        var session = LinkedInSessionRenewer.Encode("AQEDA-fresh", "ajax:1");
        var renewer = new FakeLinkedInRenewer(_ => new PortalSession(session, DateTimeOffset.UtcNow.AddDays(300)));
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(),
            browser: FakeBrowser, submitter: fake, linkedin: renewer);

        Assert.Equal(1, await worker.DrainSubmitsAsync(CancellationToken.None));

        Assert.Empty(fake.Runs);   // the browser was never opened
        var last = (await EvidenceAsync(conn, t, "high-engineer.md")).Last();
        Assert.Equal("failed", last.Kind);
        var detail = JsonDocument.Parse(last.Detail).RootElement;
        Assert.Equal("linkedin.com", detail.GetProperty("awaiting_session").GetString());
        Assert.Contains("Sign in now", detail.GetProperty("reason").GetString());
        Assert.True(ReadyReconciler.WaitedOnSession("failed", detail, "www.linkedin.com"));
        Assert.False(ReadyReconciler.IsTransientFailure("failed", detail));
        // The reconciler: not on its clock, and not counted against the packet.
        var healed = await ReadyReconciler.ReconcileAsync(conn, t, Protector, longTail: true, linkedInEasy: true);
        Assert.Equal(0, healed.Count);
        Assert.Equal(0, await PendingSubmitsAsync(conn, t));
        var errors = await new AgentEvidenceRepo(conn, t, Protector).ErroredAsync(ReadyReconciler.Window);
        Assert.Equal(0, errors.Single(e => e.ApplicationName == "high-engineer.md").RecentFailures);
        Assert.Contains("Sign in now", ErrorsEndpoints.Describe(errors.Single(e => e.ApplicationName == "high-engineer.md"), longTail: true, linkedInEasy: true).Why);

        // The renewal keeps a session: both stood-down runs are queued again, as dry runs. (The
        // renewal query is cross-tenant, so other tests' due accounts may be signed in here too.)
        Assert.True(await worker.RenewLinkedInSessionsAsync(CancellationToken.None) >= 1);
        Assert.Contains(renewer.Attempts, a => a.User == "ada-easy@example.com");
        var queued = (await QueuedAsync(conn, t)).OrderBy(q => q.Name).ToList();
        Assert.Equal([("high-engineer.md", true, false), ("mid-engineer.md", true, false)], queued);
        Assert.Equal(2, (await EventsAsync(conn, t)).Count(e => e.Kind == AgentEventRepo.Kinds.Requeued));
        // And with the session kept, the run reaches the browser.
        Assert.Equal(2, await worker.DrainSubmitsAsync(CancellationToken.None));
        Assert.Equal(2, fake.Runs.Count);
    }

    private static async Task<List<(string Name, string Kind)>> EventsAsync(NpgsqlConnection conn, long t) =>
        (await conn.QueryAsync<(string, string)>(
            "SELECT application_name, kind FROM agent_events WHERE tenant_id = @t ORDER BY id", new { t })).ToList();

    [Fact]
    public async Task A_proceed_becomes_a_packet_in_ready_and_exactly_one_moo()
    {
        var stub = new StubLlmClient(Responders.Agent());
        var notifier = new CapturingNotifier();
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await using var _ = conn;
        using var worker = NewWorker(stub, pg.ConnectionString, notifier);

        await worker.RunOnceAsync(CancellationToken.None);

        // "Low" is under the bar, "Junk" has no comparable score, "Applied" is not a lead,
        // and MaxPerRun = 2 covers exactly High + Mid.
        var events = await EventsAsync(conn, t);
        Assert.Equal(["high-engineer.md", "mid-engineer.md"],
            events.Where(e => e.Kind == "verdict").Select(e => e.Name).ToArray());
        Assert.Equal(2, events.Count(e => e.Kind == PacketBuilder.PacketEvent));
        Assert.Equal(2, events.Count(e => e.Kind == PacketReadyNotifier.SentEvent));

        var statuses = await conn.QueryAsync<string>(
            "SELECT DISTINCT status FROM applications WHERE tenant_id = @t AND company IN ('High','Mid')", new { t });
        Assert.Equal(["ready"], statuses.ToArray());

        var packet = await new AgentPacketRepo(conn, t, Protector).GetAsync("high-engineer.md");
        Assert.NotNull(packet);
        Assert.Equal("unknown", packet!.Provider);
        Assert.Equal("Ada", packet.Answers["std:first_name"]);
        Assert.Equal("555-0100", packet.Answers["std:phone"]);
        Assert.Equal(StubLlmClient.DefaultBody, packet.Answers["std:cover_letter"]);
        Assert.NotNull(packet.NotifiedAt);

        Assert.Equal(2, notifier.Sent.Count);
        Assert.Equal(BotToken, notifier.Sent[0].BotToken);
        Assert.Equal("4242", notifier.Sent[0].ChatId);
        Assert.Contains("🐮 moo — High · Engineer is ready to submit", notifier.Sent[0].Text);
        Assert.Contains("https://apply.example/#app=high-engineer.md", notifier.Sent[0].Text);

        // A second pass finds nothing new: a verdict is terminal, and no second moo.
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, (await EventsAsync(conn, t)).Count(e => e.Kind == "verdict"));
        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact]
    public async Task A_posting_that_closed_by_judge_time_is_retired_before_any_packet_is_built()
    {
        // The latency fix: a proceed verdict on a lead whose apply page already reads
        // "no longer open" is retired to `passed` by a cheap GET, before the packet build
        // spends any LLM tokens. A still-open lead in the same pass is unaffected — it
        // builds its packet as usual, proving the pre-check is gated on the closed signal
        // (and on the link being present), not on there being a link at all.
        var stub = new StubLlmClient(Responders.Agent());
        var notifier = new CapturingNotifier();
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await using var _ = conn;

        // Unknown-provider links (not an ATS the packet builder tries to call): the
        // pre-check still fetches and detects closure on any provider, and the open lead
        // then builds its packet through the standard-question path.
        var apps = new ApplicationRepo(conn, t);
        var high = await apps.GetAsync("high-engineer.md");
        await apps.UpdateStructuredAsync("high-engineer.md",
            high!.Fields with { Link = "https://careers.example.com/acme/eng" }, null);
        var mid = await apps.GetAsync("mid-engineer.md");
        await apps.UpdateStructuredAsync("mid-engineer.md",
            mid!.Fields with { Link = "https://careers.example.com/globex/eng" }, null);

        // Closed for acme (High), a live form for everyone else (Mid).
        var handler = new CapturingHandler(req =>
        {
            var closed = (req.RequestUri?.AbsolutePath ?? "").Contains("acme");
            var body = closed
                ? "<html><body>The job you are looking for is no longer open</body></html>"
                : "<html><body><form>Apply<input name='email'></form></body></html>";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
            };
        });
        using var worker = NewWorker(stub, pg.ConnectionString, notifier,
            fetcher: new JobPageFetcher(handler));

        await worker.RunOnceAsync(CancellationToken.None);

        // High: retired to passed, no packet, an error row that says why.
        var highStatus = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM applications WHERE tenant_id = @t AND name = 'high-engineer.md'", new { t });
        Assert.Equal("passed", highStatus);
        Assert.Null(await new AgentPacketRepo(conn, t, Protector).GetAsync("high-engineer.md"));
        var reason = await conn.ExecuteScalarAsync<string>(
            "SELECT detail->>'reason' FROM agent_events WHERE tenant_id = @t"
            + " AND application_name = 'high-engineer.md' AND kind = 'error'", new { t });
        Assert.Contains("posting closed before packet build", reason);

        // Mid: still open, so it built its packet and moo'd as normal.
        Assert.NotNull(await new AgentPacketRepo(conn, t, Protector).GetAsync("mid-engineer.md"));
        var midStatus = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM applications WHERE tenant_id = @t AND name = 'mid-engineer.md'", new { t });
        Assert.Equal("ready", midStatus);
    }

    [Fact]
    public async Task A_skip_records_the_veto_and_stages_nothing()
    {
        var skip = "{\"decision\":\"skip\",\"confidence\":80,\"rationale\":\"Wrong stack.\",\"concerns\":[]}";
        var stub = new StubLlmClient(Responders.Agent(verdictJson: skip));
        var notifier = new CapturingNotifier();
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await using var _ = conn;
        using var worker = NewWorker(stub, pg.ConnectionString, notifier);

        await worker.RunOnceAsync(CancellationToken.None);

        var events = await EventsAsync(conn, t);
        Assert.All(events, e => Assert.Equal("verdict", e.Kind));
        Assert.Empty(notifier.Sent);
        var statuses = await conn.QueryAsync<string>(
            "SELECT DISTINCT status FROM applications WHERE tenant_id = @t AND company IN ('High','Mid')", new { t });
        Assert.Equal(["lead"], statuses.ToArray());
        Assert.Null(await new AgentPacketRepo(conn, t, Protector).GetAsync("high-engineer.md"));
    }

    [Fact]
    public async Task No_telegram_target_means_a_packet_but_no_moo()
    {
        var stub = new StubLlmClient(Responders.Agent());
        var notifier = new CapturingNotifier();
        var (conn, t) = await SeedTenantAsync(enabled: true, telegram: false);
        await using var _ = conn;
        using var worker = NewWorker(stub, pg.ConnectionString, notifier);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Empty(notifier.Sent);
        var packet = await new AgentPacketRepo(conn, t, Protector).GetAsync("high-engineer.md");
        Assert.NotNull(packet);
        Assert.Null(packet!.NotifiedAt); // the claim is only taken when there is a target
    }

    [Fact]
    public async Task A_tenant_that_has_not_opted_in_is_never_touched()
    {
        var stub = new StubLlmClient(Responders.Agent());
        var (conn, t) = await SeedTenantAsync(enabled: false);
        await using var _ = conn;
        using var worker = NewWorker(stub, pg.ConnectionString, new CapturingNotifier());

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Empty(await EventsAsync(conn, t));
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task A_tenant_without_a_model_gets_an_error_row_not_a_verdict()
    {
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await using var _ = conn;
        var stub = new StubLlmClient(Responders.Agent());
        using var worker = NewWorker(stub, pg.ConnectionString, new CapturingNotifier(), new LlmOptions());

        await worker.RunOnceAsync(CancellationToken.None);

        var events = await EventsAsync(conn, t);
        Assert.Single(events);
        Assert.Equal("error", events[0].Kind);
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task A_browser_crash_records_failed_evidence_instead_of_vanishing()
    {
        // The regression this locks: the catch in RunSubmitAsync named three exception
        // types, so anything else escaped to the drain loop's generic handler, which marks
        // the queue row done and writes NO evidence. On a live instance every submission
        // died on a Win32Exception out of fork/exec, matched none of the three, and left
        // no trace — a crashed submission was indistinguishable from one never requested.
        var stub = new StubLlmClient(Responders.Agent());
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await using var _ = conn;
        using (var builder = NewWorker(stub, pg.ConnectionString, new CapturingNotifier()))
            await builder.RunOnceAsync(CancellationToken.None);

        // A real public posting link, so the run gets PAST url validation (whose
        // AppValidationException the old filter already caught) and dies on the browser
        // itself — the part that used to vanish.
        var apps = new ApplicationRepo(conn, t);
        var rec = await apps.GetAsync("high-engineer.md");
        await apps.UpdateStructuredAsync("high-engineer.md",
            rec!.Fields with { Link = "https://boards.greenhouse.io/acme/jobs/1" }, null);
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true);
        // A malformed endpoint: the failure is a plain UriFormatException/ArgumentException
        // from the connect, which is exactly the shape the three-type filter missed.
        using var worker = NewWorker(stub, pg.ConnectionString, new CapturingNotifier(),
            browser: new BrowserOptions { Endpoint = "::::" });

        await worker.DrainSubmitsAsync(CancellationToken.None);

        var kind = await conn.ExecuteScalarAsync<string>(
            "SELECT kind FROM agent_evidence WHERE tenant_id = @t"
            + " AND application_name = 'high-engineer.md' ORDER BY id DESC LIMIT 1", new { t });
        Assert.Equal("failed", kind);
        var reason = await conn.ExecuteScalarAsync<string>(
            "SELECT detail->>'reason' FROM agent_evidence WHERE tenant_id = @t"
            + " AND application_name = 'high-engineer.md' ORDER BY id DESC LIMIT 1", new { t });
        // The type-name prefix is emitted only by the widened handler, so this also pins
        // that the failure went through it. Reproducing the exact Win32Exception from
        // fork/exec needs a broken node binary and is not unit-testable; the widened catch
        // is `is not OperationCanceledException`, so type no longer decides visibility.
        Assert.StartsWith("PlaywrightException: ", reason);
    }

    [Fact]
    public async Task A_dropped_submit_request_records_an_error_instead_of_vanishing()
    {
        // A dropped submission used to leave no trace anywhere the user can see: the
        // queue row still reads claimed+done and nothing reaches agent_evidence. That
        // silence is also the reason an application never got its "applied" flag, so
        // the drop has to say so.
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await using var _ = conn;
        // The queue is drained across tenants: settle what other tests left, so the count is this one's.
        await conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE done_at IS NULL AND tenant_id <> @t", new { t });
        await new SubmitRequestRepo(conn, t).EnqueueAsync("no-such-role.md", dryRun: true);
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString,
            new CapturingNotifier());

        var drained = await worker.DrainSubmitsAsync(CancellationToken.None);

        Assert.Equal(1, drained);
        var reason = await conn.ExecuteScalarAsync<string>(
            "SELECT detail->>'reason' FROM agent_events"
            + " WHERE tenant_id = @t AND kind = 'error' AND application_name = 'no-such-role.md'",
            new { t });
        Assert.Equal("submit dropped: application missing", reason);
    }

    // -- auto-submit promotion -------------------------------------------------
    //
    // A clean dry run re-queues itself for real. These lock the NEGATIVE half of that
    // rule, which is the half that matters: promotion must never fire off a run that
    // did not actually prove the form works. The positive path needs a live browser and
    // is covered by BrowserSubmitterTests plus the post-deploy check.

    private static async Task<int> PendingSubmitsAsync(NpgsqlConnection conn, long t) =>
        await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM submit_requests WHERE tenant_id = @t AND done_at IS NULL", new { t });

    [Fact]
    public async Task A_failed_dry_run_is_never_promoted_to_a_real_submission()
    {
        // The browser cannot connect, so the run records failed evidence. Nothing about
        // that proves the form is fillable, so it must not become a real application.
        var stub = new StubLlmClient(Responders.Agent());
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await using var _ = conn;
        await new AgentSettingsRepo(conn, t).UpsertAsync(new AgentSettings
        {
            Enabled = true, DryRun = false, MinFitScore = 70, MaxPerRun = 2, MaxPerDay = 3, Phone = "555-0100",
        });
        using (var builder = NewWorker(stub, pg.ConnectionString, new CapturingNotifier()))
            await builder.RunOnceAsync(CancellationToken.None);

        var apps = new ApplicationRepo(conn, t);
        var rec = await apps.GetAsync("high-engineer.md");
        await apps.UpdateStructuredAsync("high-engineer.md",
            rec!.Fields with { Link = "https://boards.greenhouse.io/acme/jobs/1" }, null);
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true);
        using var worker = NewWorker(stub, pg.ConnectionString, new CapturingNotifier(),
            browser: new BrowserOptions { Endpoint = "ws://127.0.0.1:1/" });

        await worker.DrainSubmitsAsync(CancellationToken.None);

        Assert.Equal(0, await PendingSubmitsAsync(conn, t));
        var kind = await conn.ExecuteScalarAsync<string>(
            "SELECT kind FROM agent_evidence WHERE tenant_id = @t ORDER BY id DESC LIMIT 1", new { t });
        Assert.Equal("failed", kind);
    }

    [Fact]
    public async Task A_tenant_still_in_dry_run_mode_is_never_promoted()
    {
        // DryRun = true is the opt-out. Even a flawless fill stays a rehearsal.
        var stub = new StubLlmClient(Responders.Agent());
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await using var _ = conn;
        using (var builder = NewWorker(stub, pg.ConnectionString, new CapturingNotifier()))
            await builder.RunOnceAsync(CancellationToken.None);

        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true);
        using var worker = NewWorker(stub, pg.ConnectionString, new CapturingNotifier(),
            browser: new BrowserOptions { Endpoint = "ws://127.0.0.1:1/" });

        await worker.DrainSubmitsAsync(CancellationToken.None);

        Assert.Equal(0, await PendingSubmitsAsync(conn, t));
    }

    [Fact]
    public async Task A_tenant_off_the_operators_allowlist_is_never_judged_and_its_queued_run_is_never_claimed()
    {
        var (conn, t) = await SeedTenantAsync(enabled: true, allowed: false);
        await using var _ = conn;
        var stub = new StubLlmClient(Responders.Agent());
        var worker = NewWorker(stub, pg.ConnectionString, new CapturingNotifier());

        // Enabled by the tenant, not allowed by the operator: the pass skips it entirely.
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, stub.Calls);
        Assert.Empty(await new AgentEventRepo(conn, t).ListRecentAsync(50));

        // A queued browser run for it is left alone too.
        var name = (await new ApplicationRepo(conn, t).ListAsync()).First().Filename;
        await new SubmitRequestRepo(conn, t).EnqueueAsync(name, dryRun: true);
        Assert.Null(await SubmitQueue.ClaimNextAsync(conn));
    }

    [Fact]
    public async Task A_pass_stamps_the_workers_heartbeat_and_the_api_side_reads_a_browser_from_it()
    {
        // The api container never has Browser__Endpoint on any shipped deployment shape;
        // what it can know is that a worker with one has been heard from (#159).
        var stub = new StubLlmClient(Responders.Agent());
        var (conn, _) = await SeedTenantAsync(enabled: false);
        await using var _ = conn;
        using (var noBrowser = NewWorker(stub, pg.ConnectionString, new CapturingNotifier()))
            await noBrowser.RunOnceAsync(CancellationToken.None);
        Assert.True(await AgentWorkerRegistry.WorkerSeenAsync(conn));
        Assert.False(await AgentWorkerRegistry.BrowserSeenAsync(conn));
        Assert.False(await new BrowserAvailability(new BrowserOptions(), conn).IsAvailableAsync());

        using (var withBrowser = NewWorker(stub, pg.ConnectionString, new CapturingNotifier(),
                   browser: new BrowserOptions { Endpoint = "ws://127.0.0.1:1/" }))
            await withBrowser.DrainSubmitsAsync(CancellationToken.None);
        Assert.True(await AgentWorkerRegistry.BrowserSeenAsync(conn));
        Assert.True(await new BrowserAvailability(new BrowserOptions(), conn).IsAvailableAsync());

        // Silence is absence: a stale heartbeat promises nothing.
        await conn.ExecuteAsync("UPDATE agent_workers SET seen_at = now() - interval '1 hour'");
        Assert.False(await AgentWorkerRegistry.BrowserSeenAsync(conn));
        Assert.False(await AgentWorkerRegistry.WorkerSeenAsync(conn));
        // …but when it was last heard from is still on record (#184).
        var seen = await AgentWorkerRegistry.LastSeenAsync(conn);
        Assert.NotNull(seen);
        Assert.True(seen < DateTimeOffset.UtcNow.AddMinutes(-50));
    }

    // -- the Ready lane: promotion after the flip, the queued prepare, the profile at fill time --

    private static readonly BrowserOptions FakeBrowser = new() { Endpoint = "ws://127.0.0.1:1/", AllowPrivateTargets = true };

    private static Task<string?> StatusAsync(NpgsqlConnection conn, long t, string name) =>
        conn.ExecuteScalarAsync<string?>("SELECT status FROM applications WHERE tenant_id = @t AND name = @n", new { t, n = name });

    private static async Task<List<(string Kind, string Detail)>> EvidenceAsync(NpgsqlConnection conn, long t, string name) =>
        (await conn.QueryAsync<(string, string)>(
            "SELECT kind, detail::text FROM agent_evidence WHERE tenant_id = @t AND application_name = @n ORDER BY id",
            new { t, n = name })).ToList();

    /// <summary>A tenant with dry-run off and the long tail on, two packets built and parked in Ready.</summary>
    private async Task<(NpgsqlConnection Conn, long Tenant, CapturingNotifier Notifier)> ReadyTenantAsync(bool dryRun = false)
    {
        var stub = new StubLlmClient(Responders.Agent());
        var notifier = new CapturingNotifier();
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await new AgentSettingsRepo(conn, t).UpsertAsync(new AgentSettings
        {
            Enabled = true, DryRun = dryRun, LongTail = true, MinFitScore = 70, MaxPerRun = 2, MaxPerDay = 3, Phone = "555-0100",
        });
        using (var builder = NewWorker(stub, pg.ConnectionString, notifier))
            await builder.RunOnceAsync(CancellationToken.None);
        var apps = new ApplicationRepo(conn, t);
        foreach (var name in new[] { "high-engineer.md", "mid-engineer.md" })
        {
            var rec = await apps.GetAsync(name);
            await apps.UpdateStructuredAsync(name, rec!.Fields with { Link = $"https://careers.example.com/{name}" }, null);
        }
        return (conn, t, notifier);
    }

    [Fact]
    public async Task A_pass_promotes_ready_packets_whose_last_dry_run_was_clean_once_dry_run_is_off()
    {
        // 23 packets sat in Ready on 2026-09-13: each was dry-run-filled while the switch was
        // on, and nothing revisited them when it flipped (#185). The pass does now.
        var (conn, t, _) = await ReadyTenantAsync(dryRun: false);
        await using var _ = conn;
        var evidence = new AgentEvidenceRepo(conn, t, Protector);
        await evidence.RecordAsync("high-engineer.md", "dry_run", "https://careers.example.com/high-engineer.md", "",
            new { dry_run = true, mapped = new[] { "std:first_name" }, unmapped = Array.Empty<string>(), error = "" }, null);
        await evidence.RecordAsync("mid-engineer.md", "dry_run", "https://careers.example.com/mid-engineer.md", "",
            new { dry_run = true, mapped = new[] { "std:first_name" }, unmapped = new[] { "std:resume" }, error = "" }, null);

        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(), browser: FakeBrowser);
        await worker.RunOnceAsync(CancellationToken.None);

        var queued = (await conn.QueryAsync<(string Name, bool DryRun)>(
            "SELECT application_name, dry_run FROM submit_requests WHERE tenant_id = @t AND done_at IS NULL ORDER BY 1", new { t })).ToList();
        Assert.Equal([("high-engineer.md", false)], queued);

        // Still in dry-run mode, nothing is promoted however clean the evidence.
        await new AgentSettingsRepo(conn, t).UpsertAsync(new AgentSettings { Enabled = true, DryRun = true, LongTail = true, MinFitScore = 70 });
        await conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE tenant_id = @t", new { t });
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, await PendingSubmitsAsync(conn, t));
    }

    [Fact]
    public async Task A_dry_run_that_filled_nothing_is_never_promoted_however_clean_it_reads()
    {
        // #302: two LinkedIn Easy Apply packets looped every ~32 s for hours — "0 mapped,
        // 0 unmapped", read as a clean dry run, promoted to a real run that came back a dry
        // run again. A run that mapped nothing has proved nothing about the form.
        var (conn, t, _) = await ReadyTenantAsync(dryRun: false);
        await using var _ = conn;
        var evidence = new AgentEvidenceRepo(conn, t, Protector);
        await evidence.RecordAsync("high-engineer.md", "dry_run", "https://careers.example.com/high-engineer.md", "",
            new { dry_run = true, mapped = Array.Empty<string>(), unmapped = Array.Empty<string>(), error = "" }, null);

        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(), browser: FakeBrowser);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(await QueuedAsync(conn, t), q => q.Name == "high-engineer.md" && !q.DryRun);
    }

    private static Task<List<(string Name, bool DryRun, bool Prepare)>> QueuedAsync(NpgsqlConnection conn, long t) =>
        conn.QueryAsync<(string, bool, bool)>(
            "SELECT application_name, dry_run, prepare FROM submit_requests WHERE tenant_id = @t AND done_at IS NULL ORDER BY 1",
            new { t }).ContinueWith(r => r.Result.ToList());

    private static Task AgeEvidenceAsync(NpgsqlConnection conn, long t, string interval) =>
        conn.ExecuteAsync($"UPDATE agent_evidence SET created_at = now() - interval '{interval}' WHERE tenant_id = @t", new { t });

    [Fact]
    public async Task A_pass_retries_a_ready_packet_whose_last_dry_run_died_on_something_transient()
    {
        // 33 packets sat in Ready on 2026-09-19 with an empty queue: a run that failed was
        // stamped done and nothing ever looked at it again (#274).
        var (conn, t, _) = await ReadyTenantAsync(dryRun: false);
        await using var _ = conn;
        var evidence = new AgentEvidenceRepo(conn, t, Protector);
        await evidence.RecordAsync("high-engineer.md", "failed", "https://careers.example.com/high-engineer.md", "",
            new { reason = "TimeoutException: Timeout 20000ms exceeded.", dry_run = true }, null);
        // The board may have taken this one. A second application is worse than a parked one.
        await evidence.RecordAsync("mid-engineer.md", "failed", "https://careers.example.com/mid-engineer.md", "",
            new { dry_run = false, error = "Submit was clicked but no confirmation text was recognised" }, null);
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(), browser: FakeBrowser);

        // Too soon: a board that is down stays down for a while.
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(await QueuedAsync(conn, t), q => q.Name is "high-engineer.md" or "mid-engineer.md");

        await AgeEvidenceAsync(conn, t, "7 hours");
        await worker.RunOnceAsync(CancellationToken.None);
        var queued = await QueuedAsync(conn, t);
        Assert.Contains(("high-engineer.md", true, false), queued);
        Assert.DoesNotContain(queued, q => q.Name == "mid-engineer.md");
        Assert.Contains(("high-engineer.md", "requeued"), await EventsAsync(conn, t));
    }

    [Fact]
    public async Task A_pass_stops_retrying_a_ready_packet_that_keeps_failing()
    {
        var (conn, t, _) = await ReadyTenantAsync(dryRun: false);
        await using var _ = conn;
        var evidence = new AgentEvidenceRepo(conn, t, Protector);
        for (var i = 0; i < ReadyReconciler.MaxFailures; i++)
            await evidence.RecordAsync("high-engineer.md", "failed", "https://careers.example.com/high-engineer.md", "",
                new { reason = "PlaywrightException: Target closed", dry_run = true, transient = true }, null);
        await AgeEvidenceAsync(conn, t, "7 hours");
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(), browser: FakeBrowser);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(await QueuedAsync(conn, t), q => q.Name == "high-engineer.md");
    }

    [Fact]
    public async Task A_pass_queues_a_prepare_for_a_ready_application_that_has_no_packet()
    {
        // Ready, version 1, no verdict, no packet, no events: the pass reads leads and the
        // promoter reads evidence, so nothing would ever have picked it up (#274).
        var (conn, t, _) = await ReadyTenantAsync(dryRun: false);
        await using var _ = conn;
        await conn.ExecuteAsync("DELETE FROM agent_packets WHERE tenant_id = @t AND application_name = 'high-engineer.md'", new { t });
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(), browser: FakeBrowser);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Contains(("high-engineer.md", true, true), await QueuedAsync(conn, t));

        // A prepare that failed is not tried again on the very next pass: three model
        // builds in fifteen minutes is not a retry policy.
        await conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE tenant_id = @t", new { t });
        await new AgentEventRepo(conn, t).RecordAsync("error", "high-engineer.md", new { reason = "packet build failed: model down" });
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(await QueuedAsync(conn, t), q => q.Name == "high-engineer.md");
        await conn.ExecuteAsync("UPDATE agent_events SET created_at = now() - interval '7 hours' WHERE tenant_id = @t AND kind = 'error'", new { t });
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Contains(("high-engineer.md", true, true), await QueuedAsync(conn, t));

        // Without a browser there is nowhere to run it, and nothing is queued.
        await conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE tenant_id = @t", new { t });
        using var blind = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier());
        await blind.RunOnceAsync(CancellationToken.None);
        Assert.Empty(await QueuedAsync(conn, t));
    }

    [Fact]
    public async Task A_pass_retires_a_workday_posting_that_no_longer_exists_and_no_other()
    {
        // The browser never opens a Workday link, so nothing ever found one closed (#277). Workday's
        // page is a script under a 200 either way; the JSON behind it answers 404 for a removed job.
        var (conn, t, _) = await ReadyTenantAsync(dryRun: false);
        await using var _ = conn;
        var apps = new ApplicationRepo(conn, t);
        foreach (var (name, link) in new[]
                 {
                     ("high-engineer.md", "https://gone.wd1.myworkdayjobs.com/Careers/job/Remote/Engineer_R1"),
                     ("mid-engineer.md", "https://refuses.wd5.myworkdayjobs.com/Careers/job/Remote/Engineer_R2"),
                 })
        {
            var rec = await apps.GetAsync(name);
            await apps.UpdateStructuredAsync(name, rec!.Fields with { Link = link }, null);
        }
        // As Workday really answers: 404 for a removed job only to a request that asks for JSON;
        // the same address answers text/html with a 406, which says nothing. Live on 1.49.3 the
        // page fetcher's default Accept got exactly that, and retired nothing.
        var handler = new CapturingHandler(req => new HttpResponseMessage(
            !req.Headers.Accept.Any(a => a.MediaType == "application/json") ? HttpStatusCode.NotAcceptable
            : req.RequestUri!.Host.StartsWith("gone.", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden));
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(),
            fetcher: new JobPageFetcher(handler));

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal("passed", await StatusAsync(conn, t, "high-engineer.md"));
        // A tenant that refuses a bot says nothing about the job: never retired on that.
        Assert.Equal("ready", await StatusAsync(conn, t, "mid-engineer.md"));
        Assert.Contains(handler.Requests, r => r.Request.RequestUri!.AbsolutePath.StartsWith("/wday/cxs/gone/Careers/job/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pass_retires_a_dead_end_aggregator_listing_without_a_browser()
    {
        // "Quick Apply" on bestjobtool leads to thebigjobsite behind a bot check and on to
        // more aggregators — never a form. Parked in Ready it would sit there for good (#281).
        var (conn, t, _) = await ReadyTenantAsync(dryRun: false);
        await using var _ = conn;
        var apps = new ApplicationRepo(conn, t);
        var rec = await apps.GetAsync("high-engineer.md");
        await apps.UpdateStructuredAsync("high-engineer.md",
            rec!.Fields with { Link = "https://www.bestjobtool.com/job-description-usb/3D6E?src=LinkedIn" }, null);
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier());

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal("passed", await StatusAsync(conn, t, "high-engineer.md"));
        Assert.Equal("ready", await StatusAsync(conn, t, "mid-engineer.md"));
        Assert.Contains(("high-engineer.md", "error"), await EventsAsync(conn, t));
    }

    [Theory]
    [InlineData("""{"reason":"TimeoutException: Timeout 20000ms exceeded.","dry_run":true}""", true)]
    [InlineData("""{"dry_run":true,"error":"no application form was found on the page — nothing to fill (the Apply button did not respond within 5 s)"}""", true)]
    [InlineData("""{"dry_run":true,"error":"no application form was found on the page — nothing to fill (Apply was clicked but no form appeared within 10 s)"}""", true)]
    [InlineData("""{"reason":"refused to submit: required fields could not be mapped (x)","dry_run":true,"transient":false}""", false)]
    [InlineData("""{"dry_run":true,"error":"no application form was found on the page — nothing to fill (no Apply button or link on the page)"}""", false)]
    [InlineData("""{"captcha":true,"error":"TimeoutException: x","dry_run":true}""", false)]
    [InlineData("""{"reason":"TimeoutException: Timeout 20000ms exceeded.","dry_run":false,"transient":true}""", false)]
    [InlineData("""{"dry_run":true,"error":"Submit was clicked but no confirmation text was recognised"}""", false)]
    public void Only_a_failed_dry_run_that_died_on_something_transient_is_worth_another_try(string detail, bool expected) =>
        Assert.Equal(expected, ReadyReconciler.IsTransientFailure("failed", JsonDocument.Parse(detail).RootElement));

    [Fact]
    public async Task A_run_that_meets_new_questions_gets_them_answered_and_goes_round_again_once()
    {
        // LinkedIn Easy Apply shows its questions only as each step is reached, so the first run
        // can only name them. The packet takes them on, the drafter answers, and the run goes
        // round again — and it cannot loop, because a question is only "discovered" once (#278).
        var (conn, t, notifier) = await ReadyTenantAsync(dryRun: true);
        await using var _ = conn;
        const string question = "How many years of experience do you have with C#?";
        var runs = 0;
        var fake = new FakeSubmitter((link, packet, dry, _, _) =>
        {
            runs++;
            var known = packet.Questions.Any(q => q.Id == question);
            return Task.FromResult(known
                ? FakeSubmitter.Clean(link)
                : new SubmitOutcome(true, false, link, "", null, [question], [], "",
                    Discovered: [new PacketQuestion(question, question, true, PacketQuestion.Text, [], PacketQuestion.Custom)]));
        });
        var llm = new StubLlmClient(Responders.Agent(answersJson: $$"""{"answers":[{"id":"{{question}}","answer":"12"}]}"""));
        // The queue is drained across tenants: settle what other tests left, so the count is this one's.
        await conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE done_at IS NULL AND tenant_id <> @t", new { t });
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true);
        using var worker = NewWorker(llm, pg.ConnectionString, notifier, browser: FakeBrowser, submitter: fake);

        // One drain: the first run names the question, the second finds it answered.
        Assert.Equal(2, await worker.DrainSubmitsAsync(CancellationToken.None));

        Assert.Equal(2, runs);
        var packet = await new AgentPacketRepo(conn, t, Protector).GetAsync("high-engineer.md");
        Assert.Contains(packet!.Questions, q => q.Id == question);
        Assert.Equal("12", packet.Answers[question]);
        Assert.Equal("12", fake.Runs[1].Answers[question]);
        Assert.Equal(0, await PendingSubmitsAsync(conn, t));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_stopped_at_an_account_only_ats_creates_the_account_only_when_the_tenant_allows_it(bool allowed)
    {
        // Workday takes applications only from a signed-in candidate, per employer (#277). With
        // create_accounts on, the account is registered, proved, sealed under Board accounts and
        // recorded, and the run goes round again signed in. Off, nothing is registered anywhere.
        var (conn, t, notifier) = await ReadyTenantAsync(dryRun: true);
        await using var _ = conn;
        var settings = await new AgentSettingsRepo(conn, t).GetAsync();
        settings.CreateAccounts = allowed;
        await new AgentSettingsRepo(conn, t).UpsertAsync(settings);
        const string host = "acme.wd5.myworkdayjobs.com";
        var runs = 0;
        var fake = new FakeSubmitter((link, _, _, _, _) => Task.FromResult(++runs == 1
            ? new SubmitOutcome(false, false, link, "", null, [], [], $"Apply leads to a sign-in at {host} (Workday) and no account is saved for it", NeedsAccountAt: host)
            : FakeSubmitter.Clean(link)));
        var creator = new FakeAccountCreator(h => new AccountCreation(new BoardAccount(h, "ada@example.com", "Gen3rated!pw"), $"created a candidate account at {h}"));
        await conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE done_at IS NULL AND tenant_id <> @t", new { t });
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true);
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, notifier,
            browser: FakeBrowser, submitter: fake, accountCreator: creator);

        await worker.DrainSubmitsAsync(CancellationToken.None);

        var saved = await new BoardAccountRepo(conn, t, Protector, NullLogger.Instance).TargetsAsync();
        if (allowed)
        {
            Assert.Equal([host], creator.Hosts);
            var account = Assert.Single(saved, a => a.Host == host);
            Assert.Equal("Gen3rated!pw", account.Password);
            Assert.Contains(("high-engineer.md", "account_created"), await EventsAsync(conn, t));
            Assert.Equal(2, runs);
        }
        else
        {
            Assert.Empty(creator.Hosts);
            Assert.DoesNotContain(saved, a => a.Host == host);
            Assert.Equal(1, runs);
        }
    }

    [Fact]
    public async Task A_queued_prepare_rebuilds_the_packet_on_the_worker_and_goes_straight_to_the_dry_run()
    {
        // The api container has no browser, so a packet prepared there never sees the form;
        // queued with prepare = true, the worker builds it and runs the fill in one claim (#183).
        var (conn, t, notifier) = await ReadyTenantAsync(dryRun: true);
        await using var _ = conn;
        var before = (await new AgentPacketRepo(conn, t, Protector).GetAsync("high-engineer.md"))!.Version;
        // The account email changes between the build and the run.
        await conn.ExecuteAsync("UPDATE users SET email = 'ada.new@example.com' WHERE id = @t", new { t });
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><form><input name='email'></form></body></html>", System.Text.Encoding.UTF8, "text/html"),
        });
        var fake = new FakeSubmitter((link, _, dry, _, _) => Task.FromResult(dry ? FakeSubmitter.Clean(link) : FakeSubmitter.Submitted(link)));
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true, prepare: true);
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, notifier,
            browser: FakeBrowser, fetcher: new JobPageFetcher(handler), submitter: fake);

        Assert.Equal(1, await worker.DrainSubmitsAsync(CancellationToken.None));

        var packet = await new AgentPacketRepo(conn, t, Protector).GetAsync("high-engineer.md");
        Assert.True(packet!.Version > before, "the packet was rebuilt");
        var run = Assert.Single(fake.Runs);
        Assert.True(run.DryRun);
        Assert.Equal("ada.new@example.com", run.Answers["std:email"]);
        Assert.Equal("dry_run", (await EvidenceAsync(conn, t, "high-engineer.md")).Last().Kind);
        Assert.Equal("ready", await StatusAsync(conn, t, "high-engineer.md"));
        Assert.Contains(notifier.Sent, m => m.Text.Contains("filled in and ready for you to click Apply"));
    }

    [Fact]
    public async Task A_queued_prepare_for_an_aggregator_listing_builds_the_packet_and_moos_without_a_browser_run()
    {
        var (conn, t, notifier) = await ReadyTenantAsync(dryRun: true);
        await using var _ = conn;
        var apps = new ApplicationRepo(conn, t);
        var rec = await apps.GetAsync("high-engineer.md");
        await apps.UpdateStructuredAsync("high-engineer.md",
            rec!.Fields with { Link = "https://remoteok.com/remote-jobs/remote-engineer-acme-1", Source = "auto:remoteok" }, null);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><p>A listing.</p></body></html>", System.Text.Encoding.UTF8, "text/html"),
        });
        var fake = new FakeSubmitter((link, _, _, _, _) => Task.FromResult(FakeSubmitter.Clean(link)));
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true, prepare: true);
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, notifier,
            browser: FakeBrowser, fetcher: new JobPageFetcher(handler), submitter: fake);

        await worker.DrainSubmitsAsync(CancellationToken.None);

        Assert.Empty(fake.Runs);
        Assert.Equal("aggregator", (await new AgentPacketRepo(conn, t, Protector).GetAsync("high-engineer.md"))!.Provider);
        Assert.Contains(notifier.Sent, m => m.Text.Contains("High · Engineer is ready to submit"));
    }

    [Fact]
    public async Task The_standard_answers_are_refreshed_from_the_profile_at_fill_time()
    {
        // When the account email changed, all 35 built packets had to be patched by hand (#189).
        var (conn, t, _) = await ReadyTenantAsync(dryRun: true);
        await using var _ = conn;
        await conn.ExecuteAsync("UPDATE users SET email = 'ada.moved@example.com' WHERE id = @t", new { t });
        await new AgentSettingsRepo(conn, t).UpsertAsync(new AgentSettings { Enabled = true, DryRun = true, LongTail = true, Phone = "555-0199" });
        var fake = new FakeSubmitter((link, _, _, _, _) => Task.FromResult(FakeSubmitter.Clean(link)));
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true);
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(),
            browser: FakeBrowser, submitter: fake);

        await worker.DrainSubmitsAsync(CancellationToken.None);

        var run = Assert.Single(fake.Runs);
        Assert.Equal("ada.moved@example.com", run.Answers["std:email"]);
        Assert.Equal("555-0199", run.Answers["std:phone"]);
        var stored = await new AgentPacketRepo(conn, t, Protector).GetAsync("high-engineer.md");
        Assert.Equal("ada.moved@example.com", stored!.Answers["std:email"]);
    }

    [Fact]
    public async Task The_packets_provider_is_re_read_from_the_link_at_fill_time()
    {
        // A packet built before its board was taught kept saying "unknown", so the browser
        // was driven at the posting page instead of the form, and found none (#203).
        var (conn, t, _) = await ReadyTenantAsync(dryRun: true);
        await using var _ = conn;
        var apps = new ApplicationRepo(conn, t);
        var rec = await apps.GetAsync("high-engineer.md");
        await apps.UpdateStructuredAsync("high-engineer.md",
            rec!.Fields with { Link = "https://jobs.workable.com/view/jiiC71GdyakPxmZCnY85MJ/senior-engineer-at-acme" }, null);
        var packets = new AgentPacketRepo(conn, t, Protector);
        Assert.Equal("unknown", (await packets.GetAsync("high-engineer.md"))!.Provider);
        var fake = new FakeSubmitter((link, _, _, _, _) => Task.FromResult(FakeSubmitter.Clean(link)));
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true);
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, new CapturingNotifier(),
            browser: FakeBrowser, submitter: fake);

        await worker.DrainSubmitsAsync(CancellationToken.None);

        Assert.Single(fake.Runs);
        Assert.Equal("workable", (await packets.GetAsync("high-engineer.md"))!.Provider);
    }

    [Fact]
    public async Task A_dry_run_that_stopped_on_required_questions_moos_what_still_needs_the_person()
    {
        var (conn, t, notifier) = await ReadyTenantAsync(dryRun: true);
        await using var _ = conn;
        var packets = new AgentPacketRepo(conn, t, Protector);
        var packet = (await packets.GetAsync("high-engineer.md"))!;
        packet.Questions.Add(new PacketQuestion("q_salary", "Expected monthly salary", true, PacketQuestion.Text, [], PacketQuestion.Custom));
        packet.NeedsReview.Add(new ReviewItem("q_salary", "salary is yours to state"));
        await packets.UpsertAsync(packet);
        var fake = new FakeSubmitter((link, _, _, _, _) => Task.FromResult(
            new SubmitOutcome(true, false, link, "", null, ["std:resume"], ["std:first_name"], "")));
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: true);
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, notifier, browser: FakeBrowser, submitter: fake);

        await worker.DrainSubmitsAsync(CancellationToken.None);

        // The two Ready moos from the build, then the Filled one that names the questions.
        var moo = Assert.Single(notifier.Sent, m => m.Text.Contains("is filled in"));
        Assert.Contains("2 questions need you: Expected monthly salary, Résumé", moo.Text);
        var detail = (await EvidenceAsync(conn, t, "high-engineer.md")).Last().Detail;
        Assert.Contains("\"needs_you\": [\"Expected monthly salary\", \"Résumé\"]", detail.Replace("\n", "").Replace("  ", " "));
    }

    // -- the parked-on-code path, end to end against the worker (#192) --------------------

    private const string Code = "IEG0PXWR";

    private static FakeSubmitter CodeGate() => new((link, _, dry, awaitCode, ct) =>
        dry ? Task.FromResult(FakeSubmitter.Clean(link)) : RealRunAsync(link, awaitCode!, ct));

    private static async Task<SubmitOutcome> RealRunAsync(string link, Func<CodeRequest, CancellationToken, Task<string?>> awaitCode, CancellationToken ct)
    {
        var code = await awaitCode(new CodeRequest("ada@example.com"), ct);
        if (code == Code) return FakeSubmitter.Submitted(link);
        return new SubmitOutcome(true, false, link, "", null, [], ["std:first_name"],
            code is null
                ? "the board emailed a security code to ada@example.com and none was entered in time — run Submit again and paste the code when it arrives"
                : "the security code was refused");
    }

    private async Task<(NpgsqlConnection Conn, long Tenant, CapturingNotifier Notifier)> ParkedTenantAsync()
    {
        var (conn, t, notifier) = await ReadyTenantAsync(dryRun: false);
        await new SubmitRequestRepo(conn, t).EnqueueAsync("high-engineer.md", dryRun: false);
        return (conn, t, notifier);
    }

    [Fact]
    public async Task The_code_pasted_into_the_app_finishes_the_parked_run_and_the_moo_asked_for_it()
    {
        var (conn, t, notifier) = await ParkedTenantAsync();
        await using var _ = conn;
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, notifier,
            browser: FakeBrowser, submitter: CodeGate(), options: new AgentOptions { Enabled = true, SecurityCodeWaitSeconds = 30 });

        var drain = worker.DrainSubmitsAsync(CancellationToken.None);
        // The person pastes the code a moment after the 🔐 moo.
        await Task.Delay(1500);
        await using (var paste = new NpgsqlConnection(pg.ConnectionString))
        {
            await paste.OpenAsync();
            Assert.True(await new SubmitRequestRepo(paste, t).SetSecurityCodeAsync("high-engineer.md", Code));
        }
        await drain;

        Assert.Equal("applied", await StatusAsync(conn, t, "high-engineer.md"));
        Assert.Equal(["awaiting_code", "submitted"], (await EvidenceAsync(conn, t, "high-engineer.md")).Select(e => e.Kind));
        Assert.Contains(notifier.Sent, m => m.Text.StartsWith("🔐") && m.Text.Contains("ada@example.com"));
        Assert.Contains(notifier.Sent, m => m.Text.StartsWith("✅"));
    }

    [Fact]
    public async Task A_reply_to_the_moo_carries_the_code()
    {
        var (conn, t, notifier) = await ParkedTenantAsync();
        await using var _ = conn;
        // The reply is dated after the moo and comes from the configured chat.
        notifier.Replies.Add(new TelegramReply(7, "4242", DateTimeOffset.UtcNow.AddSeconds(60), "code: " + Code));
        // Noise from another chat, and an earlier message, are never a code.
        notifier.Replies.Add(new TelegramReply(5, "9999", DateTimeOffset.UtcNow.AddSeconds(60), Code));
        notifier.Replies.Add(new TelegramReply(6, "4242", DateTimeOffset.UtcNow.AddMinutes(-10), "ZZZZ9999"));
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, notifier,
            browser: FakeBrowser, submitter: CodeGate(), telegram: notifier,
            options: new AgentOptions { Enabled = true, SecurityCodeWaitSeconds = 30 });

        await worker.DrainSubmitsAsync(CancellationToken.None);

        Assert.Equal("applied", await StatusAsync(conn, t, "high-engineer.md"));
        Assert.NotEmpty(notifier.Offsets);
        Assert.Contains(notifier.Sent, m => m.Text.StartsWith("🔐"));
    }

    [Fact]
    public async Task With_a_mailbox_the_code_is_read_from_it_and_nobody_is_mooed()
    {
        var (conn, t, notifier) = await ParkedTenantAsync();
        await using var _ = conn;
        await new MailboxSettingsRepo(conn, t, Protector, NullLogger<MailboxSettingsRepo>.Instance)
            .UpsertAsync(true, "imap.example", 993, "ada@example.com", true, "app-password");
        var mailbox = new FakeCodeSource(() => Code);
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, notifier,
            browser: FakeBrowser, submitter: CodeGate(), codes: mailbox, telegram: notifier,
            options: new AgentOptions { Enabled = true, SecurityCodeWaitSeconds = 30 });

        await worker.DrainSubmitsAsync(CancellationToken.None);

        Assert.Equal("applied", await StatusAsync(conn, t, "high-engineer.md"));
        Assert.True(mailbox.Calls >= 1);
        Assert.DoesNotContain(notifier.Sent, m => m.Text.StartsWith("🔐"));
        Assert.Contains("\"mailbox\": true", (await EvidenceAsync(conn, t, "high-engineer.md")).First().Detail.Replace("\n", ""));
    }

    [Fact]
    public async Task A_mailbox_that_fails_falls_back_to_the_moo_and_the_reply_still_finishes_the_run()
    {
        var (conn, t, notifier) = await ParkedTenantAsync();
        await using var _ = conn;
        await new MailboxSettingsRepo(conn, t, Protector, NullLogger<MailboxSettingsRepo>.Instance)
            .UpsertAsync(true, "imap.example", 993, "ada@example.com", true, "app-password");
        var mailbox = new FakeCodeSource(() => throw new IOException("connection refused"));
        notifier.Replies.Add(new TelegramReply(1, "4242", DateTimeOffset.UtcNow.AddSeconds(60), Code));
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, notifier,
            browser: FakeBrowser, submitter: CodeGate(), codes: mailbox, telegram: notifier,
            options: new AgentOptions { Enabled = true, SecurityCodeWaitSeconds = 30 });

        await worker.DrainSubmitsAsync(CancellationToken.None);

        Assert.Equal("applied", await StatusAsync(conn, t, "high-engineer.md"));
        Assert.Contains(notifier.Sent, m => m.Text.StartsWith("🔐"));
        var reasons = await conn.QueryAsync<string>(
            "SELECT detail->>'reason' FROM agent_events WHERE tenant_id = @t AND kind = 'error' AND application_name = 'high-engineer.md'", new { t });
        Assert.Contains(reasons, r => r.StartsWith("mailbox: ") && r.Contains("connection refused"));
    }

    [Fact]
    public async Task A_code_that_never_arrives_ends_the_run_at_the_deadline_with_nothing_submitted()
    {
        var (conn, t, notifier) = await ParkedTenantAsync();
        await using var _ = conn;
        using var worker = NewWorker(new StubLlmClient(Responders.Agent()), pg.ConnectionString, notifier,
            browser: FakeBrowser, submitter: CodeGate(), options: new AgentOptions { Enabled = true, SecurityCodeWaitSeconds = 2 });

        var started = DateTime.UtcNow;
        await worker.DrainSubmitsAsync(CancellationToken.None);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "the deadline bounded the wait");
        Assert.Equal("ready", await StatusAsync(conn, t, "high-engineer.md"));
        var last = (await EvidenceAsync(conn, t, "high-engineer.md")).Last();
        Assert.Equal("failed", last.Kind);
        Assert.Contains("none was entered in time", last.Detail);
        Assert.Equal(0, await PendingSubmitsAsync(conn, t));
    }
}
