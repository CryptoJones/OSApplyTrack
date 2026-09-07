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
        BrowserOptions? browser = null)
    {
        var evaluator = new LeadEvaluator(
            new FitJudge(new StructuredCompleter(stub)), new JobPageFetcher(),
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
            connectionString, new AgentOptions { Enabled = true },
            llm ?? new LlmOptions { BaseUrl = "http://stub/v1", Model = "stub-model" },
            Protector, evaluator, builder, ready, browserOptions,
            new BrowserSubmitter(browserOptions, NullLogger<BrowserSubmitter>.Instance), NullLoggerFactory.Instance);
    }

    private async Task<(NpgsqlConnection Conn, long Tenant)> SeedTenantAsync(bool enabled, bool telegram = true)
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var t = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        await new AgentSettingsRepo(conn, t).UpsertAsync(new AgentSettings
        {
            Enabled = enabled, MinFitScore = 70, MaxPerRun = 2, MaxPerDay = 3, Phone = "555-0100",
        });
        await new ResumeRepo(conn, t).UpsertAsync(new Resume { FullName = "Ada Byte", Summary = "Ships .NET." });
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

        var packet = await new AgentPacketRepo(conn, t).GetAsync("high-engineer.md");
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
        Assert.Null(await new AgentPacketRepo(conn, t).GetAsync("high-engineer.md"));
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
        var packet = await new AgentPacketRepo(conn, t).GetAsync("high-engineer.md");
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
}
