// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The Ready lane in bulk (#186): one request prepares, submits or passes many packets,
/// each refusal named rather than failing the batch; <c>all_clean</c> queues every
/// packet whose last dry run was clean (#185) and refuses while dry-run is still on.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReadyEndpointTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
    }

    private async Task<(HttpClient Client, long Tenant)> ClientAsync(bool browser = true)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", pg.ConnectionString);
            b.UseSetting("Llm:BaseUrl", "http://stub/v1");
            b.UseSetting("Llm:Model", "stub-model");
            if (browser)
            {
                b.UseSetting("Browser:Endpoint", "ws://127.0.0.1:1/");
                b.UseSetting("Browser:AllowPrivateTargets", "true");
            }
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

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>Prepare queues a dry run per packet; finish those so the bulk actions queue fresh rows.</summary>
    private async Task SettleQueueAsync(long tenant)
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE tenant_id = @t", new { t = tenant });
    }

    private async Task<List<(string Name, bool DryRun, bool Prepare)>> PendingAsync(long tenant)
    {
        await using var conn = await OpenAsync();
        return (await conn.QueryAsync<(string, bool, bool)>(
            "SELECT application_name, dry_run, prepare FROM submit_requests WHERE tenant_id = @t AND done_at IS NULL ORDER BY 1",
            new { t = tenant })).ToList();
    }

    [Fact]
    public async Task Pass_retires_the_selection_and_names_what_it_could_not()
    {
        var (client, _) = await ClientAsync(browser: false);
        var a = await PreparedLeadAsync(client, "Acme");
        var b = await PreparedLeadAsync(client, "Globex");

        var res = await client.PostAsync("/api/ready/actions", Json($$"""{"action":"pass","names":["{{a}}","{{b}}","nobody.md"]}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal([a, b], body.GetProperty("done").EnumerateArray().Select(e => e.GetString()));
        var skipped = Assert.Single(body.GetProperty("skipped").EnumerateArray());
        Assert.Equal(("nobody.md", "not found"), (skipped.GetProperty("name").GetString(), skipped.GetProperty("reason").GetString()));
        Assert.Equal("passed", (await ReadJson(await client.GetAsync($"/api/apps/{a}"))).GetProperty("fields").GetProperty("status").GetString());

        // Passing again is a no-op with a reason, not an error.
        var again = await ReadJson(await client.PostAsync("/api/ready/actions", Json($$"""{"action":"pass","names":["{{a}}"]}""")));
        Assert.Equal(0, again.GetProperty("done").GetArrayLength());
        Assert.Equal("already passed", again.GetProperty("skipped")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Prepare_and_submit_queue_the_batch_in_one_request()
    {
        var (client, tenant) = await ClientAsync();
        var a = await PreparedLeadAsync(client, "Acme");
        var b = await PreparedLeadAsync(client, "Globex");
        await SettleQueueAsync(tenant);

        var prep = await client.PostAsync("/api/ready/actions", Json($$"""{"action":"prepare","names":["{{a}}","{{b}}"]}"""));
        Assert.Equal(HttpStatusCode.Accepted, prep.StatusCode);
        Assert.Equal([(a, true, true), (b, true, true)], await PendingAsync(tenant));
        // Already queued is a reason, not a second row.
        var again = await ReadJson(await client.PostAsync("/api/ready/actions", Json($$"""{"action":"prepare","names":["{{a}}"]}""")));
        Assert.Equal("already queued", again.GetProperty("skipped")[0].GetProperty("reason").GetString());
        await SettleQueueAsync(tenant);

        // Dry-run mode is on, so a bulk Submit queues dry runs and says so.
        var sub = await client.PostAsync("/api/ready/actions", Json($$"""{"action":"submit","names":["{{a}}","{{b}}","nobody.md"]}"""));
        Assert.Equal(HttpStatusCode.Accepted, sub.StatusCode);
        var body = await ReadJson(sub);
        Assert.True(body.GetProperty("dry_run").GetBoolean());
        Assert.Equal(2, body.GetProperty("done").GetArrayLength());
        Assert.Equal("not found", body.GetProperty("skipped")[0].GetProperty("reason").GetString());
        Assert.Equal([(a, true, false), (b, true, false)], await PendingAsync(tenant));
    }

    [Fact]
    public async Task Submit_all_clean_queues_the_real_click_for_every_clean_dry_run_once_dry_run_is_off()
    {
        var (client, tenant) = await ClientAsync();
        var a = await PreparedLeadAsync(client, "Acme");
        var b = await PreparedLeadAsync(client, "Globex");
        var c = await PreparedLeadAsync(client, "Initech");
        await SettleQueueAsync(tenant);

        // Refused while dry-run is still on.
        var refused = await client.PostAsync("/api/ready/actions", Json("""{"action":"submit","all_clean":true}"""));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Dry run only", (await ReadJson(refused)).GetProperty("detail").GetString());

        await client.PutAsync("/api/agent-settings", Json("""{"dry_run":false}"""));
        await using (var conn = await OpenAsync())
        {
            var evidence = new AgentEvidenceRepo(conn, tenant, TestAuth.Protector);
            // a: clean. b: a required field unmapped. c: clean once, but the latest run failed.
            await evidence.RecordAsync(a, "dry_run", "https://x", "", new { dry_run = true, unmapped = Array.Empty<string>(), error = "" }, null);
            await evidence.RecordAsync(b, "dry_run", "https://x", "", new { dry_run = true, unmapped = new[] { "resume" }, error = "" }, null);
            await evidence.RecordAsync(c, "dry_run", "https://x", "", new { dry_run = true, unmapped = Array.Empty<string>(), error = "" }, null);
            await evidence.RecordAsync(c, "failed", "https://x", "", new { reason = "boom" }, null);
        }

        var res = await client.PostAsync("/api/ready/actions", Json("""{"action":"submit","all_clean":true}"""));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var body = await ReadJson(res);
        Assert.False(body.GetProperty("dry_run").GetBoolean());
        Assert.Equal([a], body.GetProperty("done").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([(a, false, false)], await PendingAsync(tenant));
    }

    [Fact]
    public async Task Queueing_actions_need_a_browser_and_a_known_action()
    {
        await using (var conn = await OpenAsync())
            await conn.ExecuteAsync("UPDATE agent_workers SET seen_at = now() - interval '1 hour'");
        var (client, _) = await ClientAsync(browser: false);
        var a = await PreparedLeadAsync(client, "Acme");

        var noBrowser = await client.PostAsync("/api/ready/actions", Json($$"""{"action":"submit","names":["{{a}}"]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, noBrowser.StatusCode);
        Assert.Contains("no browser", (await ReadJson(noBrowser)).GetProperty("detail").GetString());

        var junk = await client.PostAsync("/api/ready/actions", Json($$"""{"action":"launch","names":["{{a}}"]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);

        var empty = await client.PostAsync("/api/ready/actions", Json("""{"action":"pass","names":[]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }
}
