// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Scrape;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// One unattended pass of the worker against the test Postgres: it judges only the
/// tenants that opted in, only leads over the agent's own bar, at most the per-run
/// cap, never the same lead twice — and touches nothing but <c>agent_events</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AgentWorkerTests(PostgresFixture pg)
{
    private const string ProceedJson =
        "{\"decision\":\"proceed\",\"confidence\":90,\"rationale\":\"Fits.\",\"concerns\":[]}";

    private static AgentWorker NewWorker(StubLlmClient stub, string connectionString)
    {
        var evaluator = new LeadEvaluator(
            new FitJudge(new StructuredCompleter(stub)), new JobPageFetcher(),
            NullLogger<LeadEvaluator>.Instance);
        return new AgentWorker(
            connectionString, new AgentOptions { Enabled = true },
            new LlmOptions { BaseUrl = "http://stub/v1", Model = "stub-model" },
            new SecretProtector(null), evaluator, NullLoggerFactory.Instance);
    }

    private async Task<(NpgsqlConnection Conn, long Tenant)> SeedTenantAsync(bool enabled, int minScore = 70)
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var t = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        await new AgentSettingsRepo(conn, t).UpsertAsync(new AgentSettings
        {
            Enabled = enabled, MinFitScore = minScore, MaxPerRun = 2, MaxPerDay = 3,
        });
        await new ResumeRepo(conn, t).UpsertAsync(new Resume { FullName = "Ada Byte", Summary = "Ships .NET." });
        var apps = new ApplicationRepo(conn, t);
        await apps.CreateAsync(new AppFields { Company = "High", Role = "Engineer", Score = "90" });
        await apps.CreateAsync(new AppFields { Company = "Mid", Role = "Engineer", Score = "75" });
        await apps.CreateAsync(new AppFields { Company = "Low", Role = "Engineer", Score = "60" });
        await apps.CreateAsync(new AppFields { Company = "Junk", Role = "Engineer", Score = "high" });
        await apps.CreateAsync(new AppFields { Company = "Applied", Role = "Engineer", Score = "95", Status = "applied" });
        return (conn, t);
    }

    private static Task<List<(string Name, string Kind)>> EventsAsync(NpgsqlConnection conn, long t) =>
        conn.QueryAsync<(string, string)>(
            "SELECT application_name, kind FROM agent_events WHERE tenant_id = @t ORDER BY id", new { t })
            .ContinueWith(x => x.Result.ToList());

    [Fact]
    public async Task A_pass_judges_the_best_unjudged_leads_over_the_bar_up_to_the_run_cap()
    {
        var stub = new StubLlmClient((_, _, _) => ProceedJson);
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await using var _ = conn;
        using var worker = NewWorker(stub, pg.ConnectionString);

        await worker.RunOnceAsync(CancellationToken.None);

        // "Low" is under the bar, "Junk" has no comparable score, "Applied" is not a lead,
        // and MaxPerRun = 2 covers exactly High + Mid.
        var events = await EventsAsync(conn, t);
        Assert.Equal(["high-engineer.md", "mid-engineer.md"], events.Select(e => e.Name).ToArray());
        Assert.All(events, e => Assert.Equal("verdict", e.Kind));

        // A second pass finds nothing new: a verdict is terminal.
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, (await EventsAsync(conn, t)).Count);
        Assert.Equal(2, stub.Calls);

        // Statuses are untouched — this increment stages nothing.
        var statuses = await conn.QueryAsync<string>(
            "SELECT DISTINCT status FROM applications WHERE tenant_id = @t AND company IN ('High','Mid')", new { t });
        Assert.Equal(["lead"], statuses.ToArray());
    }

    [Fact]
    public async Task A_tenant_that_has_not_opted_in_is_never_touched()
    {
        var stub = new StubLlmClient((_, _, _) => ProceedJson);
        var (conn, t) = await SeedTenantAsync(enabled: false);
        await using var _ = conn;
        using var worker = NewWorker(stub, pg.ConnectionString);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Empty(await EventsAsync(conn, t));
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task A_tenant_without_a_model_gets_an_error_row_not_a_verdict()
    {
        var (conn, t) = await SeedTenantAsync(enabled: true);
        await using var _ = conn;
        var stub = new StubLlmClient((_, _, _) => ProceedJson);
        var evaluator = new LeadEvaluator(
            new FitJudge(new StructuredCompleter(stub)), new JobPageFetcher(), NullLogger<LeadEvaluator>.Instance);
        using var worker = new AgentWorker(
            pg.ConnectionString, new AgentOptions { Enabled = true }, new LlmOptions(),
            new SecretProtector(null), evaluator, NullLoggerFactory.Instance);

        await worker.RunOnceAsync(CancellationToken.None);

        var events = await EventsAsync(conn, t);
        Assert.Single(events);
        Assert.Equal("error", events[0].Kind);
        Assert.Equal(0, stub.Calls);
    }
}
