// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Llm;
using Dapper;
using Npgsql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// <c>GET /api/pipeline</c> (#211): the submit queue in the worker's claim order, each
/// request labelled with what the worker will do when it claims it — the tenant's
/// dry-run switch wins over the request, a real click needs nothing left to review —
/// and the Ready packets a dry-run flip would promote.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PipelineEndpointTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
    }

    private async Task<(HttpClient Client, long Tenant)> ClientAsync()
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", pg.ConnectionString);
            b.UseSetting("Llm:BaseUrl", "http://stub/v1");
            b.UseSetting("Llm:Model", "stub-model");
            b.UseSetting("Browser:Endpoint", "ws://127.0.0.1:1/");
            b.UseSetting("Browser:AllowPrivateTargets", "true");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<ILlmClient>();
                s.AddSingleton<ILlmClient>(new StubLlmClient(Responders.Agent()));
            });
        });
        _factories.Add(f);
        var (tenant, sid) = await TestAuth.SeedSessionAsync(pg.ConnectionString);
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        await client.PutAsync("/api/resume", Json("""{"full_name":"Ada Byte","summary":"Ships .NET."}"""));
        return (client, tenant);
    }

    /// <summary>Prepare with a browser on this instance queues a rebuild-then-dry-run row per
    /// packet; finish those so a Submit click queues a fresh row rather than answering "already queued".</summary>
    private async Task SettleQueueAsync(long tenant)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE tenant_id = @t", new { t = tenant });
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> PreparedLeadAsync(HttpClient client, string company)
    {
        var res = await client.PostAsync("/api/apps",
            Json($$"""{"company":"{{company}}","role":"Engineer","score":"80","link":"https://jobs.lever.co/{{company.ToLowerInvariant()}}/1234-abcd"}"""));
        var name = (await ReadJson(res)).GetProperty("filename").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/apps/{name}/packet/prepare", null)).StatusCode);
        return name;
    }

    [Fact]
    public async Task An_empty_queue_reads_as_empty_with_the_agents_switches()
    {
        var (client, _) = await ClientAsync();
        var body = await ReadJson(await client.GetAsync("/api/pipeline"));

        Assert.True(body.GetProperty("allowed").GetBoolean());
        Assert.True(body.GetProperty("dry_run").GetBoolean());
        Assert.True(body.GetProperty("browser_available").GetBoolean());
        Assert.Equal(0, body.GetProperty("queue").GetArrayLength());
        Assert.Equal(0, body.GetProperty("summary").GetProperty("queued").GetInt32());
    }

    [Fact]
    public async Task Queued_requests_come_back_oldest_first_labelled_with_what_the_worker_will_do()
    {
        var (client, tenant) = await ClientAsync();
        var first = await PreparedLeadAsync(client, "Acme");
        var second = await PreparedLeadAsync(client, "Globex");

        // Prepare, with a browser here, built each packet in-process and queued its dry run.
        var body = await ReadJson(await client.GetAsync("/api/pipeline"));
        Assert.All(body.GetProperty("queue").EnumerateArray(), q => Assert.Equal("dry_run", q.GetProperty("will").GetString()));
        Assert.Equal(2, body.GetProperty("summary").GetProperty("dry_runs").GetInt32());

        // Then both are queued as the human's Submit click; the tenant's switch still says dry run.
        await SettleQueueAsync(tenant);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/apps/{first}/submit", Json("""{"dry_run":false}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/apps/{second}/submit", Json("""{"dry_run":false}"""))).StatusCode);

        body = await ReadJson(await client.GetAsync("/api/pipeline"));
        var queue = body.GetProperty("queue").EnumerateArray().ToList();
        Assert.Equal([first, second], queue.Select(q => q.GetProperty("name").GetString()));
        Assert.Equal([1, 2], queue.Select(q => q.GetProperty("position").GetInt32()));
        Assert.All(queue, q =>
        {
            Assert.Equal("queued", q.GetProperty("phase").GetString());
            Assert.Equal("dry_run", q.GetProperty("will").GetString());
            Assert.Contains("Dry run only", q.GetProperty("reason").GetString());
            Assert.Equal("lever", q.GetProperty("provider").GetString());
        });
        Assert.Equal("Acme", queue[0].GetProperty("company").GetString());
        Assert.Equal(2, body.GetProperty("summary").GetProperty("dry_runs").GetInt32());
        Assert.Equal(0, body.GetProperty("summary").GetProperty("will_submit").GetInt32());

        // With the switch off, the rows keep their own dry-run flag (they were queued as dry
        // runs) — but a clean run now queues the real click next, and the view says so.
        await client.PutAsync("/api/agent-settings", Json("""{"dry_run":false}"""));
        body = await ReadJson(await client.GetAsync("/api/pipeline"));
        Assert.False(body.GetProperty("dry_run").GetBoolean());
        Assert.All(body.GetProperty("queue").EnumerateArray(), q =>
        {
            Assert.Equal("dry_run", q.GetProperty("will").GetString());
            Assert.True(q.GetProperty("then_submit").GetBoolean());
        });

        // A fresh click with the switch off is the real thing.
        await SettleQueueAsync(tenant);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/apps/{first}/submit", Json("""{"dry_run":false}"""))).StatusCode);
        body = await ReadJson(await client.GetAsync("/api/pipeline"));
        var real = Assert.Single(body.GetProperty("queue").EnumerateArray());
        Assert.Equal("submit", real.GetProperty("will").GetString());
        Assert.Equal("", real.GetProperty("reason").GetString());
        Assert.Equal(1, body.GetProperty("summary").GetProperty("will_submit").GetInt32());
    }

    [Fact]
    public async Task A_ready_packet_that_is_not_queued_is_listed_with_what_holds_it()
    {
        var (client, _) = await ClientAsync();
        var name = await PreparedLeadAsync(client, "Initech");
        // Prepare with a browser on this instance queues a dry run; the packet sits in Ready.
        var body = await ReadJson(await client.GetAsync("/api/pipeline"));
        var queued = body.GetProperty("queue").EnumerateArray().Select(q => q.GetProperty("name").GetString()).ToList();
        Assert.Contains(name, queued);
        // Nothing has run, so there is no evidence and it is not in the Ready list either.
        Assert.DoesNotContain(name, body.GetProperty("ready").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        Assert.Equal("ready", body.GetProperty("queue").EnumerateArray().First(q => q.GetProperty("name").GetString() == name).GetProperty("status").GetString());
    }
}
