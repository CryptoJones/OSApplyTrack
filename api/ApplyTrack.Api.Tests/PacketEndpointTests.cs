// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The packet over HTTP: prepare-on-demand parks the lead in `ready` with drafted
/// answers and moos once; a skip verdict refuses; answers edit through the
/// <c>?expected_version=</c> 409 contract; the packet rides along on the app detail.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PacketEndpointTests : IAsyncLifetime
{
    private const string Resume =
        """{"full_name":"Ada Byte","headline":"Backend Engineer","summary":"Ships reliable .NET services.","skills":["C#","Postgres"]}""";
    private const string BotToken = "123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef";

    private readonly PostgresFixture _pg;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly CapturingNotifier _notifier = new();

    public PacketEndpointTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var f in _factories)
            await f.DisposeAsync();
    }

    private async Task<HttpClient> ClientAsync(StubLlmClient stub, bool secrets = true)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            b.UseSetting("Llm:BaseUrl", "http://stub/v1");
            b.UseSetting("Llm:Model", "stub-model");
            b.UseSetting("App:PublicBaseUrl", "https://apply.example");
            if (secrets) b.UseSetting("Secrets:Key", "test-master-key");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<ILlmClient>();
                s.AddSingleton<ILlmClient>(stub);
                s.RemoveAll<INotifier>();
                s.AddSingleton<INotifier>(_notifier);
            });
        });
        _factories.Add(f);
        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        await client.PutAsync("/api/resume", Json(Resume));
        await client.PutAsync("/api/agent-settings", Json("""{"phone":"555-0100","work_authorization":"US citizen"}"""));
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> CreateLeadAsync(HttpClient client)
    {
        var res = await client.PostAsync("/api/apps",
            Json("""{"company":"Acme Corp","role":"Senior .NET Engineer","score":"80","link":""}"""));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJson(res)).GetProperty("filename").GetString()!;
    }

    [Fact]
    public async Task Prepare_builds_the_packet_parks_the_lead_in_ready_and_moos_once()
    {
        var client = await ClientAsync(new StubLlmClient(Responders.Agent()));
        await client.PutAsync("/api/notifications",
            Json($$"""{"telegram_enabled":true,"telegram_chat_id":"4242","telegram_bot_token":"{{BotToken}}"}"""));
        var name = await CreateLeadAsync(client);

        var res = await client.PostAsync($"/api/apps/{name}/packet/prepare", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var packet = (await ReadJson(res)).GetProperty("packet");
        Assert.Equal("unknown", packet.GetProperty("provider").GetString());
        Assert.Equal("Ada", packet.GetProperty("answers").GetProperty("std:first_name").GetString());
        Assert.Equal("555-0100", packet.GetProperty("answers").GetProperty("std:phone").GetString());
        Assert.Equal(0, packet.GetProperty("needs_review").GetArrayLength());
        Assert.Equal(7, packet.GetProperty("questions").GetArrayLength());

        var detail = await ReadJson(await client.GetAsync($"/api/apps/{name}"));
        Assert.Equal("ready", detail.GetProperty("fields").GetProperty("status").GetString());
        Assert.Equal("proceed", detail.GetProperty("agent_verdict").GetProperty("detail").GetProperty("decision").GetString());
        Assert.Equal(StubLlmClient.DefaultBody, detail.GetProperty("material").GetString());
        Assert.Equal(1, detail.GetProperty("packet").GetProperty("version").GetInt64());

        var moo = Assert.Single(_notifier.Sent);
        Assert.Contains("Acme Corp · Senior .NET Engineer is ready to submit", moo.Text);
        Assert.Contains($"https://apply.example/#app={name}", moo.Text);

        // Preparing again rebuilds (version bumps, notified_at reset) and moos again —
        // a rebuilt packet is a new packet.
        await client.PostAsync($"/api/apps/{name}/packet/prepare", null);
        Assert.Equal(2, _notifier.Sent.Count);
    }

    [Fact]
    public async Task Prepare_refuses_a_skip_verdict_unless_forced()
    {
        var skip = "{\"decision\":\"skip\",\"confidence\":70,\"rationale\":\"Needs Rust.\",\"concerns\":[]}";
        var client = await ClientAsync(new StubLlmClient(Responders.Agent(verdictJson: skip)));
        var name = await CreateLeadAsync(client);

        var res = await client.PostAsync($"/api/apps/{name}/packet/prepare", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Needs Rust", (await ReadJson(res)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/apps/{name}/packet")).StatusCode);
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task Answers_edit_through_the_expected_version_409_contract()
    {
        var client = await ClientAsync(new StubLlmClient(Responders.Agent()));
        var name = await CreateLeadAsync(client);
        await client.PostAsync($"/api/apps/{name}/packet/prepare", null);
        var packet = await ReadJson(await client.GetAsync($"/api/apps/{name}/packet"));
        var version = packet.GetProperty("version").GetInt64();

        var ok = await client.PutAsync($"/api/apps/{name}/packet?expected_version={version}",
            Json("""{"answers":{"std:first_name":"Augusta","std:phone":"","junk":"x"}}"""));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var saved = await ReadJson(ok);
        Assert.Equal("Augusta", saved.GetProperty("answers").GetProperty("std:first_name").GetString());
        Assert.False(saved.GetProperty("answers").TryGetProperty("junk", out _));
        Assert.Equal(version + 1, saved.GetProperty("version").GetInt64());
        // Phone is optional, so a blank does not block; nothing else went blank.
        Assert.Equal(0, saved.GetProperty("needs_review").GetArrayLength());

        var stale = await client.PutAsync($"/api/apps/{name}/packet?expected_version={version}",
            Json("""{"answers":{"std:first_name":"Stale"}}"""));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // A required question blanked out is flagged; keys not sent are kept (merge).
        var blanked = await ReadJson(await client.PutAsync($"/api/apps/{name}/packet",
            Json("""{"answers":{"std:first_name":""}}""")));
        var review = blanked.GetProperty("needs_review");
        Assert.Equal(["std:first_name"], review.EnumerateArray().Select(r => r.GetProperty("id").GetString() ?? "").ToArray());
        Assert.Equal("Byte", blanked.GetProperty("answers").GetProperty("std:last_name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/apps/{name}/packet")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/apps/{name}/packet")).StatusCode);
    }

    [Fact]
    public async Task Prepare_refuses_without_an_llm_endpoint()
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));
        _factories.Add(f);
        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        var name = await CreateLeadAsync(client);
        var res = await client.PostAsync($"/api/apps/{name}/packet/prepare", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
