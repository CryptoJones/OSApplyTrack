// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Agent.Greenhouse;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Materials;
using ApplyTrack.Api.Scrape;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The answer bank: a packet build files every screening question with the answer it
/// got; a person's edit is the answer from then on and survives rebuilds; the endpoints
/// list, set, reset and forget.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AnswerBankTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly SecretProtector Protector = new("test-master-key");
    private static readonly EffectiveLlmConfig Cfg = new("http://local/v1", "test-model", null, 30);
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
    }

    private static readonly PacketQuestion Salary =
        new("question_9", "Salary Requirements", true, PacketQuestion.Text, [], PacketQuestion.Custom);
    private static readonly PacketQuestion Why =
        new("question_3", "Describe a system you scaled.", true, PacketQuestion.Textarea, [], PacketQuestion.Custom);
    private static readonly PacketQuestion Name =
        new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard);
    private static readonly PacketQuestion Gender =
        new("gender", "Gender", false, PacketQuestion.Select, ["Decline"], PacketQuestion.Eeo);

    [Fact]
    public void The_key_is_the_prompt_as_asked_with_the_helper_text_folded_in()
    {
        Assert.Equal("salary requirements", AnswerBankRepo.KeyFor("  Salary Requirements* "));
        Assert.Equal("salary expectation per month in eur",
            AnswerBankRepo.KeyFor(new PacketQuestion("q", "Salary expectation", true, PacketQuestion.Text, [], PacketQuestion.Custom) { Help = "per month, in EUR" }));
        Assert.NotEqual(AnswerBankRepo.KeyFor("Salary expectation"), AnswerBankRepo.KeyFor("Salary expectation (per month, in EUR)"));
        Assert.True(AnswerBankRepo.Bankable(Salary));
        Assert.True(AnswerBankRepo.Bankable(Name));      // the two name fields are the exception (#200)
        Assert.Equal(AnswerBankRepo.FirstNameKey, AnswerBankRepo.KeyFor(Name));
        Assert.False(AnswerBankRepo.Bankable(Gender));
        Assert.False(AnswerBankRepo.Bankable(new PacketQuestion("email", "Email", true, PacketQuestion.Text, [], PacketQuestion.Standard)));
    }

    [Fact]
    public async Task A_build_files_custom_questions_and_a_persons_answer_survives_the_next_build()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var t = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        var bank = new AnswerBankRepo(conn, t, Protector);

        await bank.RecordAsync([Name, Salary, Why, Gender], new Dictionary<string, string>
        {
            ["first_name"] = "Ada", ["question_9"] = "125000", ["gender"] = "Decline",
        }, "acme-engineer.md");

        var entries = await bank.ListAsync();
        Assert.Equal(["describe a system you scaled", "first name", "salary requirements"], entries.Select(e => e.Key).Order().ToArray());
        // The name row is filed under its own label, not the form's, and listed first.
        Assert.Equal("first name", entries[0].Key);
        Assert.Equal("First name", entries[0].Label);
        Assert.Equal("Ada", entries[0].Answer);
        var salary = entries.Single(e => e.Key == "salary requirements");
        Assert.Equal("125000", salary.Answer);
        Assert.Equal(AnswerBankEntry.Agent, salary.Source);
        Assert.Equal(1, salary.TimesSeen);
        Assert.Equal("acme-engineer.md", salary.FirstApplication);
        Assert.Equal("", entries.Single(e => e.Key == "describe a system you scaled").Answer);
        // Sealed at rest: the figure is not in the row.
        var raw = await conn.ExecuteScalarAsync<string>(
            "SELECT answer_ciphertext FROM answer_bank WHERE tenant_id = @t AND key = 'salary requirements'", new { t });
        Assert.DoesNotContain("125000", raw);
        Assert.Empty(await bank.PinnedAsync());

        // The person corrects the salary; the agent's next build refreshes the other answer only.
        Assert.True(await bank.SetAsync("Salary Requirements", "140,000 USD"));
        await bank.RecordAsync([Salary, Why], new Dictionary<string, string>
        {
            ["question_9"] = "125000", ["question_3"] = "I scaled billing.",
        }, "globex-engineer.md");
        entries = await bank.ListAsync();
        salary = entries.Single(e => e.Key == "salary requirements");
        Assert.Equal("140,000 USD", salary.Answer);
        Assert.Equal(AnswerBankEntry.Human, salary.Source);
        Assert.Equal(2, salary.TimesSeen);
        Assert.Equal("acme-engineer.md", salary.FirstApplication);
        Assert.Equal("I scaled billing.", entries.Single(e => e.Key == "describe a system you scaled").Answer);
        Assert.Equal(new Dictionary<string, string> { ["salary requirements"] = "140,000 USD" }, await bank.PinnedAsync());

        // Blank hands it back to the agent; a forgotten question is gone until asked again.
        Assert.True(await bank.SetAsync("salary requirements", ""));
        Assert.Equal(AnswerBankEntry.Agent, (await bank.GetAsync("salary requirements"))!.Source);
        Assert.Empty(await bank.PinnedAsync());
        Assert.True(await bank.DeleteAsync("salary requirements"));
        Assert.False(await bank.DeleteAsync("salary requirements"));
        Assert.Null(await bank.GetAsync("salary requirements"));
    }

    [Fact]
    public async Task The_packet_builder_reads_the_bank_first_and_writes_every_screening_question_back()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var t = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        var apps = new ApplicationRepo(conn, t);
        var name = await apps.CreateAsync(new AppFields
        {
            Company = "Acme", Role = "Engineer", Score = "90", Link = "https://job-boards.greenhouse.io/acme/jobs/4001",
        });
        var bank = new AnswerBankRepo(conn, t, Protector);
        var stub = new StubLlmClient(Responders.Agent(answersJson:
            "{\"answers\":[{\"id\":\"question_3\",\"answer\":\"MODEL DRAFT\"}]}"));
        var evaluator = new LeadEvaluator(new FitJudge(new StructuredCompleter(stub)),
            new JobPageFetcher(CapturingHandler.Always(HttpStatusCode.NotFound, "")), NullLogger<LeadEvaluator>.Instance);
        var greenhouse = new GreenhouseBoard(
            new StubHttpClientFactory(CapturingHandler.Always(HttpStatusCode.OK, GreenhouseBoardTests.JobJson), new Uri("https://boards-api.greenhouse.io/")),
            NullLogger<GreenhouseBoard>.Instance);
        var browser = new BrowserOptions();
        var builder = new PacketBuilder(greenhouse, new AnswerDrafter(new StructuredCompleter(stub)),
            new CoverLetterDrafter(stub), evaluator, browser,
            new FormDiscoverer(browser, NullLogger<FormDiscoverer>.Instance), NullLogger<PacketBuilder>.Instance);
        var scope = new PacketScope(apps, new CoverLetterRepo(conn, t, Protector),
            new AgentPacketRepo(conn, t, Protector), new AgentEventRepo(conn, t), bank);
        var inputs = new PacketInputs(
            new Resume { FullName = "Ada Byte", Summary = "Ships .NET.", Location = "Lincoln, NE" },
            new AgentSettings { WorkAuthorization = "US citizen", Phone = "555-0100" },
            "ada@example.com", "", false, Cfg);
        var verdict = new Verdict(Verdict.Proceed, 90, "Fits.", [], [], true);
        var rec = (await apps.GetAsync(name))!;

        var first = await builder.BuildAsync(rec, verdict, inputs, scope);
        Assert.Equal("MODEL DRAFT", first.Answers["question_3"]);
        var keys = (await bank.ListAsync()).Select(e => e.Key).Order().ToArray();
        // The three custom questions and the two name fields; the other standard fields
        // and the hidden tracker never.
        Assert.Equal(["are you legally authorized to work in the united states", "describe a system you scaled", "first name", "last name", "linkedin profile"], keys);
        Assert.Equal("Byte", (await bank.GetAsync(AnswerBankRepo.LastNameKey))!.Answer);
        Assert.Equal("MODEL DRAFT", (await bank.GetAsync("describe a system you scaled"))!.Answer);

        await bank.SetAsync("Describe a system you scaled.", "By hand: sharded the ledger.");
        var before = stub.Calls;
        var second = await builder.BuildAsync((await apps.GetAsync(name))!, verdict, inputs, scope);
        Assert.Equal("By hand: sharded the ledger.", second.Answers["question_3"]);
        Assert.Equal(before, stub.Calls);                     // nothing left for the model to draft
        Assert.Equal(2, (await bank.GetAsync("describe a system you scaled"))!.TimesSeen);
        Assert.Equal("By hand: sharded the ledger.", (await bank.GetAsync("describe a system you scaled"))!.Answer);
    }

    [Fact]
    public async Task The_endpoints_list_set_reset_and_forget()
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", pg.ConnectionString);
            b.UseSetting("Secrets:Key", "test-master-key");
        });
        _factories.Add(f);
        var (tenant, sid) = await TestAuth.SeedSessionAsync(pg.ConnectionString);
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await new AnswerBankRepo(conn, tenant, Protector).RecordAsync([Salary, Why],
            new Dictionary<string, string> { ["question_9"] = "125000" }, "acme-engineer.md");

        var list = JsonDocument.Parse(await (await client.GetAsync("/api/answers")).Content.ReadAsStringAsync()).RootElement;
        // The two banked questions, plus the name rows the listing seeds (#200) — blank
        // here, with no résumé to split, and listed first.
        Assert.Equal(4, list.GetArrayLength());
        var rows = list.EnumerateArray().ToList();
        Assert.Equal(["first name", "last name"], rows.Take(2).Select(e => e.GetProperty("key").GetString()).ToArray());
        Assert.Equal("First name", rows[0].GetProperty("label").GetString());
        Assert.Equal("", rows[0].GetProperty("answer").GetString());
        Assert.Equal("agent", rows[0].GetProperty("source").GetString());
        Assert.Equal(0, rows[0].GetProperty("times_seen").GetInt32());
        var salary = list.EnumerateArray().Single(e => e.GetProperty("key").GetString() == "salary requirements");
        Assert.Equal("125000", salary.GetProperty("answer").GetString());
        Assert.Equal("agent", salary.GetProperty("source").GetString());
        Assert.Equal("Salary Requirements", salary.GetProperty("label").GetString());

        var put = await client.PutAsync("/api/answers/" + Uri.EscapeDataString("salary requirements"),
            new StringContent("""{"answer":" 140,000 USD "}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("140,000 USD", updated.GetProperty("answer").GetString());
        Assert.Equal("human", updated.GetProperty("source").GetString());

        var reset = await client.PutAsync("/api/answers/" + Uri.EscapeDataString("salary requirements"),
            new StringContent("""{"answer":""}""", Encoding.UTF8, "application/json"));
        Assert.Equal("agent", JsonDocument.Parse(await reset.Content.ReadAsStringAsync()).RootElement.GetProperty("source").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync("/api/answers/never-asked",
            new StringContent("""{"answer":"x"}""", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/answers/" + Uri.EscapeDataString("salary requirements"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/answers/" + Uri.EscapeDataString("salary requirements"))).StatusCode);
        Assert.Equal(3, JsonDocument.Parse(await (await client.GetAsync("/api/answers")).Content.ReadAsStringAsync()).RootElement.GetArrayLength());

        // Another tenant sees nothing of it — only its own seeded name rows.
        var (_, other) = await TestAuth.SeedSessionAsync(pg.ConnectionString);
        var stranger = f.CreateClient();
        stranger.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={other}");
        var strangers = JsonDocument.Parse(await (await stranger.GetAsync("/api/answers")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["first name", "last name"], strangers.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray());
    }

    [Fact]
    public async Task The_name_rows_follow_the_resume_until_the_person_pins_one()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var t = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        var bank = new AnswerBankRepo(conn, t, Protector);

        await bank.SeedNamesAsync("Aaron", "Clark");
        var last = (await bank.GetAsync(AnswerBankRepo.LastNameKey))!;
        Assert.Equal("Clark", last.Answer);
        Assert.Equal(AnswerBankEntry.Agent, last.Source);
        Assert.Equal(0, last.TimesSeen);
        Assert.Empty(await bank.PinnedAsync());

        // The résumé changes: the agent's row follows it.
        await bank.SeedNamesAsync("Aaron", "Clarke");
        Assert.Equal("Clarke", (await bank.GetAsync(AnswerBankRepo.LastNameKey))!.Answer);

        // The person pins the last name; the résumé no longer moves it, and the drafter is told.
        Assert.True(await bank.SetAsync(AnswerBankRepo.LastNameKey, "Clark-Jones"));
        await bank.SeedNamesAsync("Aaron", "Clarke");
        last = (await bank.GetAsync(AnswerBankRepo.LastNameKey))!;
        Assert.Equal("Clark-Jones", last.Answer);
        Assert.Equal(AnswerBankEntry.Human, last.Source);
        Assert.Equal(new Dictionary<string, string> { [AnswerBankRepo.LastNameKey] = "Clark-Jones" }, await bank.PinnedAsync());

        // A form asking "Surname" is a sighting of the same row; the pin still stands.
        var surname = new PacketQuestion("q_2", "Surname", true, PacketQuestion.Text, [], PacketQuestion.Custom);
        await bank.RecordAsync([surname], new Dictionary<string, string> { ["q_2"] = "Clark-Jones" }, "acme-engineer.md");
        last = (await bank.GetAsync(AnswerBankRepo.LastNameKey))!;
        Assert.Equal("Last name", last.Label);
        Assert.Equal(1, last.TimesSeen);
        Assert.Equal("Clark-Jones", last.Answer);

        // Handed back to the agent, it follows the résumé again.
        Assert.True(await bank.SetAsync(AnswerBankRepo.LastNameKey, ""));
        await bank.SeedNamesAsync("Aaron", "Clarke");
        Assert.Equal("Clarke", (await bank.GetAsync(AnswerBankRepo.LastNameKey))!.Answer);
    }

    [Fact]
    public async Task A_saved_answer_is_written_into_every_ready_packet_that_asks_the_question()
    {
        // Saving an answer used to change the future only; every built packet kept the old
        // one, and re-preparing cost a model call per packet (#189).
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", pg.ConnectionString);
            b.UseSetting("Secrets:Key", "test-master-key");
        });
        _factories.Add(f);
        var (tenant, sid) = await TestAuth.SeedSessionAsync(pg.ConnectionString);
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var apps = new ApplicationRepo(conn, tenant);
        var packets = new AgentPacketRepo(conn, tenant, Protector);
        var choice = new PacketQuestion("q_5", "Salary Requirements", true, PacketQuestion.Select, ["Under 100k", "100k–150k"], PacketQuestion.Custom);
        async Task<string> ReadyPacketAsync(string company, string status, PacketQuestion q)
        {
            var name = await apps.CreateAsync(new AppFields { Company = company, Role = "Engineer", Status = status });
            await packets.UpsertAsync(new AgentPacket
            {
                ApplicationName = name, Provider = "greenhouse", Questions = [Name, q],
                Answers = new() { ["first_name"] = "Ada", [q.Id] = q == Salary ? "125000" : "" },
            });
            return name;
        }
        var ready = await ReadyPacketAsync("Acme", "ready", Salary);
        var other = await ReadyPacketAsync("Globex", "ready", Why);
        var picked = await ReadyPacketAsync("Initech", "ready", choice);
        var applied = await ReadyPacketAsync("Umbrella", "applied", Salary);
        await new AnswerBankRepo(conn, tenant, Protector).RecordAsync([Salary], new Dictionary<string, string> { ["question_9"] = "125000" }, ready);

        // Only the person's own answer is applied.
        var notMine = await client.PostAsync("/api/answers/" + Uri.EscapeDataString("salary requirements") + "/apply", null);
        Assert.Equal(HttpStatusCode.BadRequest, notMine.StatusCode);

        await client.PutAsync("/api/answers/" + Uri.EscapeDataString("salary requirements"),
            new StringContent("""{"answer":"140000"}""", Encoding.UTF8, "application/json"));
        var res = await client.PostAsync("/api/answers/" + Uri.EscapeDataString("salary requirements") + "/apply", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, body.GetProperty("updated").GetInt32());
        Assert.Equal([ready], body.GetProperty("packets").EnumerateArray().Select(e => e.GetString()));

        Assert.Equal("140000", (await packets.GetAsync(ready))!.Answers["question_9"]);
        Assert.Equal(2, (await packets.GetAsync(ready))!.Version);
        Assert.Equal("125000", (await packets.GetAsync(applied))!.Answers["question_9"]); // not in Ready
        Assert.Equal("", (await packets.GetAsync(picked))!.Answers["q_5"]);              // "140000" is not one of its options
        Assert.False((await packets.GetAsync(other))!.Answers.ContainsKey("question_9"));  // never asked it
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/answers/never-asked/apply", null)).StatusCode);
    }
}
