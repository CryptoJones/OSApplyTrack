// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Llm;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The agent routes over HTTP: settings default to OFF and round-trip; the audit
/// trail lists what the agent did; and the on-demand verdict runs the same
/// evaluation the worker would, with a stubbed model, recording proceed / skip /
/// failure exactly as the worker does.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AgentEndpointTests : IAsyncLifetime
{
    private const string Resume =
        """{"full_name":"Ada Byte","headline":"Backend Engineer","summary":"Ships reliable .NET services.","skills":["C#","Postgres"]}""";

    private readonly PostgresFixture _pg;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public AgentEndpointTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        _factory = NewFactory(null);
        _client = await AuthedClientAsync(_factory);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        foreach (var f in _factories)
            await f.DisposeAsync();
    }

    private WebApplicationFactory<Program> NewFactory(Action<IWebHostBuilder>? configure)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            configure?.Invoke(b);
        });
        _factories.Add(f);
        return f;
    }

    private static Action<IWebHostBuilder> WithStub(StubLlmClient stub) => b =>
    {
        b.UseSetting("Llm:BaseUrl", "http://stub/v1");
        b.UseSetting("Llm:Model", "stub-model");
        b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ILlmClient>();
            s.AddSingleton<ILlmClient>(stub);
        });
    };

    private async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> factory)
    {
        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> CreateLeadAsync(HttpClient client, string company = "Acme Corp", string notes = "")
    {
        var res = await client.PostAsync("/api/apps",
            Json($$"""{"company":"{{company}}","role":"Senior .NET Engineer","score":"80","notes":"{{notes}}"}"""));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJson(res)).GetProperty("filename").GetString()!;
    }

    private const string ProceedJson =
        "{\"decision\":\"proceed\",\"confidence\":85,\"rationale\":\"Core .NET requirements are met.\",\"concerns\":[\"confirm the on-call rota\"]}";

    // ---- Settings ---------------------------------------------------------------

    [Fact]
    public async Task Settings_default_to_off_with_a_higher_bar_than_discovery()
    {
        var s = await ReadJson(await _client.GetAsync("/api/agent-settings"));
        Assert.False(s.GetProperty("enabled").GetBoolean());
        Assert.True(s.GetProperty("dry_run").GetBoolean());
        Assert.Equal(70, s.GetProperty("min_fit_score").GetInt32());
        Assert.False(s.GetProperty("worker_running").GetBoolean());
    }

    [Fact]
    public async Task Settings_round_trip_and_clamp()
    {
        var res = await _client.PutAsync("/api/agent-settings", Json(
            """{"enabled":true,"min_fit_score":150,"max_per_run":0,"work_authorization":"US citizen","needs_sponsorship":false,"clearance_ok":true,"phone":"555-0100","junk":1}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var s = await ReadJson(res);
        Assert.True(s.GetProperty("enabled").GetBoolean());
        Assert.Equal(100, s.GetProperty("min_fit_score").GetInt32());
        Assert.Equal(1, s.GetProperty("max_per_run").GetInt32());
        Assert.Equal("US citizen", s.GetProperty("work_authorization").GetString());
        Assert.True(s.GetProperty("clearance_ok").GetBoolean());

        var again = await ReadJson(await _client.GetAsync("/api/agent-settings"));
        Assert.Equal("555-0100", again.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task Settings_are_per_tenant()
    {
        await _client.PutAsync("/api/agent-settings", Json("""{"enabled":true}"""));
        using var other = await AuthedClientAsync(_factory);
        var s = await ReadJson(await other.GetAsync("/api/agent-settings"));
        Assert.False(s.GetProperty("enabled").GetBoolean());
    }

    // ---- Verdict --------------------------------------------------------------

    [Fact]
    public async Task Verdict_refuses_without_an_llm_endpoint()
    {
        var name = await CreateLeadAsync(_client);
        var res = await _client.PostAsync($"/api/apps/{name}/verdict", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("LLM endpoint", (await ReadJson(res)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Verdict_records_proceed_and_surfaces_on_the_app_detail()
    {
        var stub = new StubLlmClient((_, _, _) => ProceedJson);
        using var client = await AuthedClientAsync(NewFactory(WithStub(stub)));
        await client.PutAsync("/api/resume", Json(Resume));
        var name = await CreateLeadAsync(client);

        var res = await client.PostAsync($"/api/apps/{name}/verdict", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var v = (await ReadJson(res)).GetProperty("verdict");
        Assert.Equal("proceed", v.GetProperty("decision").GetString());
        Assert.Equal(85, v.GetProperty("confidence").GetInt32());
        Assert.True(v.GetProperty("model_consulted").GetBoolean());
        Assert.Contains("Ada Byte", stub.LastUserPrompt);   // judged against the tenant's own brief
        Assert.Contains("Acme Corp", stub.LastUserPrompt);

        var detail = await ReadJson(await client.GetAsync($"/api/apps/{name}"));
        var recorded = detail.GetProperty("agent_verdict").GetProperty("detail");
        Assert.Equal("proceed", recorded.GetProperty("decision").GetString());
        Assert.Equal("stub-model", recorded.GetProperty("model").GetString());

        var events = await ReadJson(await client.GetAsync("/api/agent-events"));
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal("verdict", events[0].GetProperty("kind").GetString());
        Assert.Equal(name, events[0].GetProperty("application_name").GetString());
    }

    [Fact]
    public async Task Verdict_repairs_one_bad_reply_and_gives_up_on_the_second()
    {
        var replies = new Queue<string>(["not json", ProceedJson, "still not json", "{\"decision\":\"perhaps\"}"]);
        var stub = new StubLlmClient((_, _, _) => replies.Dequeue());
        using var client = await AuthedClientAsync(NewFactory(WithStub(stub)));
        await client.PutAsync("/api/resume", Json(Resume));
        var name = await CreateLeadAsync(client);

        // First evaluation: bad, then repaired.
        var ok = await client.PostAsync($"/api/apps/{name}/verdict", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(2, stub.Calls);

        // Second evaluation: bad twice -> 502, and an error row rather than a verdict.
        var bad = await client.PostAsync($"/api/apps/{name}/verdict", null);
        Assert.Equal(HttpStatusCode.BadGateway, bad.StatusCode);
        Assert.Equal(4, stub.Calls);

        var events = await ReadJson(await client.GetAsync("/api/agent-events"));
        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal("error", events[0].GetProperty("kind").GetString()); // newest first
        Assert.Contains("usable answer", events[0].GetProperty("detail").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_disqualified_posting_is_skipped_without_spending_a_model_call()
    {
        var stub = new StubLlmClient((_, _, _) => ProceedJson);
        using var client = await AuthedClientAsync(NewFactory(WithStub(stub)));
        await client.PutAsync("/api/resume", Json(Resume));
        var name = await CreateLeadAsync(client, notes: "Must hold an active Top Secret clearance.");

        var res = await client.PostAsync($"/api/apps/{name}/verdict", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var v = (await ReadJson(res)).GetProperty("verdict");
        Assert.Equal("skip", v.GetProperty("decision").GetString());
        Assert.False(v.GetProperty("model_consulted").GetBoolean());
        Assert.Contains("clearance", v.GetProperty("disqualifiers")[0].GetString());
        Assert.Equal(0, stub.Calls);

        // The lead's status is untouched — a skip is terminal for the agent, not the human.
        var detail = await ReadJson(await client.GetAsync($"/api/apps/{name}"));
        Assert.Equal("lead", detail.GetProperty("fields").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Verdict_404s_for_an_unknown_application()
    {
        using var client = await AuthedClientAsync(NewFactory(WithStub(new StubLlmClient())));
        var res = await client.PostAsync("/api/apps/nope.md/verdict", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
