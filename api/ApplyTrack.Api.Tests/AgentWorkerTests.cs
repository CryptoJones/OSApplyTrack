// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Agent.Greenhouse;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
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
        AgentOptions? options = null)
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
            codes, telegram);
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
