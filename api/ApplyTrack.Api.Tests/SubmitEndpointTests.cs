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
/// The Submit click as a queue row: refused without a browser or a packet, a dry run
/// by default, a double-click is a no-op, the real thing only with nothing left to
/// review — and the queue's claim/complete cycle. Evidence lists and serves screenshots.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SubmitEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public SubmitEndpointTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
    }

    private async Task<(HttpClient Client, long Tenant)> ClientAsync(bool browser = true)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            b.UseSetting("Llm:BaseUrl", "http://stub/v1");
            b.UseSetting("Llm:Model", "stub-model");
            if (browser)
            {
                // A browser that isn't there: discovery fails fast and falls back to the
                // standard form; queueing is what's under test. No DNS pre-flight either.
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
        var (tenant, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        await client.PutAsync("/api/resume", Json("""{"full_name":"Ada Byte","summary":"Ships .NET."}"""));
        return (client, tenant);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    // A Lever posting: an ATS the browser may drive, so a build queues a dry run.
    private static async Task<string> PreparedLeadAsync(HttpClient client)
    {
        var res = await client.PostAsync("/api/apps",
            Json("""{"company":"Acme","role":"Engineer","score":"80","link":"https://jobs.lever.co/acme/1234-abcd"}"""));
        var name = (await ReadJson(res)).GetProperty("filename").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/apps/{name}/packet/prepare", null)).StatusCode);
        return name;
    }

    [Fact]
    public async Task Submit_is_refused_without_a_browser_or_a_packet()
    {
        var (noBrowser, _) = await ClientAsync(browser: false);
        var res = await noBrowser.PostAsync("/api/apps",
            Json("""{"company":"Acme","role":"Engineer","link":"https://example.com/jobs/1"}"""));
        var name = (await ReadJson(res)).GetProperty("filename").GetString()!;
        var refused = await noBrowser.PostAsync($"/api/apps/{name}/submit", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Copy answers", (await ReadJson(refused)).GetProperty("detail").GetString());

        var (client, _) = await ClientAsync();
        res = await client.PostAsync("/api/apps",
            Json("""{"company":"Acme","role":"Engineer","link":"https://example.com/jobs/1"}"""));
        name = (await ReadJson(res)).GetProperty("filename").GetString()!;
        var noPacket = await client.PostAsync($"/api/apps/{name}/submit", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, noPacket.StatusCode);
        Assert.Contains("prepare the packet", (await ReadJson(noPacket)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Prepare_with_a_browser_queues_a_dry_run_instead_of_mooing_and_submit_is_a_dry_run_by_default()
    {
        var (client, tenant) = await ClientAsync();
        var name = await PreparedLeadAsync(client);

        // The build queued a dry run (and the moo waits for it).
        var pending = await ReadJson(await client.GetAsync($"/api/apps/{name}/submit"));
        Assert.True(pending.GetProperty("pending").GetBoolean());
        Assert.True(pending.GetProperty("dry_run").GetBoolean());

        // A click while one is queued is a no-op (200, queued:false), not a second row.
        var again = await client.PostAsync($"/api/apps/{name}/submit", Json("""{"dry_run":false}"""));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.False((await ReadJson(again)).GetProperty("queued").GetBoolean());

        // Drain it as the worker would (the queue is global, so claim past other
        // tenants' rows); then a real submit can be queued — but the tenant's dry_run
        // switch (default ON) still turns it into a dry run.
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        var claimed = await DrainForAsync(conn, tenant);
        Assert.NotNull(claimed);
        Assert.True(claimed!.DryRun);
        Assert.Null(await DrainForAsync(conn, tenant));

        var real = await client.PostAsync($"/api/apps/{name}/submit", Json("""{"dry_run":false}"""));
        Assert.Equal(HttpStatusCode.Accepted, real.StatusCode);
        Assert.True((await ReadJson(real)).GetProperty("dry_run").GetBoolean());

        // Turn dry-run off: now a real submit queues as real — unless answers need review.
        Assert.NotNull(await DrainForAsync(conn, tenant));
        await client.PutAsync("/api/agent-settings", Json("""{"dry_run":false}"""));
        var live = await ReadJson(await client.PostAsync($"/api/apps/{name}/submit", Json("""{"dry_run":false}""")));
        Assert.False(live.GetProperty("dry_run").GetBoolean());
        Assert.False((await DrainForAsync(conn, tenant))!.DryRun);
        await client.PutAsync($"/api/apps/{name}/packet", Json("""{"answers":{"std:first_name":""}}"""));
        var blocked = await client.PostAsync($"/api/apps/{name}/submit", Json("""{"dry_run":false}"""));
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("still need you", (await ReadJson(blocked)).GetProperty("detail").GetString());
    }

    /// <summary>Claim and complete requests until one for <paramref name="tenant"/> turns up (or none are left).</summary>
    private static async Task<SubmitRequest?> DrainForAsync(NpgsqlConnection conn, long tenant)
    {
        while (await SubmitQueue.ClaimNextAsync(conn) is { } req)
        {
            await SubmitQueue.CompleteAsync(conn, req.Id);
            if (req.TenantId == tenant) return req;
        }
        return null;
    }

    [Fact]
    public async Task An_unknown_ats_is_not_auto_queued_and_submit_is_refused_until_the_long_tail_is_on_and_workday_never()
    {
        var (client, _) = await ClientAsync();
        var res = await client.PostAsync("/api/apps",
            Json("""{"company":"Acme","role":"Engineer","score":"80","link":"https://careers.acme.example/jobs/1"}"""));
        var name = (await ReadJson(res)).GetProperty("filename").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/apps/{name}/packet/prepare", null)).StatusCode);
        Assert.False((await ReadJson(await client.GetAsync($"/api/apps/{name}/submit"))).GetProperty("pending").GetBoolean());

        var refused = await client.PostAsync($"/api/apps/{name}/submit", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("long tail", (await ReadJson(refused)).GetProperty("detail").GetString());

        await client.PutAsync("/api/agent-settings", Json("""{"long_tail":true}"""));
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/apps/{name}/submit", Json("{}"))).StatusCode);

        res = await client.PostAsync("/api/apps",
            Json("""{"company":"Wayne","role":"Engineer","score":"80","link":"https://wayne.wd5.myworkdayjobs.com/en-US/careers/job/1"}"""));
        var workday = (await ReadJson(res)).GetProperty("filename").GetString()!;
        await client.PostAsync($"/api/apps/{workday}/packet/prepare", null);
        var never = await client.PostAsync($"/api/apps/{workday}/submit", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, never.StatusCode);
        Assert.Contains("Workday", (await ReadJson(never)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Evidence_lists_metadata_and_serves_the_screenshot()
    {
        var (client, tenant) = await ClientAsync();
        var name = await PreparedLeadAsync(client);
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        var repo = new AgentEvidenceRepo(conn, tenant, TestAuth.Protector);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var id = await repo.RecordAsync(name, AgentEvidenceRepo.Kinds.DryRun, "https://example.com/jobs/1", "",
            new { mapped = 5 }, png);

        var list = await ReadJson(await client.GetAsync($"/api/apps/{name}/evidence"));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("dry_run", list[0].GetProperty("kind").GetString());
        Assert.True(list[0].GetProperty("has_screenshot").GetBoolean());
        Assert.False(list[0].TryGetProperty("screenshot", out _));

        var shot = await client.GetAsync($"/api/apps/{name}/evidence/{id}/screenshot.png");
        Assert.Equal(HttpStatusCode.OK, shot.StatusCode);
        Assert.Equal("image/png", shot.Content.Headers.ContentType!.MediaType);
        Assert.Equal(png, await shot.Content.ReadAsByteArrayAsync());

        // Another tenant sees nothing.
        var (other, _) = await ClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/apps/{name}/evidence/{id}/screenshot.png")).StatusCode);
    }

    [Fact]
    public async Task The_resume_pdf_is_kept_for_the_browser()
    {
        var (client, tenant) = await ClientAsync();
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        var resumes = new ResumeRepo(conn, tenant, TestAuth.Protector);
        Assert.Null(await resumes.GetPdfAsync());
        await resumes.StorePdfAsync([1, 2, 3], "ada.pdf");
        var pdf = await resumes.GetPdfAsync();
        Assert.Equal([1, 2, 3], pdf!.Value.Bytes);
        Assert.Equal("ada.pdf", pdf.Value.Name);
        // The profile upsert leaves the file alone.
        await resumes.UpsertAsync(new Resume { FullName = "Ada" });
        Assert.NotNull(await resumes.GetPdfAsync());
    }

    [Fact]
    public void The_grants_script_is_a_run_always_script_not_a_versioned_migration()
    {
        var names = typeof(Migrator).Assembly.GetManifestResourceNames();
        Assert.Contains(names, n => n.EndsWith(".Migrations.Always.agent_role.sql"));
        Assert.True(Migrator.VersionedScriptCount() >= 22);
    }
}
